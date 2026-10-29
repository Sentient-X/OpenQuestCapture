# nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RealityLog.Rig
{
    /// <summary>
    /// One notice: a code of the rig's notice table (sx_operator_notices.json), who acts, and the
    /// arguments its words take. libsx_rig names most (attempts, start refusals, check reports,
    /// sx_rig_last_error); the app names the ones only it can see (sign-in, USB access, the
    /// controllers, the display rate).
    /// </summary>
    public sealed class OperatorNotice
    {
        public string Code { get; }
        /// <summary>collector, floor_manager or technician; "" until the table says.</summary>
        public string Who { get; }
        public IReadOnlyDictionary<string, string> Args { get; }

        public OperatorNotice(string code, string who = "", IReadOnlyDictionary<string, string>? args = null)
        {
            Code = code;
            Who = who;
            Args = args ?? new Dictionary<string, string>();
        }

        public static OperatorNotice Of(string code, params (string name, string value)[] args) =>
            new(code, "", args.ToDictionary(a => a.name, a => a.value));

        /// <summary>The rig's {"code", "who", "args"}; null when <paramref name="token"/> is not one.</summary>
        public static OperatorNotice? FromJson(JToken? token)
        {
            if (token is not JObject json || json["code"] is not JValue { Type: JTokenType.String } code) return null;
            var args = new Dictionary<string, string>();
            if (json["args"] is JObject given)
            {
                foreach (var arg in given.Properties())
                {
                    if (arg.Value is JValue value && value.Value != null) args[arg.Name] = Convert.ToString(value.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
                }
            }
            return new OperatorNotice((string)code!, json["who"] is JValue { Type: JTokenType.String } who ? (string?)who ?? "" : "", args);
        }

        /// <summary>The same notice with <paramref name="name"/> set, unless the rig already gave it.</summary>
        public OperatorNotice WithDefault(string name, string value)
        {
            if (Args.ContainsKey(name)) return this;
            var args = new Dictionary<string, string>(Args.ToDictionary(a => a.Key, a => a.Value)) { [name] = value };
            return new OperatorNotice(Code, Who, args);
        }

        public JObject ToJson() => new()
        {
            ["code"] = Code,
            ["who"] = Who,
            ["args"] = new JObject(Args.Select(a => new JProperty(a.Key, a.Value))),
        };
    }

    /// <summary>A notice in words: what prevents it, what is still fine, who acts and the next action (and its button).</summary>
    public readonly struct OperatorMessage
    {
        public string Code { get; }
        /// <summary>collector, floor_manager or technician.</summary>
        public string Who { get; }
        /// <summary>"Collector", "Floor manager", "Technician".</summary>
        public string WhoLabel { get; }
        public string Headline { get; }
        public string StillFine { get; }
        public string Action { get; }
        /// <summary>"check", "sign_in" or "".</summary>
        public string Button { get; }
        public IReadOnlyDictionary<string, string> Args { get; }

        public OperatorMessage(string code, string who, string whoLabel, string headline, string stillFine, string action, string button,
            IReadOnlyDictionary<string, string> args)
        {
            Code = code;
            Who = who;
            WhoLabel = whoLabel;
            Headline = headline;
            StillFine = stillFine;
            Action = action;
            Button = button;
            Args = args;
        }
    }

    /// <summary>
    /// The operator's words, from sx_operator_notices.json: libsx_rig's own table, shipped
    /// byte-identical in Assets/RealityLog/Resources (copied with the library by
    /// scripts/copy_rig_artifacts.sh; the host harness compares it with the sx checkout's), the
    /// same table the phone's catalogue equals. Pure: no UnityEngine; RigCoordinator loads the
    /// text at startup, the host harness from the file. Without a table, every notice still reads
    /// as the unknown code's words, never raw text.
    /// </summary>
    public static class OperatorNotices
    {
        public const int Schema = 1;
        private static JObject? table;

        /// <summary>True once a table was loaded.</summary>
        public static bool Loaded => table != null;

        /// <summary>Loads the table; throws <see cref="InvalidOperationException"/> when it is not one this build reads.</summary>
        public static void Load(string json)
        {
            JObject parsed;
            try
            {
                parsed = JObject.Parse(json);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("sx_operator_notices.json is not JSON: " + ex.Message);
            }
            if (parsed["schema"] is not JValue { Type: JTokenType.Integer } schema || (long)schema != Schema || parsed["notices"] is not JObject)
            {
                throw new InvalidOperationException("sx_operator_notices.json is not schema " + Schema);
            }
            table = parsed;
        }

        /// <summary>Every code the table names.</summary>
        public static IEnumerable<string> Codes => (table?["notices"] as JObject)?.Properties().Select(p => p.Name) ?? Enumerable.Empty<string>();

        /// <summary>A stage's word ("sealing" -> "Sealing").</summary>
        public static string Stage(string key) => Str(table?["stages"]?[key]) is { Length: > 0 } word ? word : Humanize(key);

        /// <summary>A phrase ("ready", "sealing_for" with {minutes}), filled.</summary>
        public static string Phrase(string key, params (string name, string value)[] args) =>
            Fill(Str(table?["phrases"]?[key]), args.ToDictionary(a => a.name, a => a.value));

        public static OperatorMessage Render(OperatorNotice notice)
        {
            var entry = table?["notices"]?[notice.Code] as JObject;
            if (entry == null)
            {
                // A code this build does not know: the unknown code's words, with that code.
                var unknown = new Dictionary<string, string> { ["code"] = notice.Code.Length > 0 ? notice.Code : "none" };
                var fallback = table?["notices"]?["unknown"] as JObject;
                return new OperatorMessage("unknown", "technician", Owner("technician"),
                    fallback != null ? Fill(Str(fallback["headline"]), unknown) : "Recording failed",
                    fallback != null ? Fill(Str(fallback["still_fine"]), unknown) : "Your earlier episodes are safe.",
                    fallback != null ? Fill(Str(fallback["action"]), unknown) : "Tell a technician (code " + unknown["code"] + ").",
                    "", unknown);
            }
            var who = Str(entry["who"]) is { Length: > 0 } said ? said : notice.Who;
            return new OperatorMessage(notice.Code, who, Owner(who), Fill(Str(entry["headline"]), notice.Args),
                Fill(Str(entry["still_fine"]), notice.Args), Fill(Str(entry["action"]), notice.Args), Str(entry["button"]), notice.Args);
        }

        /// <summary>"collector" -> "Collector".</summary>
        public static string Owner(string who) => Str(table?["owners"]?[who]) is { Length: > 0 } word ? word : Humanize(who);

        /// <summary>
        /// {name} filled from <paramref name="args"/>, side, camera and part through the table's
        /// tokens; what is left renders as nothing and the spaces it leaves close up (the rule
        /// libsx_rig's notice.rs and the phone apply).
        /// </summary>
        public static string Fill(string template, IReadOnlyDictionary<string, string> args)
        {
            var text = template;
            foreach (var arg in args)
            {
                var word = Str(table?["tokens"]?[arg.Key]?[arg.Value]) is { Length: > 0 } token ? token : arg.Value;
                text = text.Replace("{" + arg.Key + "}", word);
            }
            var open = text.IndexOf('{');
            while (open >= 0)
            {
                var close = text.IndexOf('}', open);
                if (close < 0) break;
                text = text.Remove(open, close - open + 1);
                open = text.IndexOf('{');
            }
            return string.Join(" ", text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }

        /// <summary>
        /// The notice a refused start carries: the rig's own (its error JSON's "notice"), else the
        /// app's refusal code's.
        /// </summary>
        public static OperatorNotice ForRefusal(string code, string detailJson)
        {
            try
            {
                if (!string.IsNullOrEmpty(detailJson) && JToken.Parse(detailJson) is JObject detail && OperatorNotice.FromJson(detail["notice"]) is { } rig)
                {
                    return rig;
                }
            }
            catch (JsonException)
            {
            }
            return ForCode(code);
        }

        /// <summary>The app's refusal codes (RigRefusal, the HTTP routes' errors) as notices.</summary>
        public static OperatorNotice ForCode(string code) => code switch
        {
            "collector_signed_out" => OperatorNotice.Of("signed_out"),
            "rig_usb_permission" => OperatorNotice.Of("usb_permission"),
            "rig_library_missing" => OperatorNotice.Of("library_missing"),
            "rig_open_failed" => OperatorNotice.Of("rig_open_failed"),
            "rig_check_failed" => OperatorNotice.Of("rig_check_failed"),
            "rig_storage_low" or "storage_full" => OperatorNotice.Of("storage_low"),
            "factory_unanswered" => OperatorNotice.Of("factory_unanswered"),
            "app_paused" => OperatorNotice.Of("app_paused"),
            _ => OperatorNotice.Of("unknown", ("code", code.Length > 0 ? code : "none")),
        };

        private static string Humanize(string key) =>
            key.Length == 0 ? "" : char.ToUpperInvariant(key[0]) + key.Substring(1).Replace('_', ' ');

        private static string Str(JToken? token) =>
            token is JValue { Type: JTokenType.String } value ? (string?)value ?? "" : "";
    }
}
