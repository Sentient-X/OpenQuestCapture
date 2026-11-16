# nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using RealityLog.Common;
using RealityLog.Rig;

namespace RealityLog.Camera
{
    /// <summary>
    /// The on-device camera gate's in-app half (sx#4210, scripts/device_smoke.sh): both head
    /// cameras record ~15 s through OQC's own C# start and stop (the JNI calls, the stop worker,
    /// the metadata writer), and each eye must leave a finished MP4, one timestamp row per encoded
    /// frame and no capture_error. No rig, no tracking gate, no wearer.
    ///
    /// Builds 74 and 75 passed every host harness and failed the first real recording on pod5;
    /// 75's stop threw NoSuchMethodError in this exact path. It runs only when adb asked for it:
    /// a file only the shell or the app can write (<see cref="RequestFile"/> in the app's files
    /// directory), taken once at launch and deleted before the test starts, so a crash never
    /// repeats it. The episode directory is <see cref="DirectoryName"/>, which no recording
    /// pattern matches, and a start is refused while it runs.
    /// </summary>
    public static class CameraSelfTest
    {
        public const string RequestFile = "camera_self_test.request";
        public const string DirectoryName = "camera_self_test";
        public const string ReportFile = "report.json";
        private const int Fps = 30;

        /// <summary>True while the self-test records (RecordingManager refuses a start).</summary>
        public static bool Running { get; private set; }

        /// <summary>Main thread (a coroutine of RecordingManager): runs once if adb asked for it.</summary>
        public static IEnumerator RunIfRequested(IReadOnlyList<VideoRecorderSurfaceProvider> eyes, Func<bool> recorderIdle)
        {
            var root = Application.persistentDataPath;
            var request = Path.Join(root, RequestFile);
            if (!File.Exists(request)) yield break;
            var seconds = 15;
            try
            {
                var text = File.ReadAllText(request).Trim();
                if (text.Length > 0 && JToken.Parse(text)["seconds"] is JValue { Type: JTokenType.Integer } s)
                    seconds = Math.Clamp((int)s, 3, 60);
            }
            catch (Exception) { /* a malformed request runs the default */ }
            try { File.Delete(request); }
            catch (Exception ex)
            {
                Debug.LogError($"[{Constants.LOG_TAG}] CAMERA SELF-TEST FAIL: cannot take the request ({ex.Message}); not run");
                yield break;
            }

            var dir = Path.Join(root, DirectoryName);
            var errors = new List<string>();
            Debug.Log($"[{Constants.LOG_TAG}] CameraSelfTest: requested, {seconds} s on {eyes.Count} eye(s)");
            if (eyes.Count != 2) errors.Add($"expected 2 head cameras, found {eyes.Count}");

            // The cameras open a few seconds after launch.
            var readyBy = Time.realtimeSinceStartup + 30f;
            while (errors.Count == 0 && Time.realtimeSinceStartup < readyBy
                && !(eyes.All(e => e.IsReadyToRecord) && recorderIdle()))
            {
                yield return null;
            }
            foreach (var eye in eyes.Where(e => !e.IsReadyToRecord))
                errors.Add($"{eye.OutputVideoFileName}: not ready in 30 s ({eye.StartRefusal ?? "camera session not open"})");
            if (errors.Count == 0 && !recorderIdle()) errors.Add("a recording is running");

            var stopped = false;
            if (errors.Count == 0)
            {
                Running = true;
                try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
                catch (Exception ex) { errors.Add($"cannot clear {dir}: {ex.Message}"); }
                foreach (var eye in eyes)
                {
                    eye.SetDataDirectoryName(DirectoryName);
                    eye.PrepareRecordingSession();
                }
                foreach (var eye in eyes) eye.StartRecordingSession();

                var watch = new CaptureFailureWatch(eyes.Select(e => (e.OutputVideoFileName, (Func<string?>)e.PollCaptureError)).ToArray());
                var stopAt = Time.realtimeSinceStartup + seconds;
                while (Time.realtimeSinceStartup < stopAt)
                {
                    if (watch.Poll() is { } failure) { errors.Add(failure.Describe()); break; }
                    yield return new WaitForSecondsRealtime(CaptureFailureWatch.PollIntervalMs / 1000f);
                }

                // RecordingManager's stop, step for step: freeze both eyes, then each eye's Java
                // stop on one worker attached to the JVM, then wait for the files to settle.
                foreach (var eye in eyes) eye.RequestStopRecordingSession();
                var finishers = eyes.Select(e => (e.OutputVideoFileName, finish: e.BeginStopRecordingSession())).ToList();
                var workerErrors = new List<string>();
                var worker = new System.Threading.Thread(() =>
                {
#if UNITY_ANDROID && !UNITY_EDITOR
                    var attached = AndroidJNI.AttachCurrentThread() == 0;
#endif
                    try
                    {
                        foreach (var (name, finish) in finishers)
                        {
                            if (finish == null) { lock (workerErrors) workerErrors.Add($"{name}: was not recording at stop"); continue; }
                            try { finish(); }
                            catch (Exception ex) { lock (workerErrors) workerErrors.Add($"{name}: stop threw {ex.Message}"); }
                        }
                    }
                    finally
                    {
#if UNITY_ANDROID && !UNITY_EDITOR
                        if (attached) AndroidJNI.DetachCurrentThread();
#endif
                    }
                }) { IsBackground = true, Name = "CameraSelfTestStop" };
                worker.Start();
                while (worker.IsAlive) yield return null;
                errors.AddRange(workerErrors);
                while (eyes.Any(e => e.IsFinalizingVideo)) yield return null;
                stopped = true;
                Running = false;
            }

            var reports = new JArray();
            if (stopped)
            {
                foreach (var eye in eyes)
                {
                    var (report, eyeErrors) = Judge(dir, eye, seconds);
                    reports.Add(report);
                    errors.AddRange(eyeErrors);
                }
            }
            var pass = errors.Count == 0;
            var verdict = new JObject
            {
                ["verdict"] = pass ? "pass" : "fail",
                ["seconds"] = seconds,
                ["app_version"] = Application.version,
                ["errors"] = new JArray(errors),
                ["eyes"] = reports,
                ["finished_unix_ms"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Join(dir, ReportFile), verdict.ToString());
            }
            catch (Exception ex) { Debug.LogError($"[{Constants.LOG_TAG}] CameraSelfTest: cannot write the report: {ex.Message}"); }
            if (pass)
            {
                Debug.Log($"[{Constants.LOG_TAG}] CAMERA SELF-TEST PASS: {string.Join("; ", reports.Select(r => $"{r["eye"]} {r["frames"]} frames, {r["mp4_bytes"]} B"))}");
                if (RigCoordinator.Instance is { } rig) rig.Cue(RigCues.Cue.Sealed, "camera self-test passed");
            }
            else
            {
                Debug.LogError($"[{Constants.LOG_TAG}] CAMERA SELF-TEST FAIL: {string.Join(" | ", errors)}");
                if (RigCoordinator.Instance is { } rig) rig.Cue(RigCues.Cue.Refused, "camera self-test failed");
            }
        }

