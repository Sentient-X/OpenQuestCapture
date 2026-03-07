#nullable enable

using System;
using System.Collections;
using System.IO;
using UnityEngine;
using RealityLog.Common;

namespace RealityLog
{
    public enum CalibrationPhase
    {
        NotStarted,
        StationaryBias,
        StaticOrientations,
        DynamicExcitation,
        FinalStationary,
        Complete
    }

    /// <summary>
    /// Coroutine state machine that walks through 4 timed calibration phases,
    /// controls RecordingManager start/stop, and writes episode_id.json on completion.
    /// </summary>
    public class CalibrationSession : MonoBehaviour
    {
        [SerializeField] private RecordingManager recordingManager = default!;

        [Tooltip("Duration in seconds for each phase: StationaryBias, StaticOrientations, DynamicExcitation, FinalStationary")]
        [SerializeField] private float[] phaseDurations = { 10f, 30f, 45f, 5f };

        public event Action<CalibrationPhase>? OnPhaseChanged;
        public event Action? OnCalibrationComplete;
        public event Action? OnCalibrationCancelled;

        public bool IsRunning { get; private set; }
        public CalibrationPhase CurrentPhase { get; private set; } = CalibrationPhase.NotStarted;
        public float PhaseElapsed { get; private set; }
        public float PhaseDuration { get; private set; }
        public float TotalElapsed { get; private set; }
        public float TotalDuration { get; private set; }

        private Coroutine? runningCoroutine;

        private static readonly CalibrationPhase[] Phases =
        {
            CalibrationPhase.StationaryBias,
            CalibrationPhase.StaticOrientations,
            CalibrationPhase.DynamicExcitation,
            CalibrationPhase.FinalStationary
        };

        public void StartCalibration()
        {
            if (IsRunning) return;
            if (recordingManager == null)
            {
                Debug.LogError($"[{Constants.LOG_TAG}] CalibrationSession: RecordingManager not assigned!");
                return;
            }

            TotalDuration = 0f;
            foreach (var d in phaseDurations) TotalDuration += d;

            runningCoroutine = StartCoroutine(RunCalibration());
        }

        public void CancelCalibration()
        {
            if (!IsRunning) return;

            Debug.Log($"[{Constants.LOG_TAG}] CalibrationSession: Cancelling calibration");

            if (runningCoroutine != null)
            {
                StopCoroutine(runningCoroutine);
                runningCoroutine = null;
            }

            if (recordingManager.IsRecording)
                recordingManager.StopRecording();

            IsRunning = false;
            CurrentPhase = CalibrationPhase.NotStarted;
            OnCalibrationCancelled?.Invoke();
        }

        private IEnumerator RunCalibration()
        {
            IsRunning = true;
            TotalElapsed = 0f;
            float totalStartTime = Time.time;

            Debug.Log($"[{Constants.LOG_TAG}] CalibrationSession: Starting calibration ({TotalDuration}s total)");

            recordingManager.StartRecording();

            if (!recordingManager.IsRecording)
            {
                Debug.LogError($"[{Constants.LOG_TAG}] CalibrationSession: Failed to start recording!");
                IsRunning = false;
                runningCoroutine = null;
                yield break;
            }

            for (int i = 0; i < Phases.Length && i < phaseDurations.Length; i++)
            {
                CurrentPhase = Phases[i];
                PhaseDuration = phaseDurations[i];
                PhaseElapsed = 0f;
                float phaseStartTime = Time.time;

                Debug.Log($"[{Constants.LOG_TAG}] CalibrationSession: Phase {i + 1}/{Phases.Length} - {CurrentPhase} ({PhaseDuration}s)");
                OnPhaseChanged?.Invoke(CurrentPhase);

                while (PhaseElapsed < PhaseDuration)
                {
                    if (!recordingManager.IsRecording)
                    {
                        Debug.LogWarning($"[{Constants.LOG_TAG}] CalibrationSession: Recording stopped externally, cancelling");
                        IsRunning = false;
                        CurrentPhase = CalibrationPhase.NotStarted;
                        runningCoroutine = null;
                        OnCalibrationCancelled?.Invoke();
                        yield break;
                    }

                    yield return new WaitForSeconds(0.1f);
                    PhaseElapsed = Time.time - phaseStartTime;
                    TotalElapsed = Time.time - totalStartTime;
                }
            }

            // All phases complete
            CurrentPhase = CalibrationPhase.Complete;
            OnPhaseChanged?.Invoke(CurrentPhase);

            Debug.Log($"[{Constants.LOG_TAG}] CalibrationSession: All phases complete, stopping recording");

            // Capture directory before StopRecording clears it
            string? sessionDirName = recordingManager.CurrentSessionDirectory;
            recordingManager.StopRecording();

            if (!string.IsNullOrEmpty(sessionDirName))
                WriteEpisodeId(sessionDirName!);

            IsRunning = false;
            runningCoroutine = null;
            OnCalibrationComplete?.Invoke();
        }

        private void WriteEpisodeId(string directoryName)
        {
            try
            {
                var sessionDir = Path.Join(Application.persistentDataPath, directoryName);
                var episodeIdPath = Path.Join(sessionDir, "episode_id.json");

                float totalDuration = 0f;
                foreach (var d in phaseDurations) totalDuration += d;

                var durationsStr = "[";
                for (int i = 0; i < phaseDurations.Length; i++)
                {
                    if (i > 0) durationsStr += ", ";
                    durationsStr += phaseDurations[i].ToString("F0");
                }
                durationsStr += "]";

                var json = "{\n" +
                    $"  \"purpose\": \"calibration\",\n" +
                    $"  \"protocol_version\": 1,\n" +
                    $"  \"phase_durations_s\": {durationsStr},\n" +
                    $"  \"total_duration_s\": {totalDuration:F0},\n" +
                    $"  \"created_utc\": \"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ}\"\n" +
                    "}";

                File.WriteAllText(episodeIdPath, json);
                Debug.Log($"[{Constants.LOG_TAG}] CalibrationSession: Wrote episode_id.json to '{episodeIdPath}'");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{Constants.LOG_TAG}] CalibrationSession: Failed to write episode_id.json: {ex.Message}");
            }
        }

        private void OnDestroy()
        {
            if (IsRunning)
                CancelCalibration();
        }
    }
}
