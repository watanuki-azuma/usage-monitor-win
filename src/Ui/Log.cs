// Tiny append-only log file for the running app (milestone M5).
//
// usage-monitor.log lives in the data dir next to config.json/snapshot.json and rotates at
// 512 KiB (one rotated generation, "usage-monitor.log.1"). Only poll-loop and provider-failure
// events are written here: never tokens, credentials or request bodies.
//
// Language level: C# 5.
using System;
using System.IO;
using System.Text;

namespace UsageMonitor
{
    internal sealed class Log
    {
        private const long MaxBytes = 512 * 1024;

        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        private readonly object gate = new object();
        private readonly string path;

        internal Log(string path)
        {
            this.path = path;
        }

        internal string Path
        {
            get { return path; }
        }

        internal void Info(string message)
        {
            Write("info", message);
        }

        internal void Warn(string message)
        {
            Write("warn", message);
        }

        internal void Error(string message)
        {
            Write("error", message);
        }

        private void Write(string level, string message)
        {
            if (message == null) message = "";
            try
            {
                lock (gate)
                {
                    Rotate();
                    File.AppendAllText(path,
                        Models.ToIso(DateTime.UtcNow) + " [" + level + "] " + message + "\r\n",
                        Utf8NoBom);
                }
            }
            catch (Exception) { /* logging must never break the app */ }
        }

        private void Rotate()
        {
            try
            {
                if (!File.Exists(path)) return;
                long length = new FileInfo(path).Length;
                if (length < MaxBytes) return;

                string rotated = path + ".1";
                if (File.Exists(rotated)) File.Delete(rotated);
                File.Move(path, rotated);
            }
            catch (Exception) { /* a failed rotation keeps appending to the same file */ }
        }
    }
}
