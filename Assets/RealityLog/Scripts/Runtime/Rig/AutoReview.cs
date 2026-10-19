# nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using RealityLog.Common;

namespace RealityLog.Rig
{
    /// <summary>
    /// Reviews every episode the headset sealed as a success, since no one reviews on a headset:
    /// the operator never sees the display, and a sealed episode already passed the delivery
    /// validator on the headset. Each review is the rig's collector command
    /// <c>review_success</c> with an id derived from the episode, so a retry is the same command;
    /// it is sent only on a Factory with a collector signed in, retried while the Factory has not
    /// observed the episode yet, and given up after <see cref="GiveUpAfter"/> (an episode recorded
    /// local only is never observed). Reviewed episodes are kept in <c>auto_reviewed.json</c>.
    /// Any thread; one review at a time.
    /// </summary>
    public sealed class AutoReview
    {
        private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(5);

        private readonly string storePath;
        private readonly object gate = new();
        private readonly HashSet<string> reviewed;
        private readonly Dictionary<string, (DateTime first, DateTime last)> tries = new();
        private int busy;

        public AutoReview(string dataPath)
        {
            storePath = Path.Combine(dataPath, "auto_reviewed.json");
            reviewed = Load(storePath);
        }

        /// <summary>
        /// Reviews the next sealed, unreviewed episode of <paramref name="status"/> (sx_rig_status),
        /// when the rig is on a Factory with a collector signed in. Blocks on the network: call it
        /// off Unity's main thread.
        /// </summary>
        public void Pass(JObject status, bool onFactory)
        {
            if (!onFactory || RigStatusJson.CollectorSignedIn(status) != true) return;
            if (status["seals"] is not JObject seals) return;
            if (System.Threading.Interlocked.Exchange(ref busy, 1) == 1) return;
            try
            {
                var now = DateTime.UtcNow;
                foreach (var entry in seals)
                {
                    var episode = entry.Key;
                    if (RigStatusJson.SealState(entry.Value) != "sealed") continue;
                    lock (gate)
                    {
                        if (reviewed.Contains(episode)) continue;
                        if (tries.TryGetValue(episode, out var tried)
                            && (now - tried.last < RetryEvery || now - tried.first > GiveUpAfter)) continue;
                        tries[episode] = (tries.TryGetValue(episode, out var t) ? t.first : now, now);
                    }
                    Review(episode);
                    return; // one network call per pass
                }
            }
            finally
            {
                System.Threading.Volatile.Write(ref busy, 0);
            }
        }

        private void Review(string episode)
        {
            var command = new JObject
            {
                ["command"] = "review_success",
                ["episode_id"] = episode,
                ["command_id"] = CommandId(episode).ToString(),
            };
            try
            {
                SxRig.Command(command.ToString(Formatting.None));
                lock (gate)
                {
                    reviewed.Add(episode);
                    tries.Remove(episode);
                    Save();
                }
                Debug.Log($"[{Constants.LOG_TAG}] AutoReview: {episode} reviewed as a success");
            }
            catch (SxRigException ex)
            {
                // Not observed by the Factory yet, or an outage: the next pass retries.
                Debug.Log($"[{Constants.LOG_TAG}] AutoReview: {episode} not yet: {ex.Message}");
            }
        }

        /// <summary>A UUID that is the same for every review of one episode.</summary>
        private static Guid CommandId(string episode)
        {
            using var md5 = MD5.Create();
            var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes("sx-oqc/auto-review/v1\n" + episode));
            bytes[6] = (byte)((bytes[6] & 0x0F) | 0x30); // version 3 (name-based, MD5)
            bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 4122 variant
            return new Guid(BitConverter.ToString(bytes).Replace("-", ""));
        }

        private static HashSet<string> Load(string path)
        {
            try
            {
                return File.Exists(path)
                    ? new HashSet<string>(JArray.Parse(File.ReadAllText(path)).Values<string>().OfType<string>())
                    : new HashSet<string>();
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
            {
                return new HashSet<string>();
            }
        }

        private void Save()
        {
            try
            {
                File.WriteAllText(storePath, new JArray(reviewed).ToString(Formatting.None));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] AutoReview: cannot keep {storePath}: {ex.Message}");
            }
        }
    }
}
