# nullable enable

namespace RealityLog.Rig
{
    /// <summary>
    /// The HTTP API's guard on an enrolled headset that is not Local only: every request must
    /// carry <c>Authorization: Bearer &lt;token&gt;</c>, the token the rig derives from the
    /// machine credential (sx_rig_api_token). Local only, or not enrolled (no token), keeps the
    /// open LAN API. No UnityEngine dependency (host-tested).
    /// </summary>
    public static class RigApiGuard
    {
        /// <summary>True when the request may proceed.</summary>
        public static bool Authorized(string? authorizationHeader, string? token, bool localOnly)
        {
            if (localOnly || string.IsNullOrEmpty(token)) return true;
            const string prefix = "Bearer ";
            if (authorizationHeader == null || !authorizationHeader.StartsWith(prefix, System.StringComparison.Ordinal)) return false;
            return ConstantTimeEquals(authorizationHeader.Substring(prefix.Length).Trim(), token!);
        }

        private static bool ConstantTimeEquals(string a, string b)
        {
            if (a.Length != b.Length) return false;
            var diff = 0;
            for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
