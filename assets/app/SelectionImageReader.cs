using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace SwipeTranslate
{
    public sealed class SelectionGestureProbe
    {
        internal IntPtr Window;
        internal int ProcessId;
        internal Rectangle Bounds;
        internal bool[] PreviouslyBlue;
        internal long Timestamp;
    }

    public sealed class SelectionImageSnapshot : IDisposable
    {
        // Only black glyphs on white remain. Pixels outside accepted highlights
        // are never copied into this image, and neither image nor hash is saved.
        public Bitmap Image;
        public Rectangle[] Rectangles;
        public Rectangle Bounds;
        public IntPtr ForegroundWindow;
        public int ProcessId;
        public string Fingerprint;

        public SelectionSnapshot ToSelectionSnapshot(string text)
        {
            if (String.IsNullOrWhiteSpace(text) || text.Length > 4000 ||
                Rectangles == null || Rectangles.Length == 0)
                return null;
            return new SelectionSnapshot
            {
                Text = text,
                Bounds = Bounds,
                Rectangles = (Rectangle[])Rectangles.Clone(),
                ForegroundWindow = ForegroundWindow,
                ProcessId = ProcessId,
                IsEditable = false
            };
        }

        public void Dispose()
        {
            if (Image != null) { Image.Dispose(); Image = null; }
        }
    }

    /// <summary>
    /// Conservative fallback for visible blue text highlights when UIA fails.
    /// The probe stores a 32x20 boolean color mask, never readable source text.
    /// Screen pixels are processed locally, masked before OCR, and never saved
    /// or sent to a service. Capture must run outside the mouse-hook callback.
    /// SetProcessDpiAware must be called by the host before creating windows.
    /// </summary>
    public static class SelectionImageReader
    {
        private const int ProbeWidth = 32;
        private const int ProbeHeight = 20;
        private const int MaximumWidth = 3072;
        private const int MaximumHeight = 1200;
        private const int MaximumRectangles = 128;
        private const int WhiteBorder = 8;
        private static readonly int OwnProcessId = Process.GetCurrentProcess().Id;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);
        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        private sealed class Pixels
        {
            public int Width, Height;
            public byte[] Data;
            public bool IsBlue(int index)
            {
                int offset = index * 4;
                return Blue(Data[offset + 2], Data[offset + 1], Data[offset]);
            }
        }

        private sealed class HighlightLine
        {
            public Rectangle Rectangle;
            public int Red, Green, Blue;
            public bool LightGlyphs;
        }

        public static SelectionGestureProbe CreateProbe(IntPtr window, Point dragStart)
        {
            int processId;
            if (!TryGetProcess(window, out processId)) return null;
            try
            {
                Rectangle bounds = Rectangle.Intersect(VirtualScreen(),
                    new Rectangle(dragStart.X - ProbeWidth / 2, dragStart.Y - ProbeHeight / 2,
                        ProbeWidth, ProbeHeight));
                if (bounds.Width < 8 || bounds.Height < 8) return null;
                using (Bitmap image = CopyScreen(bounds))
                    return CreateProbeFromPixels(image, bounds, window, processId);
            }
            catch (ExternalException) { return null; }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
        }

        // A mouse-down can clear an old selection before the first move. Only
        // replace its tiny probe when the same pixels prove that clearing; a
        // move onto an unrelated blue element must not manufacture a baseline.
        // This method is pure and can safely be called by the mouse watcher.
        public static bool TryRefreshProbeOnFirstMove(SelectionGestureProbe previous,
            SelectionGestureProbe refreshed, out SelectionGestureProbe effective)
        {
            effective = previous;
            if (previous == null || refreshed == null || previous.Window != refreshed.Window ||
                previous.ProcessId != refreshed.ProcessId || previous.Bounds != refreshed.Bounds ||
                previous.PreviouslyBlue == null || refreshed.PreviouslyBlue == null ||
                previous.Bounds.Width < 8 || previous.Bounds.Width > ProbeWidth ||
                previous.Bounds.Height < 8 || previous.Bounds.Height > ProbeHeight ||
                previous.PreviouslyBlue.Length != previous.Bounds.Width * previous.Bounds.Height ||
                refreshed.PreviouslyBlue.Length != previous.PreviouslyBlue.Length)
                return false;
            double elapsed = (refreshed.Timestamp - previous.Timestamp) / (double)Stopwatch.Frequency;
            if (elapsed < 0 || elapsed > 2.0) return false;
            int oldBlue = 0, remainingBlue = 0, cleared = 0;
            for (int i = 0; i < previous.PreviouslyBlue.Length; i++)
            {
                if (previous.PreviouslyBlue[i])
                {
                    oldBlue++;
                    if (!refreshed.PreviouslyBlue[i]) cleared++;
                }
                if (refreshed.PreviouslyBlue[i]) remainingBlue++;
            }
            if (oldBlue < 24 || cleared < 16 || cleared < oldBlue * 0.80 ||
                remainingBlue > oldBlue * 0.25)
                return false;
            effective = refreshed;
            return true;
        }

        public static SelectionImageSnapshot Capture(IntPtr foregroundWindow, Point dragStart,
            Point dragEnd, SelectionGestureProbe probe)
        {
            int processId;
            if (probe == null || probe.PreviouslyBlue == null || probe.Window != foregroundWindow ||
                !TryGetProcess(foregroundWindow, out processId) || probe.ProcessId != processId ||
                GetForegroundWindow() != foregroundWindow ||
                (Stopwatch.GetTimestamp() - probe.Timestamp) / (double)Stopwatch.Frequency > 35.0 ||
                Math.Abs((long)dragStart.X - dragEnd.X) + Math.Abs((long)dragStart.Y - dragEnd.Y) < 5)
                return null;
            try
            {
                NativeRect native;
                if (!GetWindowRect(foregroundWindow, out native)) return null;
                Rectangle window = Rectangle.Intersect(VirtualScreen(),
                    Rectangle.FromLTRB(native.Left, native.Top, native.Right, native.Bottom));
                int top = Math.Min(dragStart.Y, dragEnd.Y) - 72;
                int bottom = Math.Max(dragStart.Y, dragEnd.Y) + 72;
                bool severalLines = Math.Abs((long)dragStart.Y - dragEnd.Y) > 12;
                int left = severalLines ? window.Left : Math.Min(dragStart.X, dragEnd.X) - 72;
                int right = severalLines ? window.Right : Math.Max(dragStart.X, dragEnd.X) + 72;
                Rectangle capture = Rectangle.Intersect(window, Rectangle.FromLTRB(left, top, right, bottom));
                if (capture.Width < 8 || capture.Height < 8 || capture.Width > MaximumWidth ||
                    capture.Height > MaximumHeight || !capture.Contains(dragStart) || !capture.Contains(dragEnd))
                    return null;
                using (Bitmap image = CopyScreen(capture))
                {
                    SelectionImageSnapshot snapshot = AnalyzePixels(image, capture, dragStart, dragEnd,
                        probe, foregroundWindow, processId);
                    if (GetForegroundWindow() == foregroundWindow) return snapshot;
                    if (snapshot != null) snapshot.Dispose();
                    return null;
                }
            }
            catch (ExternalException) { return null; }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
        }

        // Revalidate an already-confirmed highlight before painting. The source
        // text can then be read locally again and compared by the host. Exact
        // selected geometry remains mandatory, while harmless RGB changes are
        // not themselves evidence that the text changed. No gesture/probe age
        // or newly-blue requirement applies to this second capture.
        public static SelectionImageSnapshot RecaptureCurrent(SelectionImageSnapshot previous)
        {
            if (!ValidSnapshotGeometry(previous) ||
                GetForegroundWindow() != previous.ForegroundWindow)
                return null;
            int processId;
            if (!TryGetProcess(previous.ForegroundWindow, out processId) || processId != previous.ProcessId)
                return null;
            try
            {
                NativeRect native;
                if (!GetWindowRect(previous.ForegroundWindow, out native)) return null;
                Rectangle window = Rectangle.Intersect(VirtualScreen(),
                    Rectangle.FromLTRB(native.Left, native.Top, native.Right, native.Bottom));
                Rectangle capture = previous.Bounds;
                capture.Inflate(8, 8);
                capture = Rectangle.Intersect(window, capture);
                if (!capture.Contains(previous.Bounds) || capture.Width < 8 || capture.Height < 8 ||
                    capture.Width > MaximumWidth || capture.Height > MaximumHeight)
                    return null;
                using (Bitmap image = CopyScreen(capture))
                {
                    SelectionImageSnapshot snapshot = RecaptureFromPixels(image, capture, previous);
                    int currentProcess;
                    if (GetForegroundWindow() == previous.ForegroundWindow &&
                        TryGetProcess(previous.ForegroundWindow, out currentProcess) && currentProcess == processId)
                        return snapshot;
                    if (snapshot != null) snapshot.Dispose();
                    return null;
                }
            }
            catch (ExternalException) { return null; }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
        }

        // Call before painting the inline overlay: its own pixels would otherwise
        // replace the original highlight and correctly invalidate this hash.
        public static bool IsStillCurrent(SelectionImageSnapshot snapshot)
        {
            if (snapshot == null || snapshot.Rectangles == null || snapshot.Rectangles.Length == 0 ||
                snapshot.Rectangles.Length > MaximumRectangles || String.IsNullOrEmpty(snapshot.Fingerprint) ||
                GetForegroundWindow() != snapshot.ForegroundWindow || snapshot.Bounds.IsEmpty)
                return false;
            int processId;
            if (!TryGetProcess(snapshot.ForegroundWindow, out processId) || processId != snapshot.ProcessId)
                return false;
            try
            {
                using (Bitmap image = CopyScreen(snapshot.Bounds))
                {
                    string fingerprint = Fingerprint(ReadPixels(image), snapshot.Bounds, snapshot.Rectangles);
                    return fingerprint == snapshot.Fingerprint &&
                        GetForegroundWindow() == snapshot.ForegroundWindow;
                }
            }
            catch (ExternalException) { return false; }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        // Pure image entry points let the algorithm be checked with generated
        // offscreen samples. They do not call any desktop or input API.
        internal static SelectionGestureProbe CreateProbeFromPixels(Bitmap image, Rectangle bounds,
            IntPtr window, int processId)
        {
            Pixels pixels = ReadPixels(image);
            bool[] blue = new bool[pixels.Width * pixels.Height];
            for (int i = 0; i < blue.Length; i++) blue[i] = pixels.IsBlue(i);
            return new SelectionGestureProbe
            {
                Window = window, ProcessId = processId, Bounds = bounds,
                PreviouslyBlue = blue, Timestamp = Stopwatch.GetTimestamp()
            };
        }

        internal static SelectionImageSnapshot AnalyzePixels(Bitmap image, Rectangle captureBounds,
            Point dragStart, Point dragEnd, SelectionGestureProbe probe, IntPtr window, int processId)
        {
            if (image == null || probe == null || probe.PreviouslyBlue == null ||
                image.Width != captureBounds.Width || image.Height != captureBounds.Height ||
                image.Width > MaximumWidth || image.Height > MaximumHeight ||
                probe.PreviouslyBlue.Length != probe.Bounds.Width * probe.Bounds.Height)
                return null;
            Pixels pixels = ReadPixels(image);
            List<HighlightLine> candidates = FindHighlightLines(pixels, captureBounds.Location);
            List<HighlightLine> selected = SelectGestureLines(candidates, dragStart, dragEnd);
            if (selected == null || selected.Count == 0 || selected.Count > MaximumRectangles ||
                !HasNewHighlight(pixels, captureBounds, selected, probe))
                return null;
            return MakeSnapshot(pixels, captureBounds, selected, window, processId);
        }

        internal static SelectionImageSnapshot RecaptureFromPixels(Bitmap image, Rectangle captureBounds,
            SelectionImageSnapshot previous)
        {
            if (!ValidSnapshotGeometry(previous) || image == null ||
                image.Width != captureBounds.Width || image.Height != captureBounds.Height ||
                image.Width > MaximumWidth || image.Height > MaximumHeight ||
                !captureBounds.Contains(previous.Bounds))
                return null;
            Pixels pixels = ReadPixels(image);
            List<HighlightLine> candidates = FindHighlightLines(pixels, captureBounds.Location);
            List<HighlightLine> selected = new List<HighlightLine>();
            foreach (Rectangle rectangle in previous.Rectangles)
            {
                HighlightLine match = null;
                foreach (HighlightLine candidate in candidates)
                {
                    if (candidate.Rectangle != rectangle) continue;
                    if (match != null) return null;
                    match = candidate;
                }
                if (match == null) return null;
                selected.Add(match);
            }
            return MakeSnapshot(pixels, captureBounds, selected, previous.ForegroundWindow, previous.ProcessId);
        }

        private static bool ValidSnapshotGeometry(SelectionImageSnapshot snapshot)
        {
            if (snapshot == null || snapshot.ForegroundWindow == IntPtr.Zero || snapshot.ProcessId <= 0 ||
                snapshot.Rectangles == null || snapshot.Rectangles.Length == 0 ||
                snapshot.Rectangles.Length > MaximumRectangles || snapshot.Bounds.Width < 8 ||
                snapshot.Bounds.Height < 8 || snapshot.Bounds.Width > MaximumWidth ||
                snapshot.Bounds.Height > MaximumHeight)
                return false;
            Rectangle bounds = snapshot.Rectangles[0];
            for (int i = 0; i < snapshot.Rectangles.Length; i++)
            {
                Rectangle rectangle = snapshot.Rectangles[i];
                if (rectangle.Width < 8 || rectangle.Height < 8 || !snapshot.Bounds.Contains(rectangle))
                    return false;
                for (int j = 0; j < i; j++)
                    if (snapshot.Rectangles[j].IntersectsWith(rectangle)) return false;
                bounds = Rectangle.Union(bounds, rectangle);
            }
            return bounds == snapshot.Bounds;
        }

        private static SelectionImageSnapshot MakeSnapshot(Pixels pixels, Rectangle captureBounds,
            List<HighlightLine> selected, IntPtr window, int processId)
        {
            Rectangle bounds = selected[0].Rectangle;
            Rectangle[] rectangles = new Rectangle[selected.Count];
            for (int i = 0; i < selected.Count; i++)
            {
                rectangles[i] = selected[i].Rectangle;
                bounds = Rectangle.Union(bounds, rectangles[i]);
            }
            Bitmap masked = MakeOcrImage(pixels, captureBounds, selected, bounds);
            return new SelectionImageSnapshot
            {
                Image = masked, Rectangles = rectangles, Bounds = bounds,
                ForegroundWindow = window, ProcessId = processId,
                Fingerprint = Fingerprint(pixels, captureBounds, rectangles)
            };
        }

        private static List<HighlightLine> FindHighlightLines(Pixels pixels, Point origin)
        {
            int size = pixels.Width * pixels.Height;
            bool[] blue = new bool[size];
            for (int i = 0; i < size; i++) blue[i] = pixels.IsBlue(i);
            int[] queue = new int[size];
            List<HighlightLine> lines = new List<HighlightLine>();
            for (int seed = 0; seed < size; seed++)
            {
                if (!blue[seed]) continue;
                int head = 0, count = 1;
                queue[0] = seed; blue[seed] = false;
                int left = seed % pixels.Width, right = left;
                int top = seed / pixels.Width, bottom = top;
                while (head < count)
                {
                    int index = queue[head++];
                    int x = index % pixels.Width, y = index / pixels.Width;
                    left = Math.Min(left, x); right = Math.Max(right, x);
                    top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                    if (x > 0) Enqueue(index - 1, blue, queue, ref count);
                    if (x + 1 < pixels.Width) Enqueue(index + 1, blue, queue, ref count);
                    if (y > 0) Enqueue(index - pixels.Width, blue, queue, ref count);
                    if (y + 1 < pixels.Height) Enqueue(index + pixels.Width, blue, queue, ref count);
                }
                int width = right - left + 1, height = bottom - top + 1;
                // Blue link glyphs are sparse, small components. A real blue
                // selection has a filled, connected rectangular background.
                if (count < 80 || width < 8 || height < 8 ||
                    count < width * height * 0.48 || left == 0 || right == pixels.Width - 1 ||
                    top == 0 || bottom == pixels.Height - 1)
                    continue;
                int[] rowLeft = new int[height], rowRight = new int[height], rowCount = new int[height];
                for (int y = 0; y < height; y++) { rowLeft[y] = Int32.MaxValue; rowRight[y] = -1; }
                Dictionary<int, int> colors = new Dictionary<int, int>();
                int dominant = 0, dominantCount = 0;
                for (int i = 0; i < count; i++)
                {
                    int index = queue[i], x = index % pixels.Width, y = index / pixels.Width - top;
                    rowLeft[y] = Math.Min(rowLeft[y], x); rowRight[y] = Math.Max(rowRight[y], x); rowCount[y]++;
                    int offset = index * 4;
                    int color = (pixels.Data[offset + 2] >> 3) << 10 |
                        (pixels.Data[offset + 1] >> 3) << 5 | pixels.Data[offset] >> 3;
                    int frequency;
                    colors.TryGetValue(color, out frequency); frequency++; colors[color] = frequency;
                    if (frequency > dominantCount) { dominant = color; dominantCount = frequency; }
                }
                if (dominantCount < count * 0.55) continue;
                int red = ((dominant >> 10) & 31) * 8 + 4;
                int green = ((dominant >> 5) & 31) * 8 + 4;
                int blueValue = (dominant & 31) * 8 + 4;
                int componentLineStart = lines.Count;
                bool clippedGlyph = false;
                int runTop = -1, runLeft = 0, runRight = 0;
                for (int row = 0; row <= height; row++)
                {
                    bool solid = row < height && rowRight[row] >= rowLeft[row] &&
                        rowRight[row] - rowLeft[row] + 1 >= 8 &&
                        rowCount[row] >= (rowRight[row] - rowLeft[row] + 1) * 0.55;
                    bool same = solid && runTop >= 0 && Math.Abs(rowLeft[row] - runLeft) <= 2 &&
                        Math.Abs(rowRight[row] - runRight) <= 2;
                    if (runTop >= 0 && !same)
                    {
                        if (row - runTop >= 8)
                            clippedGlyph |= AddGlyphLines(lines, pixels, new Rectangle(runLeft, top + runTop,
                                runRight - runLeft + 1, row - runTop), origin, red, green, blueValue,
                                runTop > 0, row < height);
                        runTop = -1;
                    }
                    if (solid)
                    {
                        if (runTop < 0) { runTop = row; runLeft = rowLeft[row]; runRight = rowRight[row]; }
                        else { runLeft = Math.Min(runLeft, rowLeft[row]); runRight = Math.Max(runRight, rowRight[row]); }
                    }
                }
                // Edge occlusion can change a connected highlight's row width
                // and split it through the middle of characters. Never pass an
                // upper or lower fragment of those glyphs to OCR as a full line.
                if (clippedGlyph && lines.Count > componentLineStart)
                    lines.RemoveRange(componentLineStart, lines.Count - componentLineStart);
                if (lines.Count > 256) return new List<HighlightLine>();
            }
            lines.Sort(delegate(HighlightLine first, HighlightLine second)
            {
                int comparison = first.Rectangle.Top.CompareTo(second.Rectangle.Top);
                return comparison != 0 ? comparison : first.Rectangle.Left.CompareTo(second.Rectangle.Left);
            });
            return lines;
        }

        private static void Enqueue(int index, bool[] blue, int[] queue, ref int count)
        {
            if (!blue[index]) return;
            blue[index] = false;
            queue[count++] = index;
        }

        private static bool AddGlyphLines(List<HighlightLine> lines, Pixels pixels, Rectangle rectangle,
            Point origin, int red, int green, int blue, bool internalTop, bool internalBottom)
        {
            int light = 0, dark = 0;
            for (int y = rectangle.Top; y < rectangle.Bottom; y++)
                for (int x = rectangle.Left; x < rectangle.Right; x++)
                {
                    int index = y * pixels.Width + x;
                    if (Glyph(pixels, index, red, green, blue, true)) light++;
                    if (Glyph(pixels, index, red, green, blue, false)) dark++;
                }
            bool lightGlyphs = light >= dark;
            int glyphCount = Math.Max(light, dark);
            int area = rectangle.Width * rectangle.Height;
            if (glyphCount < Math.Max(5, area * 0.008) || glyphCount > area * 0.42) return false;
            int[] rows = new int[rectangle.Height];
            for (int row = 0; row < rows.Length; row++)
                for (int x = rectangle.Left; x < rectangle.Right; x++)
                    if (Glyph(pixels, (rectangle.Top + row) * pixels.Width + x,
                        red, green, blue, lightGlyphs)) rows[row]++;
            // Only an internal width-change boundary is an artificial crop
            // through a connected highlight. A real selection's outer border
            // can legitimately touch a glyph, especially for small fonts.
            // Never expand an internal fragment into unknown/unselected pixels.
            if ((internalTop && rows[0] > 0) || (internalBottom && rows[rows.Length - 1] > 0))
                return true;
            List<Rectangle> glyphBands = new List<Rectangle>();
            int bandStart = -1, lastGlyph = -1;
            for (int row = 0; row <= rows.Length + 2; row++)
            {
                if (row < rows.Length && rows[row] > 0)
                {
                    if (bandStart < 0) bandStart = row;
                    lastGlyph = row;
                }
                if (bandStart >= 0 && row - lastGlyph > 2)
                {
                    if (lastGlyph - bandStart + 1 >= 3)
                        glyphBands.Add(new Rectangle(0, bandStart, 1, lastGlyph - bandStart + 1));
                    bandStart = -1;
                }
            }
            if (glyphBands.Count == 0) return false;
            int previousBoundary = 0;
            for (int i = 0; i < glyphBands.Count; i++)
            {
                int nextBoundary = i + 1 < glyphBands.Count
                    ? (glyphBands[i].Bottom + glyphBands[i + 1].Top) / 2 : rectangle.Height;
                int height = nextBoundary - previousBoundary;
                if (height < 8 || height > 96) return false;
                lines.Add(new HighlightLine
                {
                    Rectangle = new Rectangle(rectangle.Left + origin.X, rectangle.Top + previousBoundary + origin.Y,
                        rectangle.Width, height), Red = red, Green = green, Blue = blue, LightGlyphs = lightGlyphs
                });
                previousBoundary = nextBoundary;
            }
            return false;
        }

        private static List<HighlightLine> SelectGestureLines(List<HighlightLine> candidates, Point start, Point end)
        {
            int firstIndex = NearestLine(candidates, start), lastIndex = NearestLine(candidates, end);
            if (firstIndex < 0 || lastIndex < 0) return null;
            if (firstIndex == lastIndex)
            {
                HighlightLine line = candidates[firstIndex];
                Rectangle rectangle = line.Rectangle;
                if (Math.Abs(rectangle.Left - Math.Min(start.X, end.X)) > 18 ||
                    Math.Abs(rectangle.Right - Math.Max(start.X, end.X)) > 18 ||
                    Math.Abs((long)start.Y - end.Y) > rectangle.Height + 8)
                    return null;
                return new List<HighlightLine> { line };
            }
            Point upper = start.Y <= end.Y ? start : end;
            Point lower = start.Y <= end.Y ? end : start;
            int upperIndex = start.Y <= end.Y ? firstIndex : lastIndex;
            int lowerIndex = start.Y <= end.Y ? lastIndex : firstIndex;
            HighlightLine first = candidates[upperIndex], last = candidates[lowerIndex];
            if (first.Rectangle.Top >= last.Rectangle.Top ||
                Math.Abs(first.Rectangle.Left - upper.X) > 18 ||
                Math.Abs(last.Rectangle.Right - lower.X) > 18 || !SameColor(first, last))
                return null;
            List<HighlightLine> selected = new List<HighlightLine>();
            int left = Math.Min(first.Rectangle.Left, last.Rectangle.Left);
            int right = Math.Max(first.Rectangle.Right, last.Rectangle.Right);
            foreach (HighlightLine line in candidates)
            {
                if (line.Rectangle.Top < first.Rectangle.Top || line.Rectangle.Top > last.Rectangle.Top ||
                    !SameColor(first, line) || Math.Min(right, line.Rectangle.Right) -
                        Math.Max(left, line.Rectangle.Left) < Math.Min(8, line.Rectangle.Width))
                    continue;
                if (selected.Count > 0)
                {
                    Rectangle previous = selected[selected.Count - 1].Rectangle;
                    if (line.Rectangle.Top < previous.Bottom - 2 ||
                        line.Rectangle.Top - previous.Bottom > Math.Max(80, previous.Height * 3))
                        return null;
                }
                selected.Add(line);
            }
            if (selected.Count < 2 || selected.Count > MaximumRectangles ||
                selected[0] != first || selected[selected.Count - 1] != last)
                return null;
            int fullLeft = Int32.MaxValue, fullRight = Int32.MinValue;
            foreach (HighlightLine line in selected)
            {
                fullLeft = Math.Min(fullLeft, line.Rectangle.Left);
                fullRight = Math.Max(fullRight, line.Rectangle.Right);
            }
            if (Math.Abs(first.Rectangle.Right - fullRight) > 3 || Math.Abs(last.Rectangle.Left - fullLeft) > 3)
                return null;
            for (int i = 1; i + 1 < selected.Count; i++)
                if (Math.Abs(selected[i].Rectangle.Left - fullLeft) > 3 ||
                    Math.Abs(selected[i].Rectangle.Right - fullRight) > 3)
                    return null;
            return selected;
        }

        private static int NearestLine(List<HighlightLine> lines, Point pointer)
        {
            int nearest = -1;
            long bestDistance = 145;
            for (int i = 0; i < lines.Count; i++)
            {
                Rectangle rectangle = lines[i].Rectangle;
                int dx = pointer.X < rectangle.Left ? rectangle.Left - pointer.X :
                    (pointer.X > rectangle.Right ? pointer.X - rectangle.Right : 0);
                int dy = pointer.Y < rectangle.Top ? rectangle.Top - pointer.Y :
                    (pointer.Y > rectangle.Bottom ? pointer.Y - rectangle.Bottom : 0);
                long distance = (long)dx * dx + (long)dy * dy;
                if (distance < bestDistance) { nearest = i; bestDistance = distance; }
            }
            return nearest;
        }

        private static bool SameColor(HighlightLine first, HighlightLine second)
        {
            return Math.Abs(first.Red - second.Red) <= 12 && Math.Abs(first.Green - second.Green) <= 12 &&
                Math.Abs(first.Blue - second.Blue) <= 12 && first.LightGlyphs == second.LightGlyphs;
        }

        private static bool HasNewHighlight(Pixels pixels, Rectangle capture, List<HighlightLine> selected,
            SelectionGestureProbe probe)
        {
            int blue = 0, newlyBlue = 0;
            Rectangle intersection = Rectangle.Intersect(capture, probe.Bounds);
            foreach (HighlightLine line in selected)
            {
                Rectangle sample = Rectangle.Intersect(intersection, line.Rectangle);
                for (int y = sample.Top; y < sample.Bottom; y++)
                    for (int x = sample.Left; x < sample.Right; x++)
                    {
                        int index = (y - capture.Top) * pixels.Width + x - capture.Left;
                        if (!pixels.IsBlue(index)) continue;
                        blue++;
                        int oldIndex = (y - probe.Bounds.Top) * probe.Bounds.Width + x - probe.Bounds.Left;
                        if (!probe.PreviouslyBlue[oldIndex]) newlyBlue++;
                    }
            }
            return blue >= 24 && newlyBlue >= 16 && newlyBlue >= blue * 0.65;
        }

        private static Bitmap MakeOcrImage(Pixels pixels, Rectangle capture,
            List<HighlightLine> selected, Rectangle bounds)
        {
            int width = bounds.Width + WhiteBorder * 2, height = bounds.Height + WhiteBorder * 2;
            byte[] output = new byte[width * height * 4];
            for (int i = 0; i < output.Length; i++) output[i] = 255;
            foreach (HighlightLine line in selected)
                for (int y = line.Rectangle.Top; y < line.Rectangle.Bottom; y++)
                    for (int x = line.Rectangle.Left; x < line.Rectangle.Right; x++)
                    {
                        int index = (y - capture.Top) * pixels.Width + x - capture.Left;
                        if (!Glyph(pixels, index, line.Red, line.Green, line.Blue, line.LightGlyphs)) continue;
                        int destination = ((y - bounds.Top + WhiteBorder) * width + x - bounds.Left + WhiteBorder) * 4;
                        output[destination] = output[destination + 1] = output[destination + 2] = 0;
                    }
            Bitmap image = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            BitmapData locked = image.LockBits(new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int row = 0; row < height; row++)
                    Marshal.Copy(output, row * width * 4, IntPtr.Add(locked.Scan0, row * locked.Stride), width * 4);
            }
            finally { image.UnlockBits(locked); }
            return image;
        }

        private static bool Glyph(Pixels pixels, int index, int red, int green, int blue, bool light)
        {
            int offset = index * 4;
            int actualBlue = pixels.Data[offset], actualGreen = pixels.Data[offset + 1], actualRed = pixels.Data[offset + 2];
            int luminance = (actualRed * 3 + actualGreen * 6 + actualBlue) / 10;
            int background = (red * 3 + green * 6 + blue) / 10;
            if (light)
                return actualRed >= red + 20 && actualGreen >= green + 16 && luminance >= background + 24;
            return actualBlue <= blue - 32 && actualGreen <= green - 24 && luminance <= background - 36;
        }

        private static bool Blue(int red, int green, int blue)
        {
            return blue >= 90 && blue - red >= 25 && blue - green >= 8 && red <= 220;
        }

        private static string Fingerprint(Pixels pixels, Rectangle capture, Rectangle[] rectangles)
        {
            using (SHA256 hash = SHA256.Create())
            {
                foreach (Rectangle rectangle in rectangles)
                {
                    if (!capture.Contains(rectangle)) return null;
                    byte[] geometry = new byte[16];
                    Buffer.BlockCopy(BitConverter.GetBytes(rectangle.X), 0, geometry, 0, 4);
                    Buffer.BlockCopy(BitConverter.GetBytes(rectangle.Y), 0, geometry, 4, 4);
                    Buffer.BlockCopy(BitConverter.GetBytes(rectangle.Width), 0, geometry, 8, 4);
                    Buffer.BlockCopy(BitConverter.GetBytes(rectangle.Height), 0, geometry, 12, 4);
                    hash.TransformBlock(geometry, 0, geometry.Length, geometry, 0);
                    for (int y = rectangle.Top; y < rectangle.Bottom; y++)
                    {
                        int offset = ((y - capture.Top) * pixels.Width + rectangle.Left - capture.Left) * 4;
                        hash.TransformBlock(pixels.Data, offset, rectangle.Width * 4, pixels.Data, offset);
                    }
                }
                hash.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(hash.Hash).Replace("-", String.Empty);
            }
        }

        private static Pixels ReadPixels(Bitmap image)
        {
            int width = image.Width, height = image.Height;
            if (width < 1 || height < 1 || width > MaximumWidth || height > MaximumHeight)
                throw new ArgumentException("Selection image size is unsupported.");
            Pixels pixels = new Pixels { Width = width, Height = height, Data = new byte[width * height * 4] };
            BitmapData locked = image.LockBits(new Rectangle(0, 0, width, height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int row = 0; row < height; row++)
                    Marshal.Copy(IntPtr.Add(locked.Scan0, row * locked.Stride), pixels.Data, row * width * 4, width * 4);
            }
            finally { image.UnlockBits(locked); }
            return pixels;
        }

        private static Bitmap CopyScreen(Rectangle bounds)
        {
            Bitmap image = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            try
            {
                using (Graphics graphics = Graphics.FromImage(image))
                    graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
                return image;
            }
            catch { image.Dispose(); throw; }
        }

        private static bool TryGetProcess(IntPtr window, out int processId)
        {
            processId = 0;
            if (window == IntPtr.Zero || !IsWindow(window)) return false;
            uint native;
            GetWindowThreadProcessId(window, out native);
            if (native == 0 || native > Int32.MaxValue || native == (uint)OwnProcessId) return false;
            processId = (int)native;
            return true;
        }

        private static Rectangle VirtualScreen()
        {
            int x = GetSystemMetrics(76), y = GetSystemMetrics(77);
            int width = GetSystemMetrics(78), height = GetSystemMetrics(79);
            return width > 0 && height > 0 ? new Rectangle(x, y, width, height) : Rectangle.Empty;
        }
    }
}
