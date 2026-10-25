using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace WirelessInternet
{
    // Shared immutable visual values and the original dashboard rendering helpers.
    internal static class UiVisualStyle
    {
        internal static readonly Color BackgroundColor = Color.FromArgb(12, 20, 31);
        internal static readonly Color PanelColor = Color.FromArgb(20, 31, 45);
        internal static readonly Color BorderColor = Color.FromArgb(40, 57, 75);
        internal static readonly Color TextColor = Color.FromArgb(235, 243, 250);
        internal static readonly Color MutedColor = Color.FromArgb(146, 166, 186);
        internal static readonly Color AccentColor = Color.FromArgb(48, 216, 192);
        internal static readonly Color UploadColor = Color.FromArgb(113, 161, 255);

        internal static Label MakeLabel(string text, float size, Color color, FontStyle style)
        {
            return new Label { Text = text, ForeColor = color, BackColor = Color.Transparent, Font = new Font("Microsoft YaHei UI", size, style), AutoSize = false };
        }

        internal static string FormatBytes(ulong bytes)
        {
            return FormatSize((double)bytes, false);
        }

        internal static string FormatRate(double bytes)
        {
            if (Double.IsNaN(bytes) || Double.IsInfinity(bytes) || bytes < 0) return "—";
            return FormatSize(bytes, true);
        }

        internal static string FormatSize(double bytes, bool rate)
        {
            if (Double.IsNaN(bytes) || Double.IsInfinity(bytes) || bytes < 0) bytes = 0;
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int index = 0;
            while (bytes >= 1024 && index < units.Length - 1) { bytes /= 1024; index++; }
            return bytes.ToString(index == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[index] + (rate ? "/s" : String.Empty);
        }

        internal static GraphicsPath RoundRect(RectangleF rectangle, float radius)
        {
            float diameter = radius * 2;
            GraphicsPath result = new GraphicsPath();
            result.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
            result.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90);
            result.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
            result.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
            result.CloseFigure();
            return result;
        }
    }
}
