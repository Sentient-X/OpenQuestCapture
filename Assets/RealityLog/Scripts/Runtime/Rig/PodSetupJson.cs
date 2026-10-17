# nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RealityLog.Rig
{
    /// <summary>One enrollment step as libsx_rig answers it.</summary>
    public readonly struct EnrollmentStep
    {
        public readonly string Step;   // join | commission | rig | builds
        public readonly string State;  // succeeded | skipped | unavailable | failed
        public readonly string Detail;

        public EnrollmentStep(string step, string state, string detail)
        {
            Step = step;
            State = state;
            Detail = detail;
        }
    }

    /// <summary>
    /// The pod setup inputs and answers: the console's QR payload
    /// <c>{"factory_url", "auth_url", "join_code"}</c>, the keyboard's Factory URL, Auth URL and
    /// Join code, the sx_rig_enroll_site request, and its steps. No UnityEngine dependency
    /// (host-tested).
    /// </summary>
    public static class PodSetupJson
    {
        public const string DefaultFactoryUrl = "https://app.sentientx.io/d/factory/";
        public const string DefaultAuthUrl = "https://app.sentientx.io/d/auth/";

        /// <summary>(factory_url, auth_url, join_code) from a QR payload, or an error sentence.</summary>
        public static (string? factoryUrl, string? authUrl, string? joinCode, string? error) ParseQr(string text)
        {
            JObject? payload;
            try
            {
                payload = JToken.Parse(text) as JObject;
            }
            catch (JsonException)
            {
                return (null, null, null, "the QR code is not a pod join code");
            }
            if (payload == null) return (null, null, null, "the QR code is not a pod join code");
            string? Field(string key) => payload[key] is JValue { Type: JTokenType.String } v ? (string?)v : null;
            var factory = Field("factory_url");
            var auth = Field("auth_url");
            var code = Field("join_code");
            if (factory == null || auth == null || code == null)
            {
                return (null, null, null, "the QR code has no factory_url, auth_url and join_code");
            }
            var (f, fError) = NormalizeUrl(factory, "factory_url");
            if (fError != null) return (null, null, null, fError);
            var (a, aError) = NormalizeUrl(auth, "auth_url");
            if (aError != null) return (null, null, null, aError);
            if (CheckJoinCode(code) is string codeError) return (null, null, null, codeError);
            return (f, a, code.Trim(), null);
        }

        /// <summary>
        /// An https service URL with a trailing slash ("app.sentientx.io/d/factory" becomes
        /// "https://app.sentientx.io/d/factory/"). Plain http is refused: the join code and the
        /// machine credential travel over it.
        /// </summary>
        public static (string? url, string? error) NormalizeUrl(string url, string what)
        {
            var text = url.Trim();
            if (text.Length == 0) return (null, $"the {what} is empty");
            if (!text.Contains("://")) text = "https://" + text;
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
            {
                return (null, $"'{url}' is not a {what}");
            }
            if (uri.Scheme != Uri.UriSchemeHttps) return (null, $"the {what} must be https");
            var normalized = uri.GetLeftPart(UriPartial.Path);
            return (normalized.EndsWith("/", StringComparison.Ordinal) ? normalized : normalized + "/", null);
        }

        /// <summary>Null when the code looks like a join code (sxj_...), else why not.</summary>
        public static string? CheckJoinCode(string code)
        {
            var text = code.Trim();
            return text.StartsWith("sxj_", StringComparison.Ordinal) && text.Length >= 8 ? null : "a join code starts with sxj_";
        }

        /// <summary>The sx_rig_enroll_site request.</summary>
        public static string Request(string factoryUrl, string authUrl, string joinCode, string? rigTypeId)
        {
            var request = new JObject
            {
                ["site"] = new JObject { ["factory_url"] = factoryUrl, ["auth_url"] = authUrl },
                ["join_code"] = joinCode,
            };
            if (!string.IsNullOrEmpty(rigTypeId)) request["rig_type_id"] = rigTypeId;
            return request.ToString(Formatting.None);
        }

        /// <summary>
        /// The steps of an enrollment answer (<c>steps</c>) or of a failed enrollment's error
        /// (<c>refusal.steps</c>); empty when there are none.
        /// </summary>
        public static List<EnrollmentStep> Steps(string json)
        {
            var steps = new List<EnrollmentStep>();
            JObject? root;
            try
            {
                root = JToken.Parse(json) as JObject;
            }
            catch (JsonException)
            {
                return steps;
            }
            var list = root?["steps"] as JArray ?? (root?["refusal"] as JObject)?["steps"] as JArray;
            if (list == null) return steps;
            foreach (var item in list.OfType<JObject>())
            {
                string Text(string key) => item[key] is JValue { Type: JTokenType.String } v ? (string?)v ?? "" : "";
                steps.Add(new EnrollmentStep(Text("step"), Text("state"), Text("detail")));
            }
            return steps;
        }

        /// <summary>A status line: "join: succeeded · commission: failed (reason) · ...".</summary>
        public static string Describe(IEnumerable<EnrollmentStep> steps) =>
            string.Join(" · ", steps.Select(s => s.Detail.Length > 0 && s.State != "succeeded"
                ? $"{s.Step}: {s.State} ({s.Detail})" : $"{s.Step}: {s.State}"));
    }
}
