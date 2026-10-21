using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HassWidgets
{
    // One borderless, self-painted window per widget. Everything is drawn
    // relative to the client size, so resizing the window scales the content.
    class WidgetForm : Form
    {
        const string FontName = "Segoe UI";
        const string ValueFontName = "Segoe UI Semibold";
        const int MaxSamples = 50000;

        // Live data for one entity shown by the widget.
        class Series
        {
            public string EntityId;
            public EntityState State;
            public string Error;
            public double Value;
            public bool HasValue;
            public bool NeedHistory = true;
            public List<Sample> Samples = new List<Sample>();
        }

        readonly App app;
        readonly WidgetConfig cfg;
        readonly Timer timer = new Timer();

        List<Series> series = new List<Series>();
        string setupError;      // nothing to poll: no entity chosen, or no connection
        bool busy;
        bool pollAgain;

        Color back, text, title, entity, accent;
        SolidBrush textBrush, dimBrush, titleBrush, entityBrush, accentBrush;

        public WidgetConfig Cfg { get { return cfg; } }

        public WidgetForm(App app, WidgetConfig cfg)
        {
            this.app = app;
            this.cfg = cfg;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            MaximizeBox = false;
            StartPosition = FormStartPosition.Manual;
            MinimumSize = new Size(48, 32);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            timer.Tick += (s, e) => Poll();
            ApplyConfig();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW: keep widgets out of Alt+Tab
                return cp;
            }
        }

        bool IsGraph
        {
            get { return cfg.Mode == DisplayModes.Line || cfg.Mode == DisplayModes.Bar; }
        }

        // Pushes the current config into the window and refreshes the data.
        public void ApplyConfig()
        {
            cfg.PollSeconds = Math.Max(1, Math.Min(86400, cfg.PollSeconds));
            cfg.HistoryHours = Math.Max(0.1, Math.Min(720, cfg.HistoryHours));
            cfg.Opacity = Math.Max(20, Math.Min(100, cfg.Opacity));

            var bounds = new Rectangle(cfg.X, cfg.Y, Math.Max(MinimumSize.Width, cfg.W), Math.Max(MinimumSize.Height, cfg.H));
            bool visible = false;
            foreach (Screen s in Screen.AllScreens)
                if (s.WorkingArea.IntersectsWith(bounds)) visible = true;
            if (!visible)
            {
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                bounds.Location = new Point(wa.X + 40, wa.Y + 40);
            }
            Bounds = bounds;

            back = Colors.Parse(cfg.BackColor, Color.FromArgb(28, 31, 38));
            text = Colors.Parse(cfg.TextColor, Color.White);
            title = string.IsNullOrEmpty(cfg.TitleColor) ? Colors.DefaultTitle(text, back) : Colors.Parse(cfg.TitleColor, text);
            entity = string.IsNullOrEmpty(cfg.EntityColor) ? title : Colors.Parse(cfg.EntityColor, title);
            accent = Colors.Parse(cfg.AccentColor, Color.DeepSkyBlue);
            DisposeBrushes();
            textBrush = new SolidBrush(text);
            dimBrush = new SolidBrush(Color.FromArgb(170, text));
            titleBrush = new SolidBrush(title);
            entityBrush = new SolidBrush(entity);
            accentBrush = new SolidBrush(accent);
            Opacity = cfg.Opacity / 100.0;
            TopMost = cfg.TopMost;

            // Entities that stay keep the data they already have.
            var next = new List<Series>();
            foreach (string id in cfg.AllEntities())
            {
                Series s = series.Find(x => x.EntityId == id);
                if (s == null) s = new Series { EntityId = id };
                if (!IsGraph) s.Samples = new List<Sample>();
                next.Add(s);
            }
            series = next;

            timer.Interval = cfg.PollSeconds * 1000;
            timer.Start();
            Invalidate();
            Reload();
        }

        // Re-reads history and the current state.
        public void Reload()
        {
            foreach (Series s in series) s.NeedHistory = true;
            Poll();
        }

        async void Poll()
        {
            if (IsDisposed) return;
            if (busy) { pollAgain = true; return; }
            setupError = series.Count == 0 ? "No entity selected"
                : !Hass.IsConfigured ? "Not connected - set the URL and token"
                : null;
            if (setupError != null) { Invalidate(); return; }

            busy = true;
            try
            {
                foreach (Series s in series.ToArray())
                {
                    await PollOne(s);
                    if (IsDisposed) return;
                }
            }
            finally
            {
                busy = false;
            }
            Invalidate();
            if (pollAgain) { pollAgain = false; Poll(); }
        }

        async Task PollOne(Series s)
        {
            try
            {
                if (s.NeedHistory && IsGraph)
                {
                    s.NeedHistory = false;
                    try
                    {
                        s.Samples = await Hass.GetHistory(s.EntityId, cfg.HistoryHours);
                    }
                    catch (Exception)
                    {
                        // The recorder may be disabled; the graph then fills from live polls.
                    }
                }

                EntityState st = await Hass.GetState(s.EntityId);
                s.State = st;
                s.Error = null;
                s.HasValue = Hass.TryValue(st.State, out s.Value);
                if (IsGraph && s.HasValue)
                {
                    s.Samples.Add(new Sample { T = DateTime.UtcNow, V = s.Value });
                    TrimSamples(s.Samples);
                }
            }
            catch (Exception ex)
            {
                s.Error = Hass.Describe(ex);
            }
        }

        // Drops samples older than the window but keeps the newest of them, so
        // the value in effect at the left edge of the graph is still known.
        void TrimSamples(List<Sample> samples)
        {
            DateTime cut = DateTime.UtcNow.AddHours(-cfg.HistoryHours);
            int k = 0;
            while (k + 1 < samples.Count && samples[k + 1].T < cut) k++;
            if (samples.Count - k > MaxSamples) k = samples.Count - MaxSamples;
            if (k > 0) samples.RemoveRange(0, k);
        }

        // ---- Painting -------------------------------------------------------

        // Name of one entity: the display name typed for it, else its Home Assistant
        // name. In a multi-entity widget the device name and the widget title are
        // dropped from the front: "Hall Sensor Humidity" -> "Humidity".
        string Label(Series s)
        {
            string custom;
            if (cfg.Labels != null && cfg.Labels.TryGetValue(s.EntityId, out custom) && !string.IsNullOrEmpty(custom))
                return custom;
            string name = s.State != null && s.State.FriendlyName.Length > 0 ? s.State.FriendlyName : s.EntityId;
            if (series.Count > 1) name = WithoutPrefix(WithoutPrefix(name, cfg.DeviceName), cfg.Title);
            return name;
        }

        static string WithoutPrefix(string name, string prefix)
        {
            if (!string.IsNullOrEmpty(prefix) && name.Length > prefix.Length + 1 &&
                name.StartsWith(prefix + " ", StringComparison.CurrentCultureIgnoreCase))
                return name.Substring(prefix.Length + 1);
            return name;
        }

        string UnitText(Series s)
        {
            if (!cfg.ShowUnit || s.State == null) return "";
            if (!string.IsNullOrEmpty(cfg.Unit)) return cfg.Unit;
            return s.State.Unit;
        }

        string FormatNumber(double v)
        {
            if (cfg.Decimals >= 0) return v.ToString("F" + Math.Min(cfg.Decimals, 10), CultureInfo.CurrentCulture);
            return v.ToString("0.###", CultureInfo.CurrentCulture);
        }

        string ValueText(Series s)
        {
            if (s.State == null) return s.Error == null ? "..." : "-";
            double v;
            if (cfg.Decimals >= 0 && double.TryParse(s.State.State, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                return FormatNumber(v);
            return s.State.State;
        }

        static float Clamp(float v, float lo, float hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(back);

            float w = ClientSize.Width, h = ClientSize.Height;
            float pad = Math.Max(4f, Math.Min(w, h) * 0.08f);
            var area = new RectangleF(pad, pad, w - 2 * pad, h - 2 * pad);
            if (area.Width < 8 || area.Height < 8) return;

            if (setupError != null)
            {
                DrawMessage(g, setupError, area);
                return;
            }
            if (series.Exists(s => s.Error != null))
            {
                float r = Clamp(h * 0.035f, 3, 7);
                using (var red = new SolidBrush(Color.FromArgb(235, 80, 70)))
                    g.FillEllipse(red, w - pad * 0.5f - 2 * r, pad * 0.5f, 2 * r, 2 * r);
            }

            if (series.Count == 1)
            {
                string label = !string.IsNullOrEmpty(cfg.Title) ? cfg.Title : Label(series[0]);
                DrawCell(g, area, series[0], cfg.ShowTitle ? label : null, titleBrush);
                return;
            }

            // Several entities: an optional widget title, then one row or cell each.
            if (cfg.ShowTitle && !string.IsNullOrEmpty(cfg.Title))
            {
                float headH = Clamp(h * 0.13f, 11, 30);
                DrawHeader(g, new RectangleF(area.X, area.Y, area.Width, headH), cfg.Title, titleBrush, null);
                float used = headH + pad * 0.4f;
                area = new RectangleF(area.X, area.Y + used, area.Width, area.Height - used);
                if (area.Height < 8) return;
            }

            int n = series.Count;
            if (cfg.Mode == DisplayModes.Number)
            {
                DrawRows(g, area);
                return;
            }

            // Graphs stack vertically; gauges use the grid that gives the largest dials.
            int cols = 1;
            if (cfg.Mode == DisplayModes.Gauge)
            {
                float best = 0;
                for (int c = 1; c <= n; c++)
                {
                    int r = (n + c - 1) / c;
                    float size = Math.Min(area.Width / c, area.Height / r);
                    if (size > best) { best = size; cols = c; }
                }
            }
            int rows = (n + cols - 1) / cols;
            float cw = area.Width / cols, ch = area.Height / rows, gap = pad * 0.35f;
            for (int i = 0; i < n; i++)
            {
                var cell = new RectangleF(area.X + (i % cols) * cw + gap / 2, area.Y + (i / cols) * ch + gap / 2, cw - gap, ch - gap);
                if (cell.Width < 8 || cell.Height < 8) continue;
                DrawCell(g, cell, series[i], Label(series[i]), entityBrush);
            }
        }

        // One entity in the widget's display mode, with an optional heading.
        void DrawCell(Graphics g, RectangleF area, Series s, string label, Brush labelBrush)
        {
            bool graph = IsGraph;
            if (graph || label != null)
            {
                float refH = area.Height * 1.19f;
                float headH = cfg.Mode == DisplayModes.Number ? Clamp(refH * 0.22f, 12, 44) : Clamp(refH * 0.16f, 10, 32);
                string value = null;
                if (graph && s.State != null)
                {
                    string unit = UnitText(s);
                    value = ValueText(s) + (unit.Length > 0 ? " " + unit : "");
                }
                DrawHeader(g, new RectangleF(area.X, area.Y, area.Width, headH), label, labelBrush, value);
                float used = headH + area.Height * 0.03f;
                area = new RectangleF(area.X, area.Y + used, area.Width, area.Height - used);
                if (area.Height < 6) return;
            }

            if (s.State == null && s.Error != null) DrawMessage(g, s.Error, area);
            else if (cfg.Mode == DisplayModes.Line) DrawSeries(g, area, s, false);
            else if (cfg.Mode == DisplayModes.Bar) DrawSeries(g, area, s, true);
            else if (cfg.Mode == DisplayModes.Gauge) DrawGauge(g, area, s);
            else DrawValue(g, area, s);
        }

        // Heading line: label on the left, optional live value on the right.
        void DrawHeader(Graphics g, RectangleF rect, string label, Brush labelBrush, string value)
        {
            using (var font = new Font(FontName, rect.Height * 0.7f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var sf = new StringFormat(StringFormatFlags.NoWrap))
            {
                sf.Trimming = StringTrimming.EllipsisCharacter;
                sf.LineAlignment = StringAlignment.Center;
                float right = rect.Right;
                if (value != null)
                {
                    SizeF sz = g.MeasureString(value, font);
                    if (sz.Width < rect.Width)
                    {
                        g.DrawString(value, font, accentBrush, right - sz.Width, rect.Y + (rect.Height - sz.Height) / 2);
                        right -= sz.Width + 2;
                    }
                }
                if (label != null && right - rect.X > 10)
                    g.DrawString(label, font, labelBrush, new RectangleF(rect.X, rect.Y, right - rect.X, rect.Height), sf);
            }
        }

        void DrawMessage(Graphics g, string message, RectangleF area)
        {
            using (var font = new Font(FontName, Clamp(area.Height * 0.2f, 9, 16), FontStyle.Regular, GraphicsUnit.Pixel))
            using (var sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                sf.Trimming = StringTrimming.EllipsisCharacter;
                g.DrawString(message, font, dimBrush, area, sf);
            }
        }

        static float Ascent(Font f)
        {
            FontFamily ff = f.FontFamily;
            return f.Size * ff.GetCellAscent(f.Style) / ff.GetEmHeight(f.Style);
        }

        // Number mode with several entities: one "name ... value unit" line each.
        void DrawRows(Graphics g, RectangleF area)
        {
            float rowH = area.Height / series.Count;
            // Sized by the row, but held back on narrow widgets so the names still fit.
            float size = Clamp(Math.Min(rowH * 0.55f, area.Width * 0.075f), 7, 48);
            StringFormat tight = StringFormat.GenericTypographic;
            using (var labelFont = new Font(FontName, size, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var valueFont = new Font(ValueFontName, size, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var unitFont = new Font(FontName, size * 0.72f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var sf = new StringFormat(StringFormatFlags.NoWrap))
            {
                sf.Trimming = StringTrimming.EllipsisCharacter;
                sf.LineAlignment = StringAlignment.Center;
                float lineH = valueFont.GetHeight(g);
                for (int i = 0; i < series.Count; i++)
                {
                    Series s = series[i];
                    float y = area.Y + i * rowH;
                    float textY = y + (rowH - lineH) / 2;
                    string value = ValueText(s), unit = UnitText(s);
                    float right = area.Right;
                    if (unit.Length > 0)
                    {
                        right -= g.MeasureString(unit, unitFont, PointF.Empty, tight).Width;
                        g.DrawString(unit, unitFont, dimBrush, right, textY + Ascent(valueFont) - Ascent(unitFont), tight);
                        right -= size * 0.18f;
                    }
                    right -= g.MeasureString(value, valueFont, PointF.Empty, tight).Width;
                    g.DrawString(value, valueFont, textBrush, Math.Max(area.X, right), textY, tight);
                    float labelW = right - size * 0.6f - area.X;
                    if (labelW > 10) g.DrawString(Label(s), labelFont, entityBrush, new RectangleF(area.X, y, labelW, rowH), sf);
                }
            }
        }

        // Value plus a half-size unit, scaled to fill 'area' and centred in it.
        void DrawValue(Graphics g, RectangleF area, Series s)
        {
            string vt = ValueText(s);
            string unit = UnitText(s);
            const float Ref = 100f;
            StringFormat sf = StringFormat.GenericTypographic;

            float valueW, unitW = 0, lineH, gap = 0;
            using (var f = new Font(ValueFontName, Ref, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                valueW = g.MeasureString(vt, f, PointF.Empty, sf).Width;
                lineH = f.GetHeight(g);
            }
            if (unit.Length > 0)
            {
                using (var f = new Font(FontName, Ref / 2, FontStyle.Regular, GraphicsUnit.Pixel))
                    unitW = g.MeasureString(unit, f, PointF.Empty, sf).Width;
                gap = Ref * 0.12f;
            }
            float totalW = Math.Max(1f, valueW + gap + unitW);
            float scale = Math.Min(area.Width / totalW, area.Height / lineH);
            float size = Ref * scale;
            if (size < 5) return;

            float x = area.X + (area.Width - totalW * scale) / 2;
            float y = area.Y + (area.Height - lineH * scale) / 2;
            using (var f = new Font(ValueFontName, size, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                g.DrawString(vt, f, textBrush, x, y, sf);
                if (unit.Length > 0)
                {
                    using (var uf = new Font(FontName, size / 2, FontStyle.Regular, GraphicsUnit.Pixel))
                        g.DrawString(unit, uf, dimBrush, x + (valueW + gap) * scale, y + Ascent(f) - Ascent(uf), sf);
                }
            }
        }

        void DrawGauge(Graphics g, RectangleF area, Series s)
        {
            const float Start = 135f, Sweep = 270f;
            const float HeightRatio = 0.854f; // a 270 degree arc covers (1 + sin 45) / 2 of its diameter
            float d = Math.Min(area.Width, area.Height / HeightRatio);
            if (d < 16) { DrawValue(g, area, s); return; }
            float thick = d * 0.1f;
            float left = area.X + (area.Width - d) / 2;
            float top = area.Y + (area.Height - d * HeightRatio) / 2;
            var arc = new RectangleF(left + thick / 2, top + thick / 2, d - thick, d - thick);

            double lo = cfg.Min.HasValue ? cfg.Min.Value : 0;
            double hi = cfg.Max.HasValue ? cfg.Max.Value : 100;
            double frac = s.HasValue && hi > lo ? (s.Value - lo) / (hi - lo) : 0;
            frac = Math.Max(0, Math.Min(1, frac));

            using (var track = new Pen(Color.FromArgb(45, text), thick))
            using (var fill = new Pen(accent, thick))
            {
                track.StartCap = track.EndCap = LineCap.Round;
                fill.StartCap = fill.EndCap = LineCap.Round;
                g.DrawArc(track, arc, Start, Sweep);
                if (frac > 0.001) g.DrawArc(fill, arc, Start, (float)(Sweep * frac));
            }
            DrawValue(g, new RectangleF(left + d * 0.22f, top + d * 0.33f, d * 0.56f, d * 0.34f), s);
        }

        // Splits the time window into n buckets. A bucket holds the mean of its
        // samples, or carries the previous value forward, because Home Assistant
        // only records a state when it changes. NaN = nothing known yet.
        static double[] Resample(List<Sample> samples, int n, DateTime start, DateTime end)
        {
            var sum = new double[n];
            var last = new double[n];
            var count = new int[n];
            double span = (end - start).TotalSeconds;
            double carry = double.NaN;
            foreach (Sample s in samples)
            {
                if (s.T < start) { carry = s.V; continue; }
                int i = (int)((s.T - start).TotalSeconds / span * n);
                if (i >= n) i = n - 1;
                sum[i] += s.V;
                last[i] = s.V;
                count[i]++;
            }
            var result = new double[n];
            for (int i = 0; i < n; i++)
            {
                if (count[i] > 0)
                {
                    result[i] = sum[i] / count[i];
                    carry = last[i];
                }
                else result[i] = carry;
            }
            return result;
        }

        void DrawSeries(Graphics g, RectangleF area, Series s, bool bars)
        {
            DateTime end = DateTime.UtcNow;
            DateTime start = end.AddHours(-cfg.HistoryHours);
            int n = bars
                ? Math.Max(4, Math.Min(60, (int)(area.Width / 7)))
                : Math.Max(2, Math.Min(600, (int)(area.Width / 2)));
            double[] vals = Resample(s.Samples, n, start, end);

            double lo = double.MaxValue, hi = double.MinValue;
            foreach (double v in vals)
            {
                if (double.IsNaN(v)) continue;
                if (v < lo) lo = v;
                if (v > hi) hi = v;
            }
            if (lo > hi)
            {
                DrawMessage(g, s.State == null ? "..." : "No numeric data", area);
                return;
            }
            double dataLo = lo, dataHi = hi;

            // Automatic range: bars grow from zero, lines get a little headroom.
            double margin = (hi - lo) * 0.08;
            if (margin < 1e-9) margin = Math.Max(Math.Abs(hi) * 0.05, 0.5);
            if (bars)
            {
                if (lo > 0) lo = 0; else lo -= margin;
                if (hi < 0) hi = 0; else hi += margin;
            }
            else
            {
                lo -= margin;
                hi += margin;
            }
            if (cfg.Min.HasValue) lo = cfg.Min.Value;
            if (cfg.Max.HasValue) hi = cfg.Max.Value;
            if (hi - lo < 1e-9) hi = lo + 1;

            double range = hi - lo;
            Func<double, float> yOf = v =>
                area.Bottom - (float)(Math.Max(0, Math.Min(1, (v - lo) / range)) * area.Height);

            if (bars)
            {
                float slot = area.Width / n;
                float gap = Math.Max(1f, slot * 0.2f);
                float baseY = yOf(Math.Max(lo, Math.Min(hi, 0)));
                for (int i = 0; i < n; i++)
                {
                    if (double.IsNaN(vals[i])) continue;
                    float y = yOf(vals[i]);
                    float y0 = Math.Min(y, baseY), barH = Math.Max(1f, Math.Abs(y - baseY));
                    g.FillRectangle(accentBrush, area.X + i * slot + gap / 2, y0, slot - gap, barH);
                }
            }
            else
            {
                var pts = new List<PointF>(n + 1);
                for (int i = 0; i < n; i++)
                {
                    if (double.IsNaN(vals[i])) continue;
                    pts.Add(new PointF(area.X + (i + 0.5f) * area.Width / n, yOf(vals[i])));
                }
                pts.Add(new PointF(area.Right, pts[pts.Count - 1].Y));

                var poly = new List<PointF>(pts);
                poly.Add(new PointF(area.Right, area.Bottom));
                poly.Add(new PointF(pts[0].X, area.Bottom));
                using (var fill = new SolidBrush(Color.FromArgb(45, accent)))
                    g.FillPolygon(fill, poly.ToArray());
                using (var pen = new Pen(accent, Clamp(area.Height * 0.02f, 1.3f, 3f)))
                {
                    pen.LineJoin = LineJoin.Round;
                    g.DrawLines(pen, pts.ToArray());
                }
            }

            // High / low of the visible window (bars would hide the labels).
            if (!bars && area.Height >= 44 && area.Width >= 70)
            {
                using (var font = new Font(FontName, Clamp(area.Height * 0.11f, 9, 13), FontStyle.Regular, GraphicsUnit.Pixel))
                {
                    float fh = font.GetHeight(g);
                    g.DrawString(FormatNumber(dataHi), font, dimBrush, area.X, area.Y);
                    g.DrawString(FormatNumber(dataLo), font, dimBrush, area.X, area.Bottom - fh);
                }
            }
        }

        // ---- Interaction ----------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll")]
        static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        Point grabOffset;       // cursor position inside the widget while it is dragged
        bool snapDisabled;      // Shift was held when the drag began

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int round = 2; // DWMWCP_ROUND (Windows 11; ignored on Windows 10)
                DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
            }
            catch (Exception) { }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x84, WM_MOVING = 0x216;

            if (m.Msg == WM_MOVING)
            {
                // Position from the cursor rather than from the rectangle Windows
                // proposes, so a snapped widget lets go once the cursor moves away.
                var r = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
                int w = r.Right - r.Left, h = r.Bottom - r.Top;
                Point c = Cursor.Position;
                int x = c.X - grabOffset.X, y = c.Y - grabOffset.Y;
                if (!snapDisabled && (ModifierKeys & Keys.Shift) == 0) Snap(ref x, ref y, w, h);
                r.Left = x; r.Top = y; r.Right = x + w; r.Bottom = y + h;
                Marshal.StructureToPtr(r, m.LParam, false);
                m.Result = (IntPtr)1;
                return;
            }

            base.WndProc(ref m);
            if (m.Msg != WM_NCHITTEST || cfg.Locked) return;

            // Resize from any edge or corner.
            long lp = m.LParam.ToInt64();
            Point p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
            int grip = 7 * DeviceDpi / 96;
            bool l = p.X < grip, rt = p.X >= ClientSize.Width - grip;
            bool t = p.Y < grip, b = p.Y >= ClientSize.Height - grip;
            int hit = 0;
            if (t && l) hit = 13; else if (t && rt) hit = 14;
            else if (b && l) hit = 16; else if (b && rt) hit = 17;
            else if (l) hit = 10; else if (rt) hit = 11;
            else if (t) hit = 12; else if (b) hit = 15;
            if (hit != 0) m.Result = (IntPtr)hit;
        }

        // Pulls a dragged widget onto nearby edges: the display it is on (both the
        // full screen and the area clear of the taskbar) and the other widgets.
        void Snap(ref int x, ref int y, int w, int h)
        {
            int reach = 12 * DeviceDpi / 96;
            Screen screen = Screen.FromPoint(Cursor.Position);
            var xs = new List<int>();
            var ys = new List<int>();
            foreach (Rectangle a in new[] { screen.WorkingArea, screen.Bounds })
            {
                xs.Add(a.Left); xs.Add(a.Right - w);
                ys.Add(a.Top); ys.Add(a.Bottom - h);
            }
            foreach (WidgetForm other in app.Widgets)
            {
                if (other == this || !other.Visible) continue;
                Rectangle o = other.Bounds;
                // Only widgets this one is beside (or nearly beside) pull on it.
                if (y < o.Bottom + reach && y + h > o.Top - reach)
                {
                    xs.Add(o.Right); xs.Add(o.Left - w);    // side by side
                    xs.Add(o.Left); xs.Add(o.Right - w);    // edges in line
                }
                if (x < o.Right + reach && x + w > o.Left - reach)
                {
                    ys.Add(o.Bottom); ys.Add(o.Top - h);
                    ys.Add(o.Top); ys.Add(o.Bottom - h);
                }
            }
            x = Nearest(xs, x, reach);
            y = Nearest(ys, y, reach);
        }

        static int Nearest(List<int> targets, int value, int reach)
        {
            int best = value, bestDistance = reach + 1;
            foreach (int t in targets)
            {
                int d = Math.Abs(t - value);
                if (d < bestDistance) { bestDistance = d; best = t; }
            }
            return best;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && !cfg.Locked)
            {
                // Noted here, not when the move loop starts: Windows only starts it
                // once the cursor has already travelled a few pixels.
                Point c = Cursor.Position;
                grabOffset = new Point(c.X - Left, c.Y - Top);
                snapDisabled = (ModifierKeys & Keys.Shift) != 0;

                // Hand the drag to Windows as if the title bar had been grabbed.
                ReleaseCapture();
                SendMessage(Handle, 0xA1 /* WM_NCLBUTTONDOWN */, (IntPtr)2 /* HTCAPTION */, IntPtr.Zero);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Right) ShowMenu(e.Location);
        }

        // Ctrl + wheel scales the widget around its centre.
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (cfg.Locked || (ModifierKeys & Keys.Control) == 0) return;
            double f = e.Delta > 0 ? 1.1 : 1 / 1.1;
            int w = Math.Max(MinimumSize.Width, (int)Math.Round(Width * f));
            int h = Math.Max(MinimumSize.Height, (int)Math.Round(Height * f));
            Bounds = new Rectangle(Left - (w - Width) / 2, Top - (h - Height) / 2, w, h);
            StoreBounds();
        }

        protected override void OnResizeEnd(EventArgs e)
        {
            base.OnResizeEnd(e);
            StoreBounds();
        }

        void StoreBounds()
        {
            cfg.X = Left;
            cfg.Y = Top;
            cfg.W = Width;
            cfg.H = Height;
            app.SaveSoon();
        }

        // Built on demand so idle widgets do not each hold a menu in memory.
        void ShowMenu(Point at)
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Configure...", null, (s, e) => app.EditWidget(this));
            menu.Items.Add("Refresh now", null, (s, e) => Reload());

            var modes = new ToolStripMenuItem("Display mode");
            for (int i = 0; i < DisplayModes.All.Length; i++)
            {
                string mode = DisplayModes.All[i];
                var item = new ToolStripMenuItem(DisplayModes.Labels[i]);
                item.Checked = cfg.Mode == mode;
                item.Click += (s, e) => { cfg.Mode = mode; ApplyConfig(); app.SaveSoon(); };
                modes.DropDownItems.Add(item);
            }
            menu.Items.Add(modes);

            var top = new ToolStripMenuItem("Always on top");
            top.Checked = cfg.TopMost;
            top.Click += (s, e) => { cfg.TopMost = !cfg.TopMost; TopMost = cfg.TopMost; app.SaveSoon(); };
            menu.Items.Add(top);

            var locked = new ToolStripMenuItem("Lock position and size");
            locked.Checked = cfg.Locked;
            locked.Click += (s, e) => { cfg.Locked = !cfg.Locked; app.SaveSoon(); };
            menu.Items.Add(locked);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Duplicate", null, (s, e) => app.DuplicateWidget(this));
            menu.Items.Add("Remove", null, (s, e) => app.RemoveWidget(this));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Add widget...", null, (s, e) => app.AddWidget());
            menu.Items.Add("Connection settings...", null, (s, e) => app.ShowSettings());
            menu.Items.Add("Exit", null, (s, e) => app.Exit());

            menu.Closed += (s, e) => BeginInvoke((Action)menu.Dispose);
            menu.Show(this, at);
        }

        void DisposeBrushes()
        {
            if (textBrush == null) return;
            textBrush.Dispose();
            dimBrush.Dispose();
            titleBrush.Dispose();
            entityBrush.Dispose();
            accentBrush.Dispose();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose();
                DisposeBrushes();
            }
            base.Dispose(disposing);
        }
    }
}
