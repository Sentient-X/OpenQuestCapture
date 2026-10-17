# nullable enable

using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RealityLog.Rig
{
    /// <summary>
    /// Reads the pod setup inputs: the console's QR payload <c>{"site", "join_code"}</c> and the
    /// keyboard's Site and Join code. No UnityEngine dependency (host-tested).
    /// </summary>
    public static class PodSetupJson
    {
        /// <summary>(site base URL, join code) from a QR payload, or an error sentence.</summary>
        public static (string? site, string? joinCode, string? error) ParseQr(string text)
        {
            JObject? payload;
            try
            {
                payload = JToken.Parse(text) as JObject;
            }
            catch (JsonException)
            {
                return (null, null, "the QR code is not a pod join code");
            }
            if (payload == null) return (null, null, "the QR code is not a pod join code");
            var site = payload["site"] is JValue { Type: JTokenType.String } s ? (string?)s : null;
            var code = payload["join_code"] is JValue { Type: JTokenType.String } c ? (string?)c : null;
            if (site == null || code == null) return (null, null, "the QR code has no site and join_code");
            var (normalized, siteError) = NormalizeSite(site);
            if (siteError != null) return (null, null, siteError);
            if (CheckJoinCode(code) is string codeError) return (null, null, codeError);
            return (normalized, code.Trim(), null);
        }

        /// <summary>
        /// The site as an https base URL (scheme and host only): "app.sentientx.io" and
        /// "https://app.sentientx.io/console" both become "https://app.sentientx.io". Plain http
        /// is refused: the join code and the machine credential travel over it.
        /// </summary>
        public static (string? site, string? error) NormalizeSite(string site)
        {
            var text = site.Trim();
            if (text.Length == 0) return (null, "the site is empty");
            if (!text.Contains("://")) text = "https://" + text;
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
            {
                return (null, $"'{site}' is not a site address");
            }
            if (uri.Scheme != Uri.UriSchemeHttps) return (null, "the site must be https");
            return (uri.GetLeftPart(UriPartial.Authority), null);
        }

        /// <summary>Null when the code looks like a join code (sxj_...), else why not.</summary>
        public static string? CheckJoinCode(string code)
        {
            var text = code.Trim();
            if (!text.StartsWith("sxj_", StringComparison.Ordinal) || text.Length < 8)
            {
                return "a join code starts with sxj_";
            }
            return null;
        }
    }
}
