// Common bases of the switchable skins. Every member that code outside the form classes
// (AppController, RenderTest, tests) uses lives here; each skin's ChipsForm / FlyoutForm derives
// from these and keeps its own drawing.
//
// Language level: C# 5.
using System;
using System.Drawing;
using System.Windows.Forms;

namespace UsageMonitor
{
    internal abstract class ChipsFormBase : Form
    {
        internal abstract void SetSnapshot(Snapshot snapshot);
        internal abstract Size MeasureSize();
        internal abstract void PaintInto(Graphics g, Size size);
        internal abstract void Place();
        internal abstract void PlaceDocked();
    }

    internal abstract class FlyoutFormBase : Form
    {
        internal abstract void SetSnapshot(Snapshot snapshot);
        internal abstract void UpdateEtas(DateTime nowUtc);
        internal abstract Size MeasureSize();
        internal abstract void PaintInto(Graphics g, Size size);
        internal abstract void Place();
    }
}
