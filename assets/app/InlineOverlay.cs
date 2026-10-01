using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SwipeTranslate
{
    // A click-through cover whose visible shape is precisely the selected text.
    // All text must fit before any cover is shown; the caller owns the fallback.
    sealed class InlineOverlay : Form
    {
        private const int MaximumRectangles = 128;
        private const float MinimumFontPixels = 10f;
        private const TextFormatFlags MeasureFlags = TextFormatFlags.NoPadding |
            TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.Left;
        private const TextFormatFlags TextFlags = TextFormatFlags.NoPadding |
            TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine |
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
            TextFormatFlags.PreserveGraphicsClipping;
        private List<LinePiece> pieces = new List<LinePiece>();
        private string displayedText = String.Empty;

        public string DisplayedText { get { return displayedText; } }

        public InlineOverlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = SystemColors.Highlight;
            ForeColor = Color.White;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams value = base.CreateParams;
                value.ExStyle |= 0x08000000 | 0x00000080 | 0x00080000 | 0x00000020;
                return value;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SetLayeredWindowAttributes(Handle, 0, 255, 2);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0021) // WM_MOUSEACTIVATE: do not steal focus.
            {
                m.Result = new IntPtr(3); // MA_NOACTIVATE
                return;
            }
            if (m.Msg == 0x0084) // WM_NCHITTEST: let the original control receive input.
            {
                m.Result = new IntPtr(-1); // HTTRANSPARENT
                return;
            }
            base.WndProc(ref m);
        }

        public bool Present(string translation, Rectangle[] rectangles, string targetLanguage)
        {
            Hide();
            ReleasePieces(pieces);
            pieces = new List<LinePiece>();
            displayedText = String.Empty;
            if (IsDisposed || String.IsNullOrWhiteSpace(translation) || rectangles == null ||
                rectangles.Length == 0 || rectangles.Length > MaximumRectangles)
                return false;

            Rectangle[] ordered = (Rectangle[])rectangles.Clone();
            long left = Int32.MaxValue, top = Int32.MaxValue;
            long right = Int32.MinValue, bottom = Int32.MinValue;
            foreach (Rectangle rectangle in ordered)
            {
                long rectangleRight = (long)rectangle.X + rectangle.Width;
                long rectangleBottom = (long)rectangle.Y + rectangle.Height;
                if (rectangle.Width <= 0 || rectangle.Height <= 0 ||
                    rectangleRight > Int32.MaxValue || rectangleBottom > Int32.MaxValue)
                    return false;
                left = Math.Min(left, rectangle.Left);
                top = Math.Min(top, rectangle.Top);
                right = Math.Max(right, rectangleRight);
                bottom = Math.Max(bottom, rectangleBottom);
            }
            // Win32 window dimensions are limited to signed 16-bit coordinates.
            if (right - left > 32767 || bottom - top > 32767)
                return false;
            Array.Sort(ordered, delegate(Rectangle first, Rectangle second)
            {
                int row = first.Top.CompareTo(second.Top);
                return row != 0 ? row : first.Left.CompareTo(second.Left);
            });

            string normalized = translation.Replace("\r\n", "\n").Replace('\r', '\n');
            int[] elements = StringInfo.ParseCombiningCharacters(normalized);
            List<LinePiece> fitted;
            if (!TryLayout(normalized, elements, ordered, 1f, out fitted))
            {
                if (!TryLayout(normalized, elements, ordered, 0f, out fitted))
                    return false;
                // Find the largest consistent size that preserves the complete text.
                float lower = 0f, upper = 1f;
                for (int attempt = 0; attempt < 9; attempt++)
                {
                    float scale = (lower + upper) / 2f;
                    List<LinePiece> candidate;
                    if (TryLayout(normalized, elements, ordered, scale, out candidate))
                    {
                        ReleasePieces(fitted);
                        fitted = candidate;
                        lower = scale;
                    }
                    else upper = scale;
                }
            }

            Rectangle windowBounds = new Rectangle((int)left, (int)top,
                (int)(right - left), (int)(bottom - top));
            Region visibleRegion = new Region();
            visibleRegion.MakeEmpty();
            foreach (LinePiece piece in fitted)
            {
                piece.Bounds.Offset(-windowBounds.Left, -windowBounds.Top);
                visibleRegion.Union(piece.Bounds);
            }
            // Cover unused selected rectangles as well; there is no visible card.
            foreach (Rectangle rectangle in ordered)
            {
                Rectangle local = rectangle;
                local.Offset(-windowBounds.Left, -windowBounds.Top);
                visibleRegion.Union(local);
            }

            Region oldRegion = Region;
            Bounds = windowBounds;
            Region = visibleRegion;
            if (oldRegion != null) oldRegion.Dispose();
            pieces = fitted;
            displayedText = translation;
            Show();
            Invalidate();
            return true;
        }

        private static bool TryLayout(string text, int[] elements, Rectangle[] rectangles,
            float scale, out List<LinePiece> layout)
        {
            layout = new List<LinePiece>();
            int next = 0;
            foreach (Rectangle rectangle in rectangles)
            {
                if (next >= elements.Length) break;
                float pixels = Math.Max(MinimumFontPixels, rectangle.Height * 0.78f * scale);
                Font font = new Font("Microsoft YaHei UI", pixels, FontStyle.Regular,
                    GraphicsUnit.Pixel);
                LinePiece piece = new LinePiece { Bounds = rectangle, Font = font, Text = String.Empty };
                layout.Add(piece);

                int segmentEnd = next;
                while (segmentEnd < elements.Length && text[elements[segmentEnd]] != '\n')
                    segmentEnd++;
                if (segmentEnd == next)
                {
                    next++; // An explicit blank line consumes one selected rectangle.
                    continue;
                }

                int low = 0, high = segmentEnd - next;
                while (low < high)
                {
                    int count = low + (high - low + 1) / 2;
                    string fragment = ElementSubstring(text, elements, next, count);
                    Size measured = TextRenderer.MeasureText(fragment, font,
                        new Size(32767, 32767), MeasureFlags);
                    if (measured.Width <= rectangle.Width && measured.Height <= rectangle.Height)
                        low = count;
                    else high = count - 1;
                }
                if (low == 0)
                {
                    ReleasePieces(layout);
                    layout = null;
                    return false;
                }
                piece.Text = ElementSubstring(text, elements, next, low);
                next += low;
                if (next < elements.Length && text[elements[next]] == '\n') next++;
            }
            if (next < elements.Length)
            {
                ReleasePieces(layout);
                layout = null;
                return false;
            }
            return true;
        }

        private static string ElementSubstring(string text, int[] elements, int start, int count)
        {
            int end = start + count < elements.Length ? elements[start + count] : text.Length;
            return text.Substring(elements[start], end - elements[start]);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(SystemColors.Highlight);
            foreach (LinePiece piece in pieces)
            {
                if (piece.Text.Length == 0) continue;
                System.Drawing.Drawing2D.GraphicsState state = e.Graphics.Save();
                e.Graphics.SetClip(piece.Bounds, System.Drawing.Drawing2D.CombineMode.Intersect);
                TextRenderer.DrawText(e.Graphics, piece.Text, piece.Font, piece.Bounds,
                    Color.White, SystemColors.Highlight, TextFlags);
                e.Graphics.Restore(state);
            }
            base.OnPaint(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ReleasePieces(pieces);
                pieces.Clear();
            }
            base.Dispose(disposing);
        }

        private static void ReleasePieces(List<LinePiece> layout)
        {
            if (layout == null) return;
            foreach (LinePiece piece in layout) piece.Font.Dispose();
        }

        private sealed class LinePiece
        {
            public Rectangle Bounds;
            public Font Font;
            public string Text;
        }

        [DllImport("user32.dll")]
        private static extern bool SetLayeredWindowAttributes(IntPtr window, uint color,
            byte alpha, uint flags);
    }
}
