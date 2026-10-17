# nullable enable

using System;

namespace RealityLog.Rig
{
    /// <summary>Why a start was refused for the rig: a closed code, a sentence, the rig's JSON.</summary>
    public sealed class RigRefusal
    {
        /// <summary>
        /// One of <c>rig_library_missing</c>, <c>rig_open_failed</c>, <c>rig_usb_permission</c>,
        /// <c>rig_check_failed</c>, <c>rig_start_failed</c>, <c>rig_not_armed</c>,
        /// <c>rig_identity_pending</c>, <c>collector_signed_out</c>, <c>rig_storage_low</c>,
        /// <c>pod_config_incomplete</c>.
        /// </summary>
        public string Code { get; }
        public string Message { get; }
        /// <summary>A JSON value (the library's error object, or null).</summary>
        public string DetailJson { get; }

        public RigRefusal(string code, string message, string? detailJson = null)
        {
            Code = code;
            Message = message;
            DetailJson = string.IsNullOrEmpty(detailJson) ? "null" : detailJson!;
        }

        public string ToJson() =>
            "{\"code\":\"" + RigJson.Escape(Code) + "\",\"message\":\"" + RigJson.Escape(Message) +
            "\",\"detail\":" + DetailJson + "}";
    }

    /// <summary>
    /// A recording start refused because the rig is expected and not ready (or, from a start
    /// path other than the HTTP API, not armed). An <see cref="InvalidOperationException"/>, so
    /// every start path that reports a refusal's message keeps doing so.
    /// </summary>
    public sealed class RigRefusedException : InvalidOperationException
    {
        public RigRefusal Refusal { get; }

        public RigRefusedException(RigRefusal refusal)
            : base($"RecordingManager: refusing to start — {refusal.Code}: {refusal.Message}")
        {
            Refusal = refusal;
        }
    }

    internal static class RigJson
    {
        public static string Escape(string value) =>
            value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
    }
}
