# nullable enable

namespace RealityLog.Rig
{
    /// <summary>
    /// The HTTP API's guard. An enrolled headset requires <c>Authorization: Bearer &lt;token&gt;</c>
    /// on every request, the token the rig derives from the machine credential
    /// (sx_rig_api_token). A headset not enrolled serves, without a token, only what
    /// setting it up needs (<see cref="OpenBeforeSetUp"/>): it records nothing until it is set
    /// up, so its recording routes are refused. There is no Local only mode any more (removed
    /// 2026-11-22). A missing token never opens anything on an enrolled headset. No
    /// UnityEngine dependency (host-tested).
    /// </summary>
    public static class RigApiGuard
    {
        /// <summary>True when the request may proceed.</summary>
        public static bool Authorized(string? authorizationHeader, string? token, bool enrolled, string method, string path)
        {
            if (!enrolled) return OpenBeforeSetUp(method, path);
            if (string.IsNullOrEmpty(token)) return false;
            const string prefix = "Bearer ";
            if (authorizationHeader == null || !authorizationHeader.StartsWith(prefix, System.StringComparison.Ordinal)) return false;
            return ConstantTimeEquals(authorizationHeader.Substring(prefix.Length).Trim(), token!);
        }

        /// <summary>
        /// What a headset not set up serves without a token: its status and diagnostics (to see
        /// why it is not ready) and the enrollment itself. Nothing that records, lists, streams
        /// or removes recordings, or drives the rig.
        /// </summary>
        public static bool OpenBeforeSetUp(string method, string path) =>
            (method == "GET" && (path == "/api/status" || path == "/api/diagnostics"))
            || (method == "POST" && path == "/api/enroll");

        private static bool ConstantTimeEquals(string a, string b)
        {
            if (a.Length != b.Length) return false;
            var diff = 0;
            for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
