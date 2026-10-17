// --render-test: renders chips.png and flyout.png from a snapshot without starting the app
// (milestone M5, SPEC section 6).
//
// No window is shown, no network is touched, no credentials are read. The snapshot comes from
// <data-dir>\snapshot.json when that file exists, otherwise from fixtures\snapshot-sample.json
// searched upward from the folder the exe lives in. The process exits 0 within a few seconds.
//
// Language level: C# 5.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace UsageMonitor
{
    internal static class RenderTest
    {
        internal static int Run(string outDir, string dataDir, string exeDir, string skin)
        {
            try
            {
                if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);

                Snapshot snapshot = LoadSnapshot(dataDir, exeDir);
                WriteChips(Path.Combine(outDir, "chips.png"), snapshot, skin);
                WriteFlyout(Path.Combine(outDir, "flyout.png"), snapshot, skin);
                return 0;
            }
            catch (Exception ex)
            {
                try
                {
                    File.WriteAllText(Path.Combine(outDir, "render-test-error.txt"), ex.ToString());
                }
                catch (Exception) { /* nowhere left to complain */ }
                return 1;
            }
        }

        private static Snapshot LoadSnapshot(string dataDir, string exeDir)
        {
            if (dataDir != null)
            {
                string candidate = Path.Combine(dataDir, "snapshot.json");
                if (File.Exists(candidate))
                {
                    Snapshot parsed = SnapshotJson.Parse(ReadAllText(candidate));
                    if (parsed != null) return parsed;
                }
            }

            DirectoryInfo dir = new DirectoryInfo(exeDir == null ? "." : exeDir);
            while (dir != null)
            {
                string fixture = Path.Combine(dir.FullName, "fixtures", "snapshot-sample.json");
                if (File.Exists(fixture))
                {
                    Snapshot parsed = SnapshotJson.Parse(ReadAllText(fixture));
                    if (parsed != null) return parsed;
                }
                dir = dir.Parent;
            }
            return null;
        }

        private static string ReadAllText(string path)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void WriteChips(string path, Snapshot snapshot, string skin)
        {
            using (ChipsFormBase form = SkinRegistry.CreateChips(skin, null))
            {
                form.SetSnapshot(snapshot);
                Size size = form.MeasureSize();
                using (Bitmap bitmap = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height)))
                {
                    using (Graphics g = Graphics.FromImage(bitmap))
                    {
                        form.PaintInto(g, size);
                    }
                    bitmap.Save(path, ImageFormat.Png);
                }
            }
        }

        private static void WriteFlyout(string path, Snapshot snapshot, string skin)
        {
            using (FlyoutFormBase form = SkinRegistry.CreateFlyout(skin, null))
            {
                form.SetSnapshot(snapshot);
                Size size = form.MeasureSize();
                using (Bitmap bitmap = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height)))
                {
                    using (Graphics g = Graphics.FromImage(bitmap))
                    {
                        form.PaintInto(g, size);
                    }
                    bitmap.Save(path, ImageFormat.Png);
                }
            }
        }
    }
}
