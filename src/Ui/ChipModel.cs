// Chips-bar model (milestone M5). Pure: maps a snapshot onto what one pill shows, and produces a
// change key so the window repaints only when the pixels would actually differ.
// Port of reference/js/ui/chips.js.
// Language level: C# 5 (in-box csc.exe): no ?. , no $"" , no expression-bodied members, no out var.
using System;
using System.Text;

namespace UsageMonitor
{
    // What a single chip pill renders. Plain fields: the drawing code reads them directly.
    public class ChipInfo
    {
        public string Id;
        public string Text;
        public string ColorClass;
        public bool Stale;
    }

    public static class ChipModel
    {
        public const string ClassRed = "red";
        public const string ClassGreen = "green";
        public const string ClassGray = "gray";

        // Field / record separators: control characters can never appear in ids or rendered text.
        private const char SepField = '\u0001';
        private const char SepRecord = '\u0002';

        // ------------------------------------------------------------------------------------------
        // One chip per provider. Text is the current window ("42/100", "—" when there is none),
        // the colour is the alert colour (gray when nothing is known) and Stale mirrors the state
        // so the pill can draw its dashed border.
        // ------------------------------------------------------------------------------------------
        public static ChipInfo For(ProviderSnapshot p)
        {
            ChipInfo chip = new ChipInfo();
            if (p == null)
            {
                chip.Id = null;
                chip.Text = Format.FormatUsedTotal(null, true);
                chip.ColorClass = ClassGray;
                chip.Stale = false;
                return chip;
            }

            UsageWindow w = Models.CurrentWindow(p);
            chip.Id = p.Id;
            chip.Text = Format.FormatUsedTotal(w, true);
            if (w == null || !w.UsedPct.HasValue) chip.ColorClass = ClassGray;
            else if (Models.IsAlerting(w)) chip.ColorClass = ClassRed;
            else chip.ColorClass = ClassGreen;

            ProviderStatus status = p.Status;
            chip.Stale = status != null && status.State == State.Stale;
            return chip;
        }

        // ------------------------------------------------------------------------------------------
        // Opaque key covering exactly the visible state: id, text, colour class and stale flag, in
        // provider order. Timestamps and everything else the pills do not draw are deliberately
        // excluded so a fresh poll with identical numbers produces the same key.
        // ------------------------------------------------------------------------------------------
        public static string Key(Snapshot s)
        {
            if (s == null || s.Providers == null) return "";

            StringBuilder sb = new StringBuilder();
            foreach (ProviderSnapshot p in s.Providers)
            {
                if (sb.Length > 0) sb.Append(SepRecord);
                ChipInfo chip = For(p);
                sb.Append(chip.Id == null ? "" : chip.Id);
                sb.Append(SepField);
                sb.Append(chip.Text == null ? "" : chip.Text);
                sb.Append(SepField);
                sb.Append(chip.ColorClass == null ? "" : chip.ColorClass);
                sb.Append(SepField);
                sb.Append(chip.Stale ? "1" : "0");
            }
            return sb.ToString();
        }
    }
}
