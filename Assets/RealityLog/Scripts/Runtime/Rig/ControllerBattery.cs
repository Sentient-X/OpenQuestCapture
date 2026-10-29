# nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.XR;
using RealityLog.Common;

namespace RealityLog.Rig
{
    /// <summary>
    /// The Touch controllers' charge. On Horizon OS 207 under OpenXR (Meta XR SDK 81) every app
    /// API answers nothing, so each read takes (f) the OS remote-service report first
    /// (<see cref="OsRemoteServiceBattery"/>: <c>dumpsys OVRRemoteService</c>'s text, read off the
    /// main thread once a minute and on a wake, needing the DUMP grant), then Unity XR's
    /// <c>CommonUsages.batteryLevel</c>, then OVRInput. Once at startup and each time a controller
    /// wakes, every app-level source this build can reach is also probed and its answer logged once
    /// under <c>[RealityLog] ControllerBattery:</c>: (e) Unity XR batteryLevel and the device's
    /// feature usages, (a) Android's InputDevice.getBatteryState (API 31+), (b) OVRInput and
    /// OVRPlugin's controller state for each mask, and (c) the OpenXR runtime's extensions naming
    /// a battery or power. (d) No public Meta broadcast or content provider for controller charge
    /// is documented; none is probed. Main thread only.
    /// </summary>
    public sealed class ControllerBattery
    {
        private const float FirstProbeSeconds = 5f;

        private readonly List<UnityEngine.XR.InputDevice> devices = new();
        private readonly OsRemoteServiceBattery os = new();
        private bool startupProbed;
        private bool leftAwake;
        private bool rightAwake;

        /// <summary>Reads both controllers now; probes and logs every source at startup and on a wake.</summary>
        public ControllerCharge Read()
        {
            var fromOs = os.Read();
            var left = XrController(InputDeviceCharacteristics.Left);
            var right = XrController(InputDeviceCharacteristics.Right);
#pragma warning disable CS0618 // marked obsolete for OpenXR, where it may answer 0: the fallback only
            var ovrLeft = OVRInput.GetControllerBatteryPercentRemaining(OVRInput.Controller.LTouch);
            var ovrRight = OVRInput.GetControllerBatteryPercentRemaining(OVRInput.Controller.RTouch);
#pragma warning restore CS0618
            var leftConnected = OVRInput.IsControllerConnected(OVRInput.Controller.LTouch);
            var rightConnected = OVRInput.IsControllerConnected(OVRInput.Controller.RTouch);
            // The OS report holds a charge only for a controller it lists connected.
            var charge = new ControllerCharge(
                ControllerCharge.First(fromOs.Left, ControllerCharge.First(ControllerCharge.FromLevel(left.ok, left.level), ControllerCharge.Reading(leftConnected, ovrLeft))),
                ControllerCharge.First(fromOs.Right, ControllerCharge.First(ControllerCharge.FromLevel(right.ok, right.level), ControllerCharge.Reading(rightConnected, ovrRight))),
                leftConnected || fromOs.Left != null,
                rightConnected || fromOs.Right != null);

            var leftNow = left.found && (left.tracked || leftConnected);
            var rightNow = right.found && (right.tracked || rightConnected);
            if (!startupProbed && Time.realtimeSinceStartup >= FirstProbeSeconds)
            {
                startupProbed = true;
                Probe("startup");
            }
            else if ((leftNow && !leftAwake) || (rightNow && !rightAwake))
            {
                os.Wake();
                Probe(leftNow && !leftAwake ? "left controller woke" : "right controller woke");
            }
            leftAwake = leftNow;
            rightAwake = rightNow;
            return charge;
        }

        private (bool found, bool tracked, bool ok, float level) XrController(InputDeviceCharacteristics side)
        {
            InputDevices.GetDevicesWithCharacteristics(InputDeviceCharacteristics.Controller | side, devices);
            foreach (var device in devices)
            {
                device.TryGetFeatureValue(CommonUsages.isTracked, out var tracked);
                var ok = device.TryGetFeatureValue(CommonUsages.batteryLevel, out var level);
                return (true, tracked, ok, level);
            }
            return (false, false, false, 0f);
        }

        /// <summary>Every source, logged once for this reason.</summary>
        private void Probe(string reason)
        {
            Log($"probing every source ({reason})");
            Safe("(e) Unity XR", ProbeXr);
            Safe("(a) Android InputDevice", ProbeAndroidInputDevices);
            Safe("(b) OVRInput/OVRPlugin", ProbeOvr);
            Safe("(c) OpenXR extensions", ProbeOpenXrExtensions);
            Log("(d) no public Meta broadcast or content provider for controller charge is documented; none probed");
        }