        /// <summary>One eye's files: metadata capture_error, timestamp rows vs encoded frames, a finished MP4.</summary>
        private static (JObject report, List<string> errors) Judge(string dir, VideoRecorderSurfaceProvider eye, int seconds)
        {
            var errors = new List<string>();
            var name = eye.OutputVideoFileName;
            var report = new JObject { ["eye"] = name };
            try
            {
                var metadata = JObject.Parse(File.ReadAllText(Path.Join(dir, eye.VideoMetadataFileName)));
                var captureError = metadata["capture_error"];
                if (captureError != null && captureError.Type != JTokenType.Null)
                    errors.Add($"{name}: capture_error {captureError}");
                var selected = (long?)metadata["selected_frame_count"] ?? -1;
                report["frames"] = selected;
                var rows = File.ReadLines(Path.Join(dir, eye.FrameTimestampsFileName)).Skip(1).Count(l => l.Trim().Length > 0);
                report["timestamp_rows"] = rows;
                if (rows != selected) errors.Add($"{name}: {rows} timestamp rows for {selected} encoded frames");
                long expected = (long)seconds * Fps;
                if (selected < expected * 9 / 10 || selected > expected * 11 / 10)
                    errors.Add($"{name}: {selected} frames in {seconds} s (expected about {expected})");
                var mp4 = Path.Join(dir, name);
                report["mp4_bytes"] = File.Exists(mp4) ? new FileInfo(mp4).Length : 0;
                if (Mp4Unfinished(mp4) is { } why) errors.Add($"{name}: {why}");
            }
            catch (Exception ex)
            {
                errors.Add($"{name}: cannot read its files: {ex.Message}");
            }
            report["errors"] = new JArray(errors);
            return (report, errors);
        }

        /// <summary>Null when the MP4's top-level boxes tile the file and start ftyp, moov with a fragment; else why not.</summary>
        public static string? Mp4Unfinished(string path)
        {
            if (!File.Exists(path)) return "no MP4";
            using var f = File.OpenRead(path);
            if (f.Length == 0) return "MP4 is empty";
            var types = new List<string>();
            var header = new byte[16];
            long at = 0;
            while (at < f.Length)
            {
                f.Position = at;
                if (f.Read(header, 0, 8) != 8) return $"truncated box header at {at}";
                long size = ((long)header[0] << 24) | ((long)header[1] << 16) | ((long)header[2] << 8) | header[3];
                var type = System.Text.Encoding.ASCII.GetString(header, 4, 4);
                if (size == 1)
                {
                    if (f.Read(header, 8, 8) != 8) return $"truncated large box at {at}";
                    size = 0;
                    for (var i = 8; i < 16; i++) size = (size << 8) | header[i];
                }
                if (size < 8 || at + size > f.Length) return $"box '{type}' at {at} has size {size} in a {f.Length} B file (not finalized)";
                types.Add(type);
                at += size;
            }
            if (types.Count < 2 || types[0] != "ftyp" || types[1] != "moov") return $"MP4 starts {string.Join(",", types.Take(2))}, not ftyp,moov";
            if (!types.Contains("moof") || !types.Contains("mdat")) return "MP4 holds no fragment";
            return null;
        }
    }
}
