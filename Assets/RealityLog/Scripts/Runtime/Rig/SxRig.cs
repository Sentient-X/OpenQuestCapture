# nullable enable

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace RealityLog.Rig
{
    /// <summary>
    /// Return codes of <c>sx_rig.h</c> (the C ABI of libsx_rig.so). Values are the ABI's.
    /// </summary>
    public enum SxRigError
    {
        Ok = 0,
        Argument = -1,
        State = -2,
        Device = -3,
        Storage = -4,
        Buffer = -5,
        Internal = -6,
    }

    /// <summary>
    /// A failed libsx_rig call: the ABI return code plus the library's
    /// <c>sx_rig_last_error</c> JSON (<c>{"code": "...", "detail": "..."}</c>), verbatim.
    /// </summary>
    public class SxRigException : Exception
    {
        public SxRigError Error { get; }
        public string Function { get; }
        public string ErrorJson { get; }

        public SxRigException(string function, SxRigError error, string errorJson)
            : base($"{function} failed: {error} {errorJson}")
        {
            Function = function;
            Error = error;
            ErrorJson = errorJson;
        }
    }

    /// <summary>libsx_rig.so is not in this build (or not loadable on this platform).</summary>
    public sealed class SxRigLibraryMissingException : SxRigException
    {
        public SxRigLibraryMissingException(string function, string cause)
            : base(function, SxRigError.Internal,
                "{\"code\":\"library_missing\",\"detail\":\"" + JsonEscape(cause) + "\"}")
        {
        }

        private static string JsonEscape(string s) =>
            s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
    }

    /// <summary>
    /// The one binding of <c>sx_rig.h</c>
    /// (sx: <c>experience/data-factory/quest-rig/include/sx_rig.h</c>). Every ABI change is an
    /// edit to this file only. JSON goes in and out as strings; strings cross as NUL-terminated
    /// UTF-8; an out-buffer call retries once at the size the library asks for.
    /// Thread-safe as the ABI is: every call may come from any thread.
    /// No UnityEngine dependency, so the binding is exercised on the host against the stub
    /// (compile with <c>SX_RIG_HOST_TEST</c>).
    /// </summary>
    public static class SxRig
    {
        private const string Library = "sx_rig"; // libsx_rig.so in Plugins/Android/libs/arm64-v8a
        private const int InitialBufferBytes = 4096;
        private const int EnrollBufferBytes = 65536; // a Factory pod record; avoids a second network call

#if (UNITY_ANDROID && !UNITY_EDITOR) || SX_RIG_HOST_TEST
        /// <summary>True on platforms where libsx_rig.so can exist (the headset).</summary>
        public const bool PlatformSupported = true;

        [DllImport(Library, EntryPoint = "sx_rig_open")]
        private static extern int NativeOpen(byte[] configJson);

        [DllImport(Library, EntryPoint = "sx_rig_enroll")]
        private static extern int NativeEnroll(byte[] joinCode, byte[]? outJson, UIntPtr cap, out UIntPtr needed);

        [DllImport(Library, EntryPoint = "sx_rig_enroll_site")]
        private static extern int NativeEnrollSite(byte[] requestJson, byte[]? outJson, UIntPtr cap, out UIntPtr needed);

        [DllImport(Library, EntryPoint = "sx_rig_api_token")]
        private static extern int NativeApiToken(byte[]? outJson, UIntPtr cap, out UIntPtr needed);

        [DllImport(Library, EntryPoint = "sx_rig_sign_in")]
        private static extern int NativeSignIn(byte[]? outJson, UIntPtr cap, out UIntPtr needed);

        [DllImport(Library, EntryPoint = "sx_rig_sign_out")]
        private static extern int NativeSignOut();

        [DllImport(Library, EntryPoint = "sx_rig_command")]
        private static extern int NativeCommand(byte[] commandJson, byte[]? outJson, UIntPtr cap, out UIntPtr needed);

        [DllImport(Library, EntryPoint = "sx_rig_report_device")]
        private static extern int NativeReportDevice(byte[] deviceJson);

        [DllImport(Library, EntryPoint = "sx_rig_attach_usb")]
        private static extern int NativeAttachUsb(int fd, ushort vendorId, ushort productId, byte[] deviceName);

        [DllImport(Library, EntryPoint = "sx_rig_detach_usb")]
        private static extern int NativeDetachUsb(byte[] deviceName);

        [DllImport(Library, EntryPoint = "sx_rig_check")]
        private static extern int NativeCheck(byte[]? outJson, UIntPtr cap, out UIntPtr needed);

        [DllImport(Library, EntryPoint = "sx_rig_start")]
        private static extern int NativeStart(byte[] episodeJson, long monotonicAnchorNs, long utcAnchorNs);

        [DllImport(Library, EntryPoint = "sx_rig_start_armed")]
        private static extern int NativeStartArmed(byte[] episodeJson, byte[]? outJson, UIntPtr cap, out UIntPtr needed);

        [DllImport(Library, EntryPoint = "sx_rig_stop")]
        private static extern int NativeStop(byte[]? outJson, UIntPtr cap, out UIntPtr needed);

        [DllImport(Library, EntryPoint = "sx_rig_status")]
        private static extern int NativeStatus(byte[]? outJson, UIntPtr cap, out UIntPtr needed);

        [DllImport(Library, EntryPoint = "sx_rig_last_error")]
        private static extern int NativeLastError(byte[]? outJson, UIntPtr cap, out UIntPtr needed);

        [DllImport(Library, EntryPoint = "sx_rig_close")]
        private static extern void NativeClose();
#else
        public const bool PlatformSupported = false;

        private const string NotOnThisPlatform = "libsx_rig.so exists only in the Android player";
        private static int NativeOpen(byte[] configJson) => throw new DllNotFoundException(NotOnThisPlatform);
        private static int NativeEnroll(byte[] joinCode, byte[]? outJson, UIntPtr cap, out UIntPtr needed) =>
            throw new DllNotFoundException(NotOnThisPlatform);
        private static int NativeEnrollSite(byte[] requestJson, byte[]? outJson, UIntPtr cap, out UIntPtr needed) =>
            throw new DllNotFoundException(NotOnThisPlatform);
        private static int NativeApiToken(byte[]? outJson, UIntPtr cap, out UIntPtr needed) =>
            throw new DllNotFoundException(NotOnThisPlatform);
        private static int NativeSignIn(byte[]? outJson, UIntPtr cap, out UIntPtr needed) =>
            throw new DllNotFoundException(NotOnThisPlatform);
        private static int NativeSignOut() => throw new DllNotFoundException(NotOnThisPlatform);
        private static int NativeCommand(byte[] commandJson, byte[]? outJson, UIntPtr cap, out UIntPtr needed) =>
            throw new DllNotFoundException(NotOnThisPlatform);
        private static int NativeReportDevice(byte[] deviceJson) => throw new DllNotFoundException(NotOnThisPlatform);
        private static int NativeAttachUsb(int fd, ushort vendorId, ushort productId, byte[] deviceName) =>
            throw new DllNotFoundException(NotOnThisPlatform);
        private static int NativeDetachUsb(byte[] deviceName) => throw new DllNotFoundException(NotOnThisPlatform);
        private static int NativeCheck(byte[]? outJson, UIntPtr cap, out UIntPtr needed) =>
            throw new DllNotFoundException(NotOnThisPlatform);
        private static int NativeStart(byte[] episodeJson, long monotonicAnchorNs, long utcAnchorNs) =>
            throw new DllNotFoundException(NotOnThisPlatform);
        private static int NativeStartArmed(byte[] episodeJson, byte[]? outJson, UIntPtr cap, out UIntPtr needed) =>
            throw new DllNotFoundException(NotOnThisPlatform);
        private static int NativeStop(byte[]? outJson, UIntPtr cap, out UIntPtr needed) =>
            throw new DllNotFoundException(NotOnThisPlatform);
        private static int NativeStatus(byte[]? outJson, UIntPtr cap, out UIntPtr needed) =>
            throw new DllNotFoundException(NotOnThisPlatform);
        private static int NativeLastError(byte[]? outJson, UIntPtr cap, out UIntPtr needed) =>
            throw new DllNotFoundException(NotOnThisPlatform);
        private static void NativeClose() => throw new DllNotFoundException(NotOnThisPlatform);
#endif

        private delegate int JsonOut(byte[]? outJson, UIntPtr cap, out UIntPtr needed);

        /// <summary><c>sx_rig_open</c>: <c>{"staging_dir": "...", "rig": {...}}</c>.</summary>
        public static void Open(string configJson) =>
            Check("sx_rig_open", () => NativeOpen(Utf8(configJson)));

        /// <summary>
        /// <c>sx_rig_enroll</c>: redeem a join code (<c>sxj_...</c>) and commission this headset
        /// as a Factory pod; with a credential already held, re-assert the commission and ignore
        /// the code. Returns <c>{"pod_id": ..., "pod": {...}}</c>. Network-bound, up to 60 s;
        /// never call it on Unity's main thread. A retry (undersized buffer) only re-asserts.
        /// </summary>
        public static string Enroll(string joinCode)
        {
            var code = Utf8(joinCode);
            return CallJson("sx_rig_enroll",
                (byte[]? outJson, UIntPtr cap, out UIntPtr needed) => NativeEnroll(code, outJson, cap, out needed),
                EnrollBufferBytes);
        }

        /// <summary>
        /// <c>sx_rig_enroll_site</c>: enroll at a scanned site.
        /// <c>{"factory_url", "auth_url", "join_code": "sxj_...", "rig_type_id"?}</c> →
        /// <c>{"pod_id", "pod", "steps": [{"step", "state", "detail"}]}</c>. A failed join or
        /// commission throws (SX_RIG_E_DEVICE; the error's refusal
        /// <c>{"kind": "enrollment_failed", "steps"}</c>). Network-bound: never on Unity's main
        /// thread. A buffer retry repeats nothing the rig did not already keep.
        /// </summary>
        public static string EnrollSite(string requestJson)
        {
            var request = Utf8(requestJson);
            return CallJson("sx_rig_enroll_site",
                (byte[]? outJson, UIntPtr cap, out UIntPtr needed) => NativeEnrollSite(request, outJson, cap, out needed),
                EnrollBufferBytes);
        }

        /// <summary>
        /// <c>sx_rig_api_token</c>: <c>{"token": "<64 hex>"}</c>, derived from the machine
        /// credential; throws (SX_RIG_E_STATE) before enrollment. Never log it.
        /// </summary>
        public static string ApiToken() => CallJson("sx_rig_api_token", NativeApiToken);

        /// <summary>
        /// <c>sx_rig_sign_in</c>: start the collector's device sign-in. Returns
        /// <c>{"user_code", "verification_uri_complete", "expires_in"}</c>; the rig polls for the
        /// approval in the background and <c>floor.operator.signed_in</c> in the status turns true.
        /// Network-bound: not on Unity's main thread.
        /// </summary>
        public static string SignIn() => CallJson("sx_rig_sign_in", NativeSignIn);

        /// <summary><c>sx_rig_sign_out</c>: sign the collector out.</summary>
        public static void SignOut() => Check("sx_rig_sign_out", NativeSignOut);

        /// <summary>
        /// <c>sx_rig_command</c>: a collector command on the open collection
        /// (<c>review_success</c>, <c>review_failure</c>, <c>end_collection</c>, <c>finalize</c>,
        /// <c>close_failed_collection</c>). Returns <c>{"answer", "floor"}</c>. Network-bound.
        /// The JSON should carry a <c>command_id</c> (UUID, the idempotency key): the buffer
        /// retry resends the same bytes, so it is the same command.
        /// </summary>
        public static string Command(string commandJson)
        {
            var command = Utf8(commandJson);
            return CallJson("sx_rig_command",
                (byte[]? outJson, UIntPtr cap, out UIntPtr needed) => NativeCommand(command, outJson, cap, out needed),
                EnrollBufferBytes);
        }

        /// <summary>
        /// <c>sx_rig_report_device</c>: <c>{"battery_percent": 0-100, "charging": bool,
        /// "network_time_synchronized": bool}</c>. The heartbeat trusts it for 60 s.
        /// </summary>
        public static void ReportDevice(string deviceJson) =>
            Check("sx_rig_report_device", () => NativeReportDevice(Utf8(deviceJson)));

        /// <summary>
        /// <c>sx_rig_attach_usb</c>. The fd stays owned by the caller (Kotlin's
        /// UsbDeviceConnection) until <see cref="DetachUsb"/> returns.
        /// </summary>
        public static void AttachUsb(int fd, ushort vendorId, ushort productId, string deviceName) =>
            Check("sx_rig_attach_usb", () => NativeAttachUsb(fd, vendorId, productId, Utf8(deviceName)));

        /// <summary><c>sx_rig_detach_usb</c> by the device name given at attach.</summary>
        public static void DetachUsb(string deviceName) =>
            Check("sx_rig_detach_usb", () => NativeDetachUsb(Utf8(deviceName)));

        /// <summary><c>sx_rig_check</c>: the device check report. Bounded 10 s on a Factory (the device reads 5 s, the Factory's rig 5 s), else 5 s.</summary>
        public static string RunCheck() => CallJson("sx_rig_check", NativeCheck);

        /// <summary>
        /// <c>sx_rig_start</c>. The two anchors must be read back to back
        /// (<see cref="ReadAnchors"/>).
        /// </summary>
        public static void Start(string episodeJson, long monotonicAnchorNs, long utcAnchorNs) =>
            Check("sx_rig_start", () => NativeStart(Utf8(episodeJson), monotonicAnchorNs, utcAnchorNs));

        /// <summary>
        /// <c>sx_rig_start_armed</c>: arm every stream, then anchor the episode once all deliver
        /// (the rig reads CLOCK_MONOTONIC and UTC itself). Returns <c>{"episode_id",
        /// "monotonic_anchor_ns", "utc_anchor_ns", "arm_utc_anchor": {...}, "arming_ms"}</c>.
        /// Blocks for the arming (seconds): never on Unity's main thread. An undersized buffer
        /// keeps the answer, so the retry does not re-arm.
        /// </summary>
        public static string StartArmed(string episodeJson)
        {
            var episode = Utf8(episodeJson);
            return CallJson("sx_rig_start_armed",
                (byte[]? outJson, UIntPtr cap, out UIntPtr needed) => NativeStartArmed(episode, outJson, cap, out needed));
        }

        /// <summary><c>sx_rig_stop</c>: the stop receipt. Bounded 5 s.</summary>
        public static string Stop() => CallJson("sx_rig_stop", NativeStop);

        /// <summary><c>sx_rig_status</c>.</summary>
        public static string Status() => CallJson("sx_rig_status", NativeStatus);

        /// <summary><c>sx_rig_close</c>. Never throws; a missing library is already closed.</summary>
        public static void Close()
        {
            try
            {
                NativeClose();
            }
            catch (Exception ex) when (IsMissingLibrary(ex))
            {
            }
        }

        /// <summary>
        /// CLOCK_MONOTONIC and UTC nanoseconds, read back to back (monotonic first), for
        /// <see cref="Start"/>. <paramref name="monotonicNanos"/> is the process's one
        /// monotonic clock (RealityLog.Common.MonotonicClock.Nanos on the headset).
        /// </summary>
        public static (long monotonicNs, long utcNs) ReadAnchors(Func<long> monotonicNanos)
        {
            var mono = monotonicNanos();
            var utcTicks = DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks;
            return (mono, checked(utcTicks * 100L));
        }

        private static void Check(string function, Func<int> call)
        {
            int code;
            try
            {
                code = call();
            }
            catch (Exception ex) when (IsMissingLibrary(ex))
            {
                throw new SxRigLibraryMissingException(function, ex.Message);
            }
            if (code != 0)
            {
                throw new SxRigException(function, ToError(code), LastErrorJson());
            }
        }

        private static string CallJson(string function, JsonOut call, int initialBytes = InitialBufferBytes)
        {
            try
            {
                var buffer = new byte[initialBytes];
                var code = call(buffer, (UIntPtr)buffer.Length, out var needed);
                if (code == (int)SxRigError.Buffer && (ulong)needed > (ulong)buffer.Length)
                {
                    buffer = new byte[checked((int)(ulong)needed)];
                    code = call(buffer, (UIntPtr)buffer.Length, out needed);
                }
                if (code != 0)
                {
                    throw new SxRigException(function, ToError(code), LastErrorJson());
                }
                return FromUtf8(buffer);
            }
            catch (Exception ex) when (IsMissingLibrary(ex))
            {
                throw new SxRigLibraryMissingException(function, ex.Message);
            }
        }

        /// <summary>The library's last error JSON, or a synthesized one if even that fails.</summary>
        private static string LastErrorJson()
        {
            try
            {
                var buffer = new byte[1024];
                var code = NativeLastError(buffer, (UIntPtr)buffer.Length, out var needed);
                if (code == (int)SxRigError.Buffer && (ulong)needed > (ulong)buffer.Length)
                {
                    buffer = new byte[checked((int)(ulong)needed)];
                    code = NativeLastError(buffer, (UIntPtr)buffer.Length, out needed);
                }
                if (code == 0)
                {
                    return FromUtf8(buffer);
                }
                return "{\"code\":\"unknown\",\"detail\":\"sx_rig_last_error returned " + code + "\"}";
            }
            catch (Exception ex) when (IsMissingLibrary(ex))
            {
                return "{\"code\":\"library_missing\",\"detail\":\"\"}";
            }
        }

        private static SxRigError ToError(int code) =>
            Enum.IsDefined(typeof(SxRigError), code) ? (SxRigError)code : SxRigError.Internal;

        private static bool IsMissingLibrary(Exception ex) =>
            ex is DllNotFoundException || ex is EntryPointNotFoundException;

        private static byte[] Utf8(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Array.Resize(ref bytes, bytes.Length + 1); // NUL
            return bytes;
        }

        private static string FromUtf8(byte[] buffer)
        {
            var end = Array.IndexOf(buffer, (byte)0);
            return Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end);
        }
    }
}
