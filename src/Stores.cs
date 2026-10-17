// File-backed stores. Port of readJson/writeJson in reference/js/cache.js (milestone M4):
// the directory is created on demand, data goes to "<path>.tmp" first and is then renamed over the
// destination, so no reader ever sees a half-written file and no .tmp is ever left behind.
// Writes are UTF-8 without a BOM. A missing or corrupt file loads as "nothing" (null snapshot /
// empty alert state) instead of throwing. Language level: C# 5 (in-box csc.exe).
using System;
using System.IO;
using System.Text;

namespace UsageMonitor
{
    public class FileSnapshotStore : ISnapshotStore
    {
        private readonly string path;

        public FileSnapshotStore(string path)
        {
            this.path = path;
        }

        // null when missing, unreadable or invalid (same as cache.js loadSnapshot).
        public Snapshot Load()
        {
            string text = AtomicFile.Read(path);
            if (text == null) return null;
            return SnapshotJson.Parse(text);
        }

        // Atomic (tmp + replace); may throw only when the filesystem itself refuses the write.
        public void Save(Snapshot snapshot)
        {
            string json = SnapshotJson.Serialize(snapshot);
            if (json == null) return;
            AtomicFile.Write(path, json);
        }
    }

    public class FileAlertStore
    {
        private readonly string path;

        public FileAlertStore(string path)
        {
            this.path = path;
        }

        // Missing or corrupt files yield an empty state (never null).
        public AlertState Load()
        {
            string text = AtomicFile.Read(path);
            if (text == null) return new AlertState();
            return AlertEngine.ParseState(text);
        }

        public void Save(AlertState state)
        {
            AtomicFile.Write(path, AlertEngine.SerializeState(state));
        }
    }

    // Shared tmp-file write used by both stores.
    internal static class AtomicFile
    {
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        // Returns null when the file is absent or cannot be read.
        internal static string Read(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                return File.ReadAllText(path);
            }
            catch (Exception) { return null; }
        }

        internal static void Write(string path, string content)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("path is required");

            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            string tmp = path + ".tmp";
            // The temp file has to be complete first: if it cannot be written we clean it up and
            // propagate, because a partial .tmp must never reach the destination.
            try
            {
                File.WriteAllText(tmp, content, Utf8NoBom);
            }
            catch (Exception)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); }
                catch (Exception) { }
                throw;
            }

            try
            {
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch (Exception)
            {
                // File.Replace can fail on unusual filesystems; a plain overwrite still works and
                // must not leave the .tmp behind either. The fallback is only used for the replace
                // step, and only with a complete temp file.
                bool recovered = false;
                try
                {
                    if (File.Exists(tmp))
                    {
                        File.Copy(tmp, path, true);
                        File.Delete(tmp);
                        recovered = true;
                    }
                }
                catch (Exception) { recovered = false; }
                if (!recovered)
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); }
                    catch (Exception) { }
                    throw;
                }
            }
        }
    }
}
