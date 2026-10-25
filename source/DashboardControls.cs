using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace WirelessInternet
{
    // Dashboard rendering only. Operation and monitoring state remain outside these controls.
    internal class RoundedPanel : Panel
    {
        public RoundedPanel() { DoubleBuffered = true; BackColor = UiVisualStyle.PanelColor; }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(UiVisualStyle.BackgroundColor);
            if (Width < 2 || Height < 2) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = UiDpi.For(this);
            using (GraphicsPath path = UiVisualStyle.RoundRect(new RectangleF(.5F * scale, .5F * scale, Width - scale, Height - scale), 10F * scale))
            using (SolidBrush fill = new SolidBrush(UiVisualStyle.PanelColor))
            using (Pen border = new Pen(UiVisualStyle.BorderColor, scale))
            { e.Graphics.FillPath(fill, path); e.Graphics.DrawPath(border, path); }
        }
    }

    internal sealed class MetricCard : RoundedPanel
    {
        private readonly Label _value;
        private readonly Label _caption;
        public MetricCard(string title, string value, string caption, Color color)
        {
            Padding = new Padding(16, 11, 12, 8);
            Label heading = UiVisualStyle.MakeLabel(title, 8.5F, UiVisualStyle.MutedColor, FontStyle.Regular);
            heading.Dock = DockStyle.Top;
            heading.Height = 22;
            _caption = UiVisualStyle.MakeLabel(caption, 8F, UiVisualStyle.MutedColor, FontStyle.Regular);
            _caption.Dock = DockStyle.Bottom;
            _caption.Height = 21;
            _caption.AutoEllipsis = true;
            _value = UiVisualStyle.MakeLabel(value, 22F, color, FontStyle.Bold);
            _value.Dock = DockStyle.Fill;
            _value.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(_value);
            Controls.Add(_caption);
            Controls.Add(heading);
        }
        public void SetValue(string value, string caption) { _value.Text = value; _caption.Text = caption; }
    }

    internal sealed class StatusLight : Control
    {
        private Color _color = UiVisualStyle.MutedColor;
        public Color LightColor { get { return _color; } set { _color = value; Invalidate(); } }
        public StatusLight() { SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true); BackColor = Color.Transparent; }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float inset = UiDpi.For(this);
            using (SolidBrush brush = new SolidBrush(_color)) e.Graphics.FillEllipse(brush, inset, inset, Width - 2 * inset, Height - 2 * inset);
        }
    }

    internal sealed class LogoMark : Control
    {
        public LogoMark() { DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw, true); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.ScaleTransform(Width / 43F, Height / 43F);
            using (GraphicsPath shape = UiVisualStyle.RoundRect(new RectangleF(0, 0, 42, 42), 10))
            using (SolidBrush fill = new SolidBrush(Color.FromArgb(22, 66, 65))) e.Graphics.FillPath(fill, shape);
            using (SolidBrush bars = new SolidBrush(UiVisualStyle.AccentColor))
            {
                e.Graphics.FillRectangle(bars, 10, 25, 4, 8);
                e.Graphics.FillRectangle(bars, 19, 18, 4, 15);
                e.Graphics.FillRectangle(bars, 28, 10, 4, 23);
            }
        }
    }

    internal sealed class SpeedChart : Control
    {
        private readonly List<double> _download = new List<double>();
        private readonly List<double> _upload = new List<double>();
        public SpeedChart() { DoubleBuffered = true; BackColor = UiVisualStyle.PanelColor; SetStyle(ControlStyles.ResizeRedraw, true); }
        public void AddSample(double download, double upload)
        {
            _download.Add(SafeValue(download));
            _upload.Add(SafeValue(upload));
            while (_download.Count > 60) { _download.RemoveAt(0); _upload.RemoveAt(0); }
            Invalidate();
        }
        public void ClearSamples() { _download.Clear(); _upload.Clear(); Invalidate(); }
        private static double SafeValue(double value) { return Double.IsNaN(value) || Double.IsInfinity(value) || value < 0 ? Double.NaN : value; }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            float scale = UiDpi.For(this);
            if (Width < 120 * scale || Height < 80 * scale) return;
            Graphics graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (Font heading = new Font("Microsoft YaHei UI", 12F * scale, FontStyle.Bold, GraphicsUnit.Pixel))
            using (Font small = new Font("Microsoft YaHei UI", (32F / 3F) * scale, FontStyle.Regular, GraphicsUnit.Pixel))
            using (SolidBrush text = new SolidBrush(UiVisualStyle.TextColor))
            using (SolidBrush muted = new SolidBrush(UiVisualStyle.MutedColor))
            {
                graphics.DrawString("实时网速", heading, text, 0, 0);
                graphics.DrawString("最近 60 次采样", small, muted, 85 * scale, 2 * scale);
                DrawLegend(graphics, "下载", UiVisualStyle.AccentColor, Width - 151 * scale, 8 * scale, small, scale);
                DrawLegend(graphics, "上传", UiVisualStyle.UploadColor, Width - 75 * scale, 8 * scale, small, scale);
                double maximum = 1024;
                for (int i = 0; i < _download.Count; i++)
                {
                    if (!Double.IsNaN(_download[i])) maximum = Math.Max(maximum, _download[i]);
                    if (!Double.IsNaN(_upload[i])) maximum = Math.Max(maximum, _upload[i]);
                }
                maximum *= 1.15;
                float axisWidth = 77 * scale;
                for (int row = 0; row <= 2; row++)
                    axisWidth = Math.Max(axisWidth, graphics.MeasureString(UiVisualStyle.FormatRate(maximum * (1 - row / 2.0)), small).Width + 14 * scale);
                RectangleF plot = new RectangleF(scale, 31 * scale, Width - axisWidth, Height - 51 * scale);
                using (Pen grid = new Pen(Color.FromArgb(36, 54, 71), scale))
                {
                    for (int row = 0; row <= 2; row++)
                    {
                        float y = plot.Top + plot.Height * row / 2;
                        graphics.DrawLine(grid, plot.Left, y, plot.Right, y);
                        graphics.DrawString(UiVisualStyle.FormatRate(maximum * (1 - row / 2.0)), small, muted, plot.Right + 9 * scale, y - 8 * scale);
                    }
                }
                DrawSeries(graphics, plot, _download, maximum, UiVisualStyle.AccentColor, true, scale);
                DrawSeries(graphics, plot, _upload, maximum, UiVisualStyle.UploadColor, false, scale);
                graphics.DrawString("较早", small, muted, plot.Left, plot.Bottom + 4 * scale);
                SizeF now = graphics.MeasureString("现在", small);
                graphics.DrawString("现在", small, muted, plot.Right - now.Width, plot.Bottom + 4 * scale);
            }
        }
        private static void DrawLegend(Graphics graphics, string label, Color color, float x, float y, Font font, float scale)
        {
            using (Pen pen = new Pen(color, 2 * scale)) graphics.DrawLine(pen, x, y + 2 * scale, x + 17 * scale, y + 2 * scale);
            using (SolidBrush brush = new SolidBrush(UiVisualStyle.MutedColor)) graphics.DrawString(label, font, brush, x + 23 * scale, y - 7 * scale);
        }
        private static void DrawSeries(Graphics graphics, RectangleF plot, List<double> values, double maximum, Color color, bool fill, float scale)
        {
            if (values.Count == 0) return;
            List<PointF> points = new List<PointF>();
            int offset = 60 - values.Count;
            for (int i = 0; i < values.Count; i++)
            {
                if (Double.IsNaN(values[i]))
                {
                    DrawSegment(graphics, plot, points.ToArray(), color, fill, scale);
                    points.Clear();
                }
                else points.Add(new PointF(plot.Left + plot.Width * (offset + i) / 59, plot.Bottom - (float)(plot.Height * values[i] / maximum)));
            }
            DrawSegment(graphics, plot, points.ToArray(), color, fill, scale);
        }
        private static void DrawSegment(Graphics graphics, RectangleF plot, PointF[] points, Color color, bool fill, float scale)
        {
            if (points.Length == 0) return;
            if (points.Length == 1)
            {
                using (SolidBrush dot = new SolidBrush(color)) graphics.FillEllipse(dot, points[0].X - 2 * scale, points[0].Y - 2 * scale, 4 * scale, 4 * scale);
                return;
            }
            if (fill)
            {
                using (GraphicsPath area = new GraphicsPath())
                {
                    area.AddLine(points[0].X, plot.Bottom, points[0].X, points[0].Y);
                    area.AddLines(points);
                    area.AddLine(points[points.Length - 1].X, points[points.Length - 1].Y, points[points.Length - 1].X, plot.Bottom);
                    area.CloseFigure();
                    using (LinearGradientBrush brush = new LinearGradientBrush(plot, Color.FromArgb(49, color), Color.FromArgb(3, color), LinearGradientMode.Vertical)) graphics.FillPath(brush, area);
                }
            }
            using (Pen line = new Pen(color, 2F * scale)) { line.LineJoin = LineJoin.Round; graphics.DrawLines(line, points); }
        }
    }

}
