# nullable enable

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RealityLog.IO
{
    public class CsvWriter : IDisposable
    {
        // Upper bound on how long written rows may sit in the StreamWriter buffer.
        // A force-stop or crash loses at most about this much data.
        private const int FlushIntervalMs = 1000;

        // Items are string[] or double[]; double[] rows are formatted on the writer thread.
        private readonly BlockingCollection<object> _queue = new();
        private readonly string _filePath;
        private readonly string[] _header;
        // Culture of the constructing thread. Numbers are formatted with it on the writer
        // thread so the text matches what double.ToString() produced on the caller thread
        // (LocaleFixer pins DefaultThreadCurrentCulture to en-US for all threads).
        private readonly CultureInfo _culture;
        private readonly Task _writerTask;
        // Rows handed out by RentRow come back here once written: the body logger writes a
        // 598-value row per poll, and a fresh array each time (with a string per value on the
        // writer) was a steady garbage stream whose collections pause Unity's main thread too.
        private const int PooledRows = 64;
        private readonly ConcurrentBag<double[]> _rowPool = new();
        private int _pooledLength;
        private readonly char[] _number = new char[64];
        private long _rowsWritten;
        private bool _disposed = false;

        public CsvWriter(string filePath, string[]? header = null)
        {
            _filePath = filePath;
            _header = header ?? Array.Empty<string>();
            _culture = CultureInfo.CurrentCulture;
            // Its own thread, not a pool one: the loop blocks for the writer's life, and five of
            // them held pool threads the rig's tracking feed and the HTTP handlers wait for.
            _writerTask = Task.Factory.StartNew(WriteLoop, CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        /// <summary>Rows written to the file so far (excludes the header). Safe from any thread.</summary>
        public long RowsWritten => Interlocked.Read(ref _rowsWritten);

        /// <summary>
        /// Enqueues a numeric row. The array is formatted later on the writer thread,
        /// so callers must not modify it after this call.
        /// </summary>
        /// <summary>
        /// A row of <paramref name="length"/> values to fill and hand to <see cref="EnqueueRow(double[])"/>;
        /// once written it returns here for a later row. Every value must be set: a rented row
        /// holds an earlier row's values. One length per writer.
        /// </summary>
        public double[] RentRow(int length)
        {
            Interlocked.CompareExchange(ref _pooledLength, length, 0);
            if (length == Volatile.Read(ref _pooledLength) && _rowPool.TryTake(out var row))
                return row;
            return new double[length];
        }

        public void EnqueueRow(params double[] columns)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CsvWriter));
            _queue.Add(columns);
        }

        public void EnqueueRow(params string[] columns)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CsvWriter));
            _queue.Add(columns);
        }

        private void WriteLoop()
        {
            var directoryName = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directoryName) && !Directory.Exists(_filePath))
            {
                Directory.CreateDirectory(directoryName);
            }

            using var writer = new StreamWriter(_filePath, append: false);

            if (_header.Length > 0)
            {
                writer.WriteLine(string.Join(",", _header));
            }
            writer.Flush();

            var sinceFlush = Stopwatch.StartNew();
            var dirty = false;

            while (true)
            {
                if (!_queue.TryTake(out var item, FlushIntervalMs))
                {
                    // Timed out (no rows for a while) or adding completed and queue drained.
                    if (_queue.IsCompleted) break;
                    if (dirty)
                    {
                        writer.Flush();
                        dirty = false;
                        sinceFlush.Restart();
                    }
                    continue;
                }

                if (item is double[] numbers)
                {
                    WriteNumberRow(writer, numbers);
                    var pooled = Volatile.Read(ref _pooledLength);
                    if (pooled != 0 && numbers.Length == pooled && _rowPool.Count < PooledRows)
                        _rowPool.Add(numbers);
                }
                else
                {
                    writer.WriteLine(string.Join(",", (string[])item));
                }
                Interlocked.Increment(ref _rowsWritten);
                dirty = true;

                if (sinceFlush.ElapsedMilliseconds >= FlushIntervalMs)
                {
                    writer.Flush();
                    dirty = false;
                    sinceFlush.Restart();
                }
            }
            // Remaining buffered data is flushed when the writer is disposed.
        }

        // Same text as string.Join(",", numbers.Select(f => f.ToString())) on a thread
        // whose CurrentCulture is _culture: TryFormat is ToString's own formatter, into a
        // buffer instead of a string per value.
        private void WriteNumberRow(StreamWriter writer, double[] numbers)
        {
            for (int i = 0; i < numbers.Length; i++)
            {
                if (i > 0) writer.Write(',');
                if (numbers[i].TryFormat(_number, out var written, default, _culture))
                    writer.Write(_number, 0, written);
                else
                    writer.Write(numbers[i].ToString(_culture));
            }
            writer.WriteLine();
        }

        /// <summary>
        /// Take no more rows and let the writer drain, without waiting for it; <see cref="Dispose"/>
        /// then waits. Closing every writer before disposing any lets their drains overlap.
        /// </summary>
        public void Close()
        {
            if (!_queue.IsAddingCompleted) _queue.CompleteAdding();
        }

        public void Dispose()
        {
            if (_disposed) return;

            Close();
            _writerTask.Wait();
            _disposed = true;
        }
    }
}