        private void ProbeXr()
        {
            foreach (var side in new[] { InputDeviceCharacteristics.Left, InputDeviceCharacteristics.Right })
            {
                InputDevices.GetDevicesWithCharacteristics(InputDeviceCharacteristics.Controller | side, devices);
                if (devices.Count == 0) Log($"(e) Unity XR {side}: no controller device");
                foreach (var device in devices)
                {
                    device.TryGetFeatureValue(CommonUsages.isTracked, out var tracked);
                    var ok = device.TryGetFeatureValue(CommonUsages.batteryLevel, out var level);
                    var usages = new List<InputFeatureUsage>();
                    device.TryGetFeatureUsages(usages);
                    var names = string.Join(", ", usages.Select(u => u.name + ":" + u.type.Name));
                    Log($"(e) Unity XR {side} '{device.name}' ({device.manufacturer}, {device.characteristics}) tracked={tracked} "
                        + $"batteryLevel ok={ok} value={level:0.###} hasBatteryUsage={usages.Any(u => u.name == CommonUsages.batteryLevel.name)} usages=[{names}]");
                }
            }
        }

        private static void ProbeAndroidInputDevices()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            using var version = new AndroidJavaClass("android.os.Build$VERSION");
            var sdk = version.GetStatic<int>("SDK_INT");
            using var inputDevice = new AndroidJavaClass("android.view.InputDevice");
            var ids = inputDevice.CallStatic<int[]>("getDeviceIds");
            Log($"(a) Android InputDevice: API {sdk}, {ids.Length} device(s)");
            foreach (var id in ids)
            {
                using var device = inputDevice.CallStatic<AndroidJavaObject>("getDevice", id);
                if (device == null) continue;
                var name = device.Call<string>("getName") ?? "";
                var sources = device.Call<int>("getSources");
                var external = device.Call<bool>("isExternal");
                var line = $"(a) Android InputDevice {id} '{name}' sources=0x{sources:X} external={external}";
                if (sdk >= 31)
                {
                    using var battery = device.Call<AndroidJavaObject>("getBatteryState");
                    if (battery == null)
                    {
                        line += " getBatteryState=null";
                    }
                    else
                    {
                        var present = battery.Call<bool>("isPresent");
                        var capacity = battery.Call<float>("getCapacity");
                        var status = battery.Call<int>("getStatus");
                        line += $" battery present={present} capacity={capacity:0.###} status={status}";
                    }
                }
                Log(line);
            }
#else
            Log("(a) Android InputDevice: not on a device");
#endif
        }

        private static void ProbeOvr()
        {
            Log($"(b) OVRInput connected={OVRInput.GetConnectedControllers()} active={OVRInput.GetActiveController()}");
#pragma warning disable CS0618 // marked obsolete for OpenXR; probed to see what this runtime answers
            foreach (var mask in new[] { OVRInput.Controller.LTouch, OVRInput.Controller.RTouch, OVRInput.Controller.Touch, OVRInput.Controller.Active })
            {
                Log($"(b) OVRInput.GetControllerBatteryPercentRemaining({mask}) = {OVRInput.GetControllerBatteryPercentRemaining(mask)}");
            }
            foreach (var mask in new[] { OVRPlugin.Controller.LTouch, OVRPlugin.Controller.RTouch, OVRPlugin.Controller.Touch })
            {
                var state = OVRPlugin.GetControllerState6((uint)mask);
                Log($"(b) OVRPlugin.GetControllerState6({mask}): connected={state.ConnectedControllers} L={state.LBatteryPercentRemaining} R={state.RBatteryPercentRemaining}");
            }
#pragma warning restore CS0618
        }

        private static void ProbeOpenXrExtensions()
        {
            var available = UnityEngine.XR.OpenXR.OpenXRRuntime.GetAvailableExtensions() ?? Array.Empty<string>();
            var enabled = UnityEngine.XR.OpenXR.OpenXRRuntime.GetEnabledExtensions() ?? Array.Empty<string>();
            bool Wanted(string name) => name.IndexOf("batter", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("power", StringComparison.OrdinalIgnoreCase) >= 0;
            Log($"(c) OpenXR runtime '{UnityEngine.XR.OpenXR.OpenXRRuntime.name}' {UnityEngine.XR.OpenXR.OpenXRRuntime.version}: "
                + $"{available.Length} available, {enabled.Length} enabled; naming battery or power: available=[{string.Join(", ", available.Where(Wanted))}] "
                + $"enabled=[{string.Join(", ", enabled.Where(Wanted))}]");
        }

        private static void Safe(string what, Action probe)
        {
            try
            {
                probe();
            }
            catch (Exception ex)
            {
                Log($"{what}: threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void Log(string line) => Debug.Log($"[{Constants.LOG_TAG}] ControllerBattery: {line}");
    }
}
