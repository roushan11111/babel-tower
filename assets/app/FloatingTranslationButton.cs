using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SwipeTranslate
{
    // A small non-activating launcher. Only an intentional click opens the
    // editor; dragging never reads a selection or submits a translation.
    sealed class FloatingTranslationButton : Form
    {
        readonly Action open;
        readonly ToolTip hint = new ToolTip();
        readonly Size normalSize;
        bool pointerDown;
        bool dragging;
        Point pointerOrigin;
        Point windowOrigin;
        bool translationEnabled = true;
        bool modelReady;
        bool unread;
        bool closing;
        string status = "正在准备本地翻译";

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x80; return cp; }
        }

        internal bool IsDragging { get { return dragging; } }
        internal bool HasUnread { get { return unread; } }
        public event Action Docked;

        public FloatingTranslationButton(Action open, Action settings)
        {
            if (open == null) throw new ArgumentNullException("open");
            this.open = open;
            Name = "FloatingTranslationLauncher";
            Text = "巴别塔 · 悬浮译按钮";
            AccessibleName = "巴别塔翻译按钮";
            AccessibleRole = AccessibleRole.PushButton;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(1, 2, 3);
            TransparencyKey = BackColor;
            using (var graphics = CreateGraphics())
            {
                int side = (int)Math.Round(64 * graphics.DpiX / 96f);
                normalSize = new Size(side, side);
            }
            Size = normalSize;
            Cursor = Cursors.Hand;
            DoubleBuffered = true;
            var menu = new ContextMenuStrip();
            menu.Items.Add("打开翻译面板", null, delegate { this.open(); });
            menu.Items.Add("语言与显示设置", null, delegate { if (settings != null) settings(); });
            ContextMenuStrip = menu;
            UpdateHint();
            SystemEvents.DisplaySettingsChanged += DisplayChanged;
        }

        internal void SetStatus(bool enabled, bool ready, string label)
        {
            translationEnabled = enabled;
            modelReady = ready;
            status = label ?? String.Empty;
            UpdateHint();
            Invalidate();
        }

        internal void SetUnread(bool value)
        {
            unread = value;
            UpdateHint();
            Invalidate();
        }

        void UpdateHint()
        {
            string state = !translationEnabled ? "划选翻译已暂停" : status;
            string text = "巴别塔 · 点击打开翻译\n拖动调整位置，松开贴右侧\n" + state;
            if (unread) text += "\n有新的划选译文";
            hint.SetToolTip(this, text);
            AccessibleDescription = text;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = Math.Min(ClientSize.Width, ClientSize.Height) / 64f;
            if (scale <= 0) return;
            var state = g.Save();
            g.ScaleTransform(scale, scale);
            using (var tile = RoundedTile(new RectangleF(5, 5, 54, 54), 12))
            using (var blue = new LinearGradientBrush(new Point(5, 5), new Point(5, 59),
                Color.FromArgb(23, 54, 77), Color.FromArgb(17, 32, 49)))
            using (var edge = new Pen(Color.FromArgb(61, 92, 119), 1))
            {
                g.FillPath(blue, tile);
                g.DrawPath(edge, tile);
            }
            // An original stepped tower marks Babel Tower, rather than a
            // translation glyph inside a circular badge.
            using (var tower = new SolidBrush(Color.FromArgb(225, 242, 250)))
            using (var window = new SolidBrush(Color.FromArgb(25, 55, 77)))
            using (var tip = new Pen(Color.FromArgb(85, 202, 222), 2))
            {
                g.DrawLine(tip, 32, 11, 32, 16);
                g.FillPolygon(tower, new[] { new Point(28, 16), new Point(36, 16), new Point(36, 23),
                    new Point(40, 23), new Point(40, 30), new Point(44, 30), new Point(44, 40),
                    new Point(20, 40), new Point(20, 30), new Point(24, 30), new Point(24, 23), new Point(28, 23) });
                g.FillRectangle(window, 31, 18, 2, 3);
                g.FillRectangle(window, 27, 25, 3, 3); g.FillRectangle(window, 34, 25, 3, 3);
                g.FillRectangle(window, 23, 33, 3, 4); g.FillRectangle(window, 30, 33, 4, 7); g.FillRectangle(window, 38, 33, 3, 4);
            }
            using (var glyph = new Font("Segoe UI", 8, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var white = new SolidBrush(Color.FromArgb(182, 216, 236)))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString("BABEL", glyph, white, new RectangleF(12, 41, 40, 11), format);
            Color indicator = !translationEnabled ? Color.FromArgb(132, 148, 164) :
                modelReady ? Color.FromArgb(76, 205, 213) : Color.FromArgb(231, 174, 84);
            using (var pen = new Pen(indicator, 2.5f))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                g.DrawLine(pen, 23, 54, 41, 54);
            }
            if (unread)
                using (var brush = new SolidBrush(Color.FromArgb(98, 211, 229))) g.FillEllipse(brush, 49, 11, 5, 5);
            g.Restore(state);
        }

        static GraphicsPath RoundedTile(RectangleF bounds, float radius)
        {
            float diameter = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        internal void PlaceAtRight(Rectangle area, int y)
        {
            if (area.Width <= 0 || area.Height <= 0) return;
            Size = new Size(Math.Min(normalSize.Width, area.Width), Math.Min(normalSize.Height, area.Height));
            Location = new Point(area.Right - Width, Math.Max(area.Top, Math.Min(y, area.Bottom - Height)));
        }

        internal void BeginPointer(Point point)
        {
            pointerDown = true;
            dragging = false;
            pointerOrigin = point;
            windowOrigin = Location;
        }

        internal void MovePointer(Point point)
        {
            if (!pointerDown) return;
            int dx = point.X - pointerOrigin.X, dy = point.Y - pointerOrigin.Y;
            if (Math.Abs(dx) + Math.Abs(dy) >= 6) dragging = true;
            if (dragging) Location = new Point(windowOrigin.X + dx, windowOrigin.Y + dy);
        }

        internal bool EndPointer(Point point, Rectangle area)
        {
            if (!pointerDown) return false;
            MovePointer(point);
            bool click = !dragging;
            pointerDown = dragging = false;
            PlaceAtRight(area, Top);
            return click;
        }

        internal void CancelPointer(Rectangle? workingArea = null)
        {
            if (dragging && workingArea.HasValue) PlaceAtRight(workingArea.Value, Top);
            pointerDown = dragging = false;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            BeginPointer(PointToScreen(e.Location));
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            MovePointer(PointToScreen(e.Location));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            Point point = PointToScreen(e.Location);
            bool click = EndPointer(point, Screen.FromPoint(point).WorkingArea);
            Capture = false;
            var docked = Docked; if (docked != null) docked();
            if (click) open();
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture)
            {
                CancelPointer(!closing && Visible ? (Rectangle?)Screen.FromRectangle(Bounds).WorkingArea : null);
            }
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            if (!Visible) { CancelPointer(); Capture = false; }
            base.OnVisibleChanged(e);
        }

        void DisplayChanged(object sender, EventArgs e)
        {
            if (closing || IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke((Action)delegate
                {
                    if (!closing && !IsDisposed && Visible && !pointerDown)
                    {
                        PlaceAtRight(Screen.FromRectangle(Bounds).WorkingArea, Top);
                        var docked = Docked; if (docked != null) docked();
                    }
                });
            }
            catch (InvalidOperationException) { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !closing)
            {
                closing = true;
                SystemEvents.DisplaySettingsChanged -= DisplayChanged;
                hint.Dispose();
                if (ContextMenuStrip != null) ContextMenuStrip.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
