# nullable enable

namespace RealityLog.Common
{
    /// <summary>
    /// When the recordings list may scan the disk and rebuild its rows. Rebuilding 118 rows took
    /// about 100 ms of the main thread, and on 2026-10-20 three rebuilds around a Pod-card Start
    /// delayed the first pose rows 291 ms past the first camera frame, so the seal refused the
    /// episode. So: nothing scans or rebuilds while an episode records (a request then is deferred
    /// to the stop), requests made while a scan runs collapse into one more scan, and a scan that
    /// finishes after a recording began is not applied. Pure, so the host harness can check it.
    /// </summary>
    public sealed class RecordingListRefreshGate
    {
        private bool scanning;
        private bool rescan;

        /// <summary>A refresh was asked for while recording; it runs once the recording ends.</summary>
        public bool Deferred { get; private set; }

        /// <summary>A scan is running off the main thread.</summary>
        public bool Scanning => scanning;

        /// <summary>Asks for a refresh; true means start a scan now.</summary>
        public bool Request(bool recording)
        {
            if (recording)
            {
                Deferred = true;
                return false;
            }
            if (scanning)
            {
                rescan = true;
                return false;
            }
            Deferred = false;
            scanning = true;
            return true;
        }

        /// <summary>Each frame: true means the refresh deferred by a recording should start now.</summary>
        public bool Tick(bool recording) => Deferred && !scanning && !recording && Request(false);

        /// <summary>
        /// A scan finished. <c>apply</c>: show its result; <c>scanAgain</c>: start another scan now
        /// (something asked while it ran). While recording neither happens; the refresh is deferred.
        /// </summary>
        public (bool apply, bool scanAgain) Completed(bool recording)
        {
            scanning = false;
            var again = rescan;
            rescan = false;
            if (recording)
            {
                Deferred = true;
                return (false, false);
            }
            return (true, again && Request(false));
        }

        /// <summary>Rows to create or refill per frame when a scan's result is applied.</summary>
        public const int RowsPerFrame = 4;
    }
}
