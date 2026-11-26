#nullable enable

using System;
using System.Collections.Concurrent;
using System.Threading;

namespace RealityLog.Rig
{
    /// <summary>
    /// One background thread that runs libsx_rig calls in the order they were posted, so Unity's
    /// main thread never makes one. Every sx_rig call takes the library's one rig lock, which a
    /// check, start, stop or a detach joining an encoder stream holds for up to seconds; on the
    /// main thread that wait starves FixedUpdate's body poll and the head-camera frame pump
    /// (Pod 7, build 105, 2026-11-26: TrackingLost after an 822 ms body gap during an encoder
    /// replug). <see cref="Post"/> never waits. Posted work must catch its own exceptions; one
    /// that escapes is reported to the error callback and the lane runs on.
    /// </summary>
    public sealed class RigLane : IDisposable
    {
        private readonly BlockingCollection<Action> queue = new();
        private readonly Action<Exception> onError;

        public RigLane(string name, Action<Exception> onError)
        {
            this.onError = onError;
            new Thread(Run) { IsBackground = true, Name = name }.Start();
        }

        /// <summary>Queues <paramref name="work"/>; false once the lane is disposed. Never waits.</summary>
        public bool Post(Action work)
        {
            try
            {
                queue.Add(work);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false; // adding completed
            }
        }

        private void Run()
        {
            foreach (var work in queue.GetConsumingEnumerable())
            {
                try { work(); }
                catch (Exception ex) { onError(ex); }
            }
        }

        /// <summary>Takes no more work; what is queued still runs, in order.</summary>
        public void Dispose()
        {
            try { queue.CompleteAdding(); }
            catch (ObjectDisposedException) { }
        }

        /// <summary>
        /// Runs <paramref name="work"/> on a new background thread and waits at most
        /// <paramref name="timeout"/> for it: for teardown (quit, pause), where Unity's thread
        /// must see the rig stop but must not wait on the rig lock without bound. True when it
        /// finished; otherwise it keeps running on its own.
        /// </summary>
        public static bool RunBounded(string name, Action work, TimeSpan timeout)
        {
            var thread = new Thread(() => work()) { IsBackground = true, Name = name };
            thread.Start();
            return thread.Join(timeout);
        }
    }
}
