// Usage history for other tools and models: one JSON object per line (JSONL), appended after a
// poll when something changed (plus a heartbeat). Numbers and states only - never tokens, hints or
// messages. Rotates at 4 MiB (the previous file is kept as <name>.1). Never throws.
//
// Line format:
//   {"t":"<utc iso>","poll_interval_secs":60,"providers":[{"id":"claude","state":"ok","plan":"Pro",
//    "source":"api","windows":[{"kind":"five_hour","label":"5h","used_pct":40,"remaining_pct":60,
//    "resets_at":"<utc iso>"}]}]}
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace UsageMonitor
{
    public sealed class UsageLog
    {
        public const long MaxBytes = 4L * 1024 * 1024;
        public static readonly TimeSpan Heartbeat = TimeSpan.FromMinutes(10);

        private readonly string path;
        private string lastFingerprint;
        private DateTime lastWriteUtc = DateTime.MinValue;

        public UsageLog(string path)
        {
            this.path = path;
        }

        // Returns true when a line was written.
        public bool Append(Snapshot snapshot, DateTime nowUtc)
        {
            try
            {
                if (snapshot == null || snapshot.Providers == null || string.IsNullOrEmpty(path)) return false;

                List<object> providers = new List<object>();
                StringBuilder fingerprint = new StringBuilder();
                foreach (ProviderSnapshot p in snapshot.Providers)
                {
                    if (p == null) continue;
                    Dictionary<string, object> entry = new Dictionary<string, object>();
                    string state = p.Status == null || p.Status.State == null ? State.Unknown : p.Status.State;
                    entry["id"] = p.Id;
                    entry["state"] = state;
                    entry["plan"] = p.Plan;
                    entry["source"] = p.Source;
                    fingerprint.Append(p.Id).Append('|').Append(state).Append('|').Append(p.Plan).Append('|');

                    List<object> windows = new List<object>();
                    if (p.Windows != null)
                    {
                        foreach (UsageWindow w in p.Windows)
                        {
                            if (w == null) continue;
                            Dictionary<string, object> item = new Dictionary<string, object>();
                            item["kind"] = w.Kind;
                            item["label"] = w.Label;
                            item["used_pct"] = w.UsedPct;
                            item["remaining_pct"] = w.RemainingPct;
                            item["resets_at"] = w.ResetsAt.HasValue ? Models.ToIso(w.ResetsAt.Value) : null;
                            windows.Add(item);
                            fingerprint.Append(w.Kind).Append(':').Append(w.Label).Append(':')
                                .Append(w.UsedPct.HasValue ? w.UsedPct.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : "-")
                                .Append(':').Append(w.ResetsAt.HasValue ? w.ResetsAt.Value.Ticks.ToString() : "-").Append(';');
                        }
                    }
                    entry["windows"] = windows;
                    fingerprint.Append('\n');
                    providers.Add(entry);
                }

                string print = fingerprint.ToString();
                bool unchanged = lastFingerprint != null && print == lastFingerprint;
                if (unchanged && nowUtc - lastWriteUtc < Heartbeat) return false;

                Dictionary<string, object> root = new Dictionary<string, object>();
                root["t"] = Models.ToIso(nowUtc);
                root["poll_interval_secs"] = snapshot.PollIntervalSecs;
                root["providers"] = providers;
                string line = new JavaScriptSerializer().Serialize(root);

                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                Rotate();
                File.AppendAllText(path, line + "\n", new UTF8Encoding(false));

                lastFingerprint = print;
                lastWriteUtc = nowUtc;
                return true;
            }
            catch (Exception)
            {
                return false; // a history log must never disturb polling
            }
        }

        private void Rotate()
        {
            FileInfo info = new FileInfo(path);
            if (!info.Exists || info.Length <= MaxBytes) return;
            string old = path + ".1";
            if (File.Exists(old)) File.Delete(old);
            File.Move(path, old);
        }
    }
}
