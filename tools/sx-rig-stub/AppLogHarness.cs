#nullable enable
// AppLogFile: the app's log lines kept in app-private storage for the rig to ship. Every start
// closes the last run's file; the active file rolls at its bound; closed files beyond the bound go
// oldest first. Run by test_binding.sh.

using System;
using System.IO;
using System.Linq;
using RealityLog.Rig;

internal static class AppLogHarness
{
    private static int failures;

    private static void Expect(bool condition, string what)
    {
        Console.WriteLine((condition ? "PASS " : "FAIL ") + what);
        if (!condition) failures++;
    }

    private static int Main()
    {
        var directory = Path.Combine(Path.GetTempPath(), "applog-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var log = new AppLogFile(directory))
            {
                log.Write(new DateTime(2026, 10, 22, 10, 0, 0, DateTimeKind.Utc), "LOG", "first line\nsecond");
            }
            var active = Path.Combine(directory, AppLogFile.ActiveName);
            Expect(File.ReadAllText(active) == "2026-10-22T10:00:00.000Z LOG first line\n    second\n",
                "a line is stamped and its continuation indented");
            using (var log = new AppLogFile(directory))
            {
                Expect(Directory.GetFiles(directory, "app-*.log").Length == 1 && new FileInfo(log.ActivePath).Length == 0,
                    "a start closes the last run's file, and writes a fresh one");
                var big = new string('x', 300 * 1024);
                for (var i = 0; i < 4; i++) log.Write(DateTime.UtcNow, "LOG", big);
                Expect(Directory.GetFiles(directory, "app-*.log").Length == 2, "the active file rolls past its bound");
                Expect(new FileInfo(log.ActivePath).Length < AppLogFile.RollBytes, "the active file stays under its bound");
            }
            for (var i = 0; i < AppLogFile.MaxClosedFiles + 4; i++)
            {
                using var log = new AppLogFile(directory);
                log.Write(DateTime.UtcNow, "LOG", "run " + i);
            }
            var closed = Directory.GetFiles(directory, "app-*.log");
            Expect(closed.Length == AppLogFile.MaxClosedFiles, $"closed files are bounded ({closed.Length})");
            Expect(closed.OrderBy(p => p, StringComparer.Ordinal).Last().Length > 0, "the newest closed files stay");
            using (var log = new AppLogFile(directory))
            {
            }
            Expect(!File.Exists(Path.Combine(directory, AppLogFile.ActiveName)) || new FileInfo(Path.Combine(directory, AppLogFile.ActiveName)).Length == 0,
                "an empty run leaves no closed file");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
        return failures == 0 ? 0 : 1;
    }
}
