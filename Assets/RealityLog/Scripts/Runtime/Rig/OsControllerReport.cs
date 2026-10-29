#nullable enable

using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RealityLog.Rig
{
    /// <summary>One controller as Horizon OS's remote service reports it in its diagnostic dump.</summary>
    public sealed class OsControllerEntry
    {
        public OsControllerEntry(int percent, string status, string externalStatus, string trackingStatus)
        {
            Percent = percent;
            Status = status;
            ExternalStatus = externalStatus;
            TrackingStatus = trackingStatus;
        }

        /// <summary>The charge, 0..100.</summary>
        public int Percent { get; }
        /// <summary>"Active", "Searching", "Disabled", ...; empty when the line has none.</summary>
        public string Status { get; }
        /// <summary>"CONNECTED_ACTIVE", "CONNECTED_INACTIVE", ...; empty when the line has none.</summary>
        public string ExternalStatus { get; }
        /// <summary>"POSITION", "NONE", ...; empty when the line has none.</summary>
        public string TrackingStatus { get; }

        /// <summary>Connected to the headset now: only then is its charge current.</summary>
        public bool Connected => ExternalStatus.StartsWith("CONNECTED", StringComparison.Ordinal);

        public override string ToString() =>
            Percent.ToString(CultureInfo.InvariantCulture) + "% (" + Word(Status) + ", " + Word(ExternalStatus) + ", " + Word(TrackingStatus) + ")";

        private static string Word(string value) => value.Length > 0 ? value : "-";
    }

    /// <summary>
    /// The Touch controllers in <c>dumpsys OVRRemoteService</c>'s text (the OS's diagnostic report,
    /// not an API): each <c>Paired device:</c> line names its side and charge, e.g.
    /// <c>Paired device: 5227d02c79c8ce97, Type:   Left, Model: RUBYPRQ, ... Battery:  40%, isAttached: 0,
    /// ..., Status: Active, ExternalStatus: CONNECTED_ACTIVE, TrackingStatus: POSITION, ...</c>.
    /// Parsed strictly: a line whose side or charge does not match, or whose charge is outside
    /// 0..100, is garbled and read as nothing, so a changed format reads "not available", never a
    /// wrong number. Pure: tested on the host (tools/sx-rig-stub/OsControllerReportHarness.cs).
    /// </summary>
    public sealed class OsControllerReport
    {
        private static readonly Regex SideAndCharge = new(@"Type:\s+(Left|Right)\b.*?Battery:\s+(\d{1,3})%", RegexOptions.CultureInvariant);
        private static readonly Regex StatusField = new(@"(?<![A-Za-z])Status:\s*([A-Za-z_]+)", RegexOptions.CultureInvariant);
        private static readonly Regex ExternalField = new(@"ExternalStatus:\s*([A-Za-z_]+)", RegexOptions.CultureInvariant);
        private static readonly Regex TrackingField = new(@"TrackingStatus:\s*([A-Za-z_0-9]+)", RegexOptions.CultureInvariant);
        private const string PairedPrefix = "Paired device:";

        private OsControllerReport(OsControllerEntry? left, OsControllerEntry? right, int paired, int garbled)
        {
            Left = left;
            Right = right;
            Paired = paired;
            Garbled = garbled;
        }

        public OsControllerEntry? Left { get; }
        public OsControllerEntry? Right { get; }
        /// <summary>The <c>Paired device:</c> lines seen.</summary>
        public int Paired { get; }
        /// <summary>The <c>Paired device:</c> lines that did not parse.</summary>
        public int Garbled { get; }
        /// <summary>At least one controller line parsed.</summary>
        public bool Parsed => Left != null || Right != null;

        public static OsControllerReport Parse(string? dump)
        {
            OsControllerEntry? left = null, right = null;
            var paired = 0;
            var garbled = 0;
            foreach (var raw in (dump ?? "").Split('\n'))
            {
                var line = raw.Trim();
                if (!line.StartsWith(PairedPrefix, StringComparison.Ordinal)) continue;
                paired++;
                var match = SideAndCharge.Match(line);
                if (!match.Success
                    || !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var percent)
                    || percent < 0 || percent > 100)
                {
                    garbled++;
                    continue;
                }
                var entry = new OsControllerEntry(percent, Field(StatusField, line), Field(ExternalField, line), Field(TrackingField, line));
                // An old pairing of the same side may be listed too: the connected one wins, else the first.
                if (match.Groups[1].Value == "Left") left = Prefer(left, entry);
                else right = Prefer(right, entry);
            }
            return new OsControllerReport(left, right, paired, garbled);
        }

        public string Describe() =>
            "Left " + (Left?.ToString() ?? "not in the report") + ", Right " + (Right?.ToString() ?? "not in the report")
            + (Garbled > 0 ? ", " + Garbled.ToString(CultureInfo.InvariantCulture) + " garbled line(s)" : "");

        private static OsControllerEntry Prefer(OsControllerEntry? kept, OsControllerEntry next) =>
            kept == null || (!kept.Connected && next.Connected) ? next : kept;

        private static string Field(Regex field, string line)
        {
            var match = field.Match(line);
            return match.Success ? match.Groups[1].Value : "";
        }
    }

    /// <summary>
    /// The last good charge of each controller from the OS report, with when it was read. A report
    /// that parsed says what is current: a controller it does not list, or lists disconnected, has
    /// no charge. A failed report (no binder, refused, timed out, nothing parsed) changes nothing,
    /// and a charge older than <see cref="Stale"/> is not available. Thread-safe; pure.
    /// </summary>
    public sealed class OsControllerBatteryCache
    {
        public static readonly TimeSpan Stale = TimeSpan.FromMinutes(5);

        private readonly object gate = new();
        private (int percent, DateTime at)? left;
        private (int percent, DateTime at)? right;

        public void Record(OsControllerReport report, DateTime now)
        {
            if (!report.Parsed) return;
            lock (gate)
            {
                left = Current(report.Left, now);
                right = Current(report.Right, now);
            }
        }

        /// <summary>Each controller's charge, 0..100; null where not available.</summary>
        public (int? Left, int? Right) Read(DateTime now)
        {
            lock (gate)
            {
                return (Fresh(left, now), Fresh(right, now));
            }
        }

        private static (int, DateTime)? Current(OsControllerEntry? entry, DateTime now) =>
            entry is { Connected: true } ? (entry.Percent, now) : ((int, DateTime)?)null;

        private static int? Fresh((int percent, DateTime at)? value, DateTime now) =>
            value is { } v && now - v.at <= Stale && now >= v.at ? v.percent : (int?)null;
    }
}
