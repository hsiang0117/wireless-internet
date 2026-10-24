using System;
using System.Drawing;
using System.Windows.Forms;

namespace WirelessInternet
{
    internal static class UiDpi
    {
        // .NET Framework 4.8 keeps explicitly assigned point fonts at system DPI.
        // Use the same system-aware space for controls and custom painting;
        // Windows scales the complete window coherently on other monitors.
        public static float For(Control control)
        {
            Form form = control.FindForm();
            // In Framework system-aware mode child DeviceDpi can remain 96.
            // CurrentAutoScaleDimensions is the exact DPI space used by the form's layout.
            float dpi = form != null && form.AutoScaleMode == AutoScaleMode.Dpi ? form.CurrentAutoScaleDimensions.Width : control.DeviceDpi;
            return Math.Max(1F, dpi / 96F);
        }
        public static int Scale(Control control, int logical)
        {
            return (int)Math.Round(logical * For(control), MidpointRounding.AwayFromZero);
        }
        static Size DeviceSize(Control control, Size logical)
        {
            return new Size(Scale(control, logical.Width), Scale(control, logical.Height));
        }
        public static void FitToWorkingArea(Form form, Size preferredLogical, Size minimumLogical)
        {
            Rectangle area = Screen.FromControl(form).WorkingArea;
            Size room = new Size(Math.Max(1, area.Width - Scale(form, 16)), Math.Max(1, area.Height - Scale(form, 16)));
            Size minimum = DeviceSize(form, minimumLogical), preferred = DeviceSize(form, preferredLogical);
            // Content can scroll when a monitor has fewer usable logical pixels than the design.
            form.MinimumSize = new Size(Math.Min(minimum.Width, room.Width), Math.Min(minimum.Height, room.Height));
            form.Size = new Size(Math.Min(preferred.Width, room.Width), Math.Min(preferred.Height, room.Height));
        }
        public static void ClampToWorkingArea(Form form, Size minimumLogical)
        {
            Rectangle area = Screen.FromControl(form).WorkingArea;
            Size room = new Size(Math.Max(1, area.Width - Scale(form, 16)), Math.Max(1, area.Height - Scale(form, 16)));
            Size minimum = DeviceSize(form, minimumLogical);
            form.MinimumSize = new Size(Math.Min(minimum.Width, room.Width), Math.Min(minimum.Height, room.Height));
            if (form.WindowState != FormWindowState.Normal) return;
            Size size = new Size(Math.Min(form.Width, room.Width), Math.Min(form.Height, room.Height));
            form.Bounds = new Rectangle(Math.Max(area.Left, Math.Min(form.Left, area.Right - size.Width)),
                Math.Max(area.Top, Math.Min(form.Top, area.Bottom - size.Height)), size.Width, size.Height);
        }
    }
}
