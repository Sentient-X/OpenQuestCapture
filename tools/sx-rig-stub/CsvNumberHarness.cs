#nullable enable
// CsvWriter: a numeric row's text is exactly string.Join(",", row.Select(v => v.ToString()))
// under the writer's culture, though it is formatted with TryFormat into one buffer (no string
// per value), and a rented row comes back to the pool once written. Run by test_binding.sh.

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using RealityLog.IO;

internal static class CsvNumberHarness
{
    private static int failures;

    private static void Expect(bool condition, string what)
    {
        Console.WriteLine((condition ? "PASS " : "FAIL ") + what);
        if (!condition) failures++;
    }

    private static int Main()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("en-US");
        Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        var random = new Random(20261126);
        var special = new[]
        {
            0.0, -0.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity, double.Epsilon,
            double.MaxValue, double.MinValue, 1e-300, 1.0 / 3.0, 0.1, 1763987654321.123, 4294967295.0,
            123456789012345678.0, -0.000012345, 0.70710678118654757,
        };
        var rows = Enumerable.Range(0, 200).Select(r =>
            special.Concat(Enumerable.Range(0, 598 - special.Length).Select(_ =>
                random.Next(4) switch
                {
                    0 => (random.NextDouble() - 0.5) * 4,
                    1 => (float)((random.NextDouble() - 0.5) * 4),
                    2 => random.Next() * 1e3 + random.NextDouble(),
                    _ => BitConverter.Int64BitsToDouble((long)(random.NextDouble() * long.MaxValue)),
                })).ToArray()).ToList();
        var expected = rows.Select(row => string.Join(",", row.Select(v => v.ToString()))).ToList();
        var path = Path.Combine(Path.GetTempPath(), "csv-" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            var distinct = new System.Collections.Generic.HashSet<double[]>();
            using (var writer = new CsvWriter(path, new[] { "h" }))
            {
                foreach (var row in rows)
                {
                    var rented = writer.RentRow(row.Length);
                    distinct.Add(rented);
                    Array.Copy(row, rented, row.Length);
                    writer.EnqueueRow(rented);
                    // Rows are copied in so a rented row may come back while the next is filled.
                    while (writer.RowsWritten < rows.IndexOf(row) + 1) Thread.Sleep(0);
                }
            }
            Expect(distinct.Count < rows.Count / 2, $"written rows come back to be rented again ({distinct.Count} arrays for {rows.Count} rows)");
            var lines = File.ReadAllLines(path);
            Expect(lines[0] == "h", "the header comes first");
            var same = lines.Skip(1).SequenceEqual(expected);
            if (!same)
            {
                var at = lines.Skip(1).Zip(expected, (a, b) => a == b).ToList().IndexOf(false);
                Console.WriteLine("first difference at row " + at);
            }
            Expect(same && lines.Length == rows.Count + 1, "every value reads as its ToString()");
        }
        finally
        {
            File.Delete(path);
        }
        return failures == 0 ? 0 : 1;
    }
}
