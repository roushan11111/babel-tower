using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace SwipeTranslate
{
    public class SelectionSnapshot
    {
        public string Text;
        public Rectangle Bounds;
        public Rectangle[] Rectangles;
        public IntPtr ForegroundWindow;
        public int ProcessId;
        public bool IsEditable;
        // Keep the originating provider only in memory for a cheap selection
        // recheck. It is not part of the persisted diagnostics or settings.
        internal AutomationElement SourceProvider;
    }

    public sealed class SelectionReadResult
    {
        public SelectionSnapshot Snapshot;
        public bool SensitiveDenied;
    }

    /// <summary>
    /// Reads only actual UI Automation selections. It never synthesizes copy,
    /// accesses the clipboard, or records application text.
    /// Call this on a worker thread: third-party UIA providers can block.
    /// The host must call SetProcessDpiAware before creating its windows.
    /// </summary>
    public static class SelectionReader
    {
        private const int MaximumTextLength = 4000;
        private const int MaximumAncestorDepth = 64;
        private const int MaximumDiscoveryNodes = 320;
        private const int MaximumDiscoveryDepth = 10;
        private const int MaximumFallbackCandidates = 32;
        private const int MaximumSelectionRanges = 32;
        private const int MaximumSelectionRectangles = 128;
        private static readonly int OwnProcessId = Process.GetCurrentProcess().Id;
        [ThreadStatic] private static bool explicitSensitiveRejection;
        private static readonly TreeWalker Walker = TreeWalker.RawViewWalker;

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsChild(IntPtr parent, IntPtr child);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        private sealed class Candidate
        {
            public AutomationElement Element;
            public bool IsFocusedPath;
            public bool IsPointerPath;
        }

        private sealed class DiscoveryNode
        {
            public AutomationElement Element;
            public int Depth;
        }

        public static SelectionSnapshot Capture(IntPtr foregroundWindow, Point pointer)
        {
            return CaptureWithStatus(foregroundWindow, pointer).Snapshot;
        }

        public static SelectionReadResult CaptureWithStatus(IntPtr foregroundWindow, Point pointer)
        {
            explicitSensitiveRejection = false;
            SelectionSnapshot snapshot = CaptureCore(foregroundWindow, pointer);
            return new SelectionReadResult { Snapshot = explicitSensitiveRejection ? null : snapshot,
                SensitiveDenied = explicitSensitiveRejection };
        }

        public static SelectionReadResult RecaptureWithStatus(SelectionSnapshot previous, Point pointer)
        {
            explicitSensitiveRejection = false;
            SelectionSnapshot snapshot = null;
            if (previous != null && previous.SourceProvider != null &&
                previous.ForegroundWindow != IntPtr.Zero && IsWindow(previous.ForegroundWindow) &&
                GetForegroundWindow() == previous.ForegroundWindow)
            {
                uint processId;
                GetWindowThreadProcessId(previous.ForegroundWindow, out processId);
                // A recycled HWND or a different host process is not the original
                // selection window, even if the old UIA provider is still alive.
                if (processId != 0 && processId == (uint)previous.ProcessId &&
                    processId != (uint)OwnProcessId && processId <= Int32.MaxValue &&
                    !IsSensitiveGesture(previous.ForegroundWindow, previous.ProcessId, pointer))
                {
                    double distance;
                    snapshot = ReadCandidate(previous.SourceProvider, previous.ForegroundWindow,
                        previous.ProcessId, pointer, out distance);
                    if (GetForegroundWindow() != previous.ForegroundWindow) snapshot = null;
                }
            }
            return new SelectionReadResult { Snapshot = explicitSensitiveRejection ? null : snapshot,
                SensitiveDenied = explicitSensitiveRejection };
        }

        private static SelectionSnapshot CaptureCore(IntPtr foregroundWindow, Point pointer)
        {
            if (foregroundWindow == IntPtr.Zero || !IsWindow(foregroundWindow) ||
                GetForegroundWindow() != foregroundWindow)
                return null;

            uint nativeProcessId;
            GetWindowThreadProcessId(foregroundWindow, out nativeProcessId);
            if (nativeProcessId == 0 || nativeProcessId == (uint)OwnProcessId ||
                nativeProcessId > Int32.MaxValue)
                return null;
            int processId = (int)nativeProcessId;

            try
            {
                AutomationElement window = AutomationElement.FromHandle(foregroundWindow);
                if (window == null)
                    return null;

                AutomationElement focused = TryGetFocusedElement();
                AutomationElement pointed = TryGetPointElement(pointer);

                // A password control under the gesture or keyboard focus is an
                // immediate veto; do not look for a previous document selection.
                if (IsInForeground(focused, foregroundWindow, processId) &&
                    HasSecureAncestor(focused, foregroundWindow))
                    return null;
                if (IsInForeground(pointed, foregroundWindow, processId) &&
                    HasSecureAncestor(pointed, foregroundWindow))
                    return null;

                List<Candidate> preferred = new List<Candidate>();
                AddAncestorCandidates(preferred, pointed, foregroundWindow, processId, false, true);
                AddAncestorCandidates(preferred, focused, foregroundWindow, processId, true, false);
                SelectionSnapshot best = null;
                double bestScore = Double.NegativeInfinity;
                EvaluateCandidates(preferred, foregroundWindow, processId, pointer, ref best, ref bestScore);

                if (best == null)
                {
                    List<Candidate> fallback = DiscoverCandidates(window, foregroundWindow, processId);
                    EvaluateCandidates(fallback, foregroundWindow, processId, pointer, ref best, ref bestScore);
                }

                // Never return a selection captured after another app gained focus.
                return GetForegroundWindow() == foregroundWindow ? best : null;
            }
            catch (ElementNotAvailableException) { return null; }
            catch (InvalidOperationException) { return null; }
            catch (COMException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
        }

        private static bool IsSensitiveGesture(IntPtr window, int processId, Point pointer)
        {
            AutomationElement focused = TryGetFocusedElement();
            AutomationElement pointed = TryGetPointElement(pointer);
            return (IsInForeground(focused, window, processId) && HasSecureAncestor(focused, window)) ||
                (IsInForeground(pointed, window, processId) && HasSecureAncestor(pointed, window));
        }

        private static AutomationElement TryGetFocusedElement()
        {
            try { return AutomationElement.FocusedElement; }
            catch (ElementNotAvailableException) { return null; }
            catch (InvalidOperationException) { return null; }
            catch (COMException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private static AutomationElement TryGetPointElement(Point pointer)
        {
            try { return AutomationElement.FromPoint(new System.Windows.Point(pointer.X, pointer.Y)); }
            catch (ElementNotAvailableException) { return null; }
            catch (InvalidOperationException) { return null; }
            catch (COMException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private static bool IsInForeground(AutomationElement element, IntPtr window, int processId)
        {
            if (element == null)
                return false;
            try
            {
                // Embedded text providers may belong to a different process.
                // The first real HWND in the ancestry must still belong to the
                // foreground window; never skip a mismatching native window.
                AutomationElement current = element;
                for (int depth = 0; current != null && depth < MaximumAncestorDepth; depth++)
                {
                    int handle = current.Current.NativeWindowHandle;
                    if (handle != 0)
                    {
                        IntPtr nativeWindow = WindowHandle(handle);
                        // Window handles in UIA are 32-bit integers even on x64.
                        return nativeWindow == window || IsChild(window, nativeWindow);
                    }
                    current = Walker.GetParent(current);
                }
                return false;
            }
            catch (ElementNotAvailableException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (COMException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private static bool HasSecureAncestor(AutomationElement element, IntPtr window)
        {
            try
            {
                AutomationElement current = element;
                for (int depth = 0; current != null && depth < MaximumAncestorDepth; depth++)
                {
                    if (current.Current.IsPassword)
                    {
                        explicitSensitiveRejection = true;
                        return true;
                    }
                    if (current.Current.NativeWindowHandle != 0 &&
                        WindowHandle(current.Current.NativeWindowHandle) == window)
                        return false;
                    current = Walker.GetParent(current);
                }
                // Incomplete ancestry cannot establish a safe boundary.
                return true;
            }
            catch (ElementNotAvailableException) { return true; }
            catch (InvalidOperationException) { return true; }
            catch (COMException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }

        private static bool IsSafeTextProvider(AutomationElement element, IntPtr window)
        {
            // Reject the selected provider itself or its password ancestors.
            // An unrelated password field elsewhere in a whole-page provider
            // must not poison an otherwise safe selected range.
            return !HasSecureAncestor(element, window);
        }

        private static void AddAncestorCandidates(List<Candidate> candidates,
            AutomationElement start, IntPtr window, int processId, bool focused, bool pointed)
        {
            AutomationElement current = start;
            try
            {
                if (!IsInForeground(start, window, processId))
                    return;
                for (int depth = 0; current != null && depth < MaximumAncestorDepth; depth++)
                {
                    object pattern;
                    if (current.TryGetCurrentPattern(TextPattern.Pattern, out pattern))
                        AddCandidate(candidates, current, focused, pointed);
                    if (current.Current.NativeWindowHandle != 0 &&
                        WindowHandle(current.Current.NativeWindowHandle) == window)
                        break;
                    current = Walker.GetParent(current);
                }
            }
            catch (ElementNotAvailableException) { }
            catch (InvalidOperationException) { }
            catch (COMException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static void AddCandidate(List<Candidate> candidates, AutomationElement element,
            bool focused, bool pointed)
        {
            foreach (Candidate existing in candidates)
            {
                if (Automation.Compare(existing.Element, element))
                {
                    existing.IsFocusedPath |= focused;
                    existing.IsPointerPath |= pointed;
                    return;
                }
            }
            candidates.Add(new Candidate { Element = element, IsFocusedPath = focused, IsPointerPath = pointed });
        }

        private static List<Candidate> DiscoverCandidates(AutomationElement window,
            IntPtr foregroundWindow, int processId)
        {
            List<Candidate> candidates = new List<Candidate>();
            Stack<DiscoveryNode> stack = new Stack<DiscoveryNode>();
            stack.Push(new DiscoveryNode { Element = window, Depth = 0 });
            int visited = 0;
            while (stack.Count > 0 && visited < MaximumDiscoveryNodes &&
                candidates.Count < MaximumFallbackCandidates)
            {
                DiscoveryNode node = stack.Pop();
                visited++;
                try
                {
                    if (!IsInForeground(node.Element, foregroundWindow, processId) || node.Element.Current.IsPassword)
                        continue;
                    object pattern;
                    if (node.Element.TryGetCurrentPattern(TextPattern.Pattern, out pattern))
                        AddCandidate(candidates, node.Element, false, false);
                    if (node.Depth >= MaximumDiscoveryDepth)
                        continue;
                    AutomationElement child = Walker.GetFirstChild(node.Element);
                    // The budget includes queued nodes, preventing a very wide
                    // provider tree from creating an unbounded sibling walk.
                    while (child != null && visited + stack.Count < MaximumDiscoveryNodes)
                    {
                        stack.Push(new DiscoveryNode { Element = child, Depth = node.Depth + 1 });
                        child = Walker.GetNextSibling(child);
                    }
                }
                catch (ElementNotAvailableException) { }
                catch (InvalidOperationException) { }
                catch (COMException) { }
                catch (UnauthorizedAccessException) { }
            }
            return candidates;
        }

        private static void EvaluateCandidates(List<Candidate> candidates, IntPtr window,
            int processId, Point pointer, ref SelectionSnapshot best, ref double bestScore)
        {
            foreach (Candidate candidate in candidates)
            {
                double distance;
                SelectionSnapshot snapshot = ReadCandidate(candidate.Element, window, processId, pointer, out distance);
                if (snapshot == null)
                    continue;

                // A descendant fallback is usable only when the gesture is at
                // its real selected range, avoiding unrelated stale selections.
                if (!candidate.IsFocusedPath && !candidate.IsPointerPath && distance > 24.0)
                    continue;
                if (!candidate.IsFocusedPath && distance > 72.0)
                    continue;
                double score = 1000.0 / (1.0 + distance);
                if (distance <= 16.0) score += 1000.0;
                if (candidate.IsPointerPath) score += 100.0;
                if (candidate.IsFocusedPath) score += 50.0;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = snapshot;
                }
            }
        }

        private static SelectionSnapshot ReadCandidate(AutomationElement element, IntPtr window,
            int processId, Point pointer, out double minimumDistance)
        {
            minimumDistance = Double.PositiveInfinity;
            try
            {
                if (!IsInForeground(element, window, processId) || !IsSafeTextProvider(element, window))
                    return null;
                object patternObject;
                if (!element.TryGetCurrentPattern(TextPattern.Pattern, out patternObject))
                    return null;
                TextPattern pattern = patternObject as TextPattern;
                if (pattern == null || pattern.SupportedTextSelection == SupportedTextSelection.None)
                    return null;
                TextPatternRange[] ranges = pattern.GetSelection();
                if (ranges == null || ranges.Length == 0 || ranges.Length > MaximumSelectionRanges)
                    return null;

                StringBuilder text = new StringBuilder();
                Rectangle selectionBounds = Rectangle.Empty;
                Rectangle virtualScreen = GetVirtualScreenBounds();
                List<Rectangle> selectionRectangles = new List<Rectangle>();
                HashSet<Rectangle> seenRectangles = new HashSet<Rectangle>();
                bool haveBounds = false;
                bool editable = true;
                int nonemptyRanges = 0;
                foreach (TextPatternRange range in ranges)
                {
                    if (range == null)
                        continue;
                    if (GetForegroundWindow() != window)
                        return null;
                    // Read one character beyond the limit to detect excess;
                    // never send a silently truncated selection for translation.
                    string selectedText = range.GetText(MaximumTextLength + 1);
                    if (String.IsNullOrWhiteSpace(selectedText))
                        continue;
                    int separatorLength = nonemptyRanges == 0 ? 0 : 1;
                    if (selectedText.Length > MaximumTextLength ||
                        text.Length + separatorLength + selectedText.Length > MaximumTextLength)
                        return null;
                    if (separatorLength != 0) text.Append('\n');
                    text.Append(selectedText);
                    nonemptyRanges++;

                    object readOnly = range.GetAttributeValue(TextPattern.IsReadOnlyAttribute);
                    if (!(readOnly is bool) || (bool)readOnly)
                        editable = false;

                    System.Windows.Rect[] rectangles = range.GetBoundingRectangles();
                    if (rectangles == null)
                        continue;
                    foreach (System.Windows.Rect rangeRectangle in rectangles)
                    {
                        Rectangle bounds;
                        if (!TryConvertRectangle(rangeRectangle.X, rangeRectangle.Y,
                            rangeRectangle.Width, rangeRectangle.Height, out bounds))
                            continue;
                        // UIA range rectangles are physical screen coordinates.
                        // Keep visible portions, including negative coordinates
                        // on monitors to the left or above the primary display.
                        if (!virtualScreen.IsEmpty)
                            bounds = Rectangle.Intersect(bounds, virtualScreen);
                        if (bounds.Width <= 0 || bounds.Height <= 0 || !seenRectangles.Add(bounds))
                            continue;
                        // Reject excessive geometry rather than covering only a
                        // truncated portion of a successfully translated selection.
                        if (selectionRectangles.Count >= MaximumSelectionRectangles)
                            return null;
                        selectionRectangles.Add(bounds);
                        selectionBounds = haveBounds ? Rectangle.Union(selectionBounds, bounds) : bounds;
                        haveBounds = true;
                        double xDistance = pointer.X < bounds.Left ? bounds.Left - (double)pointer.X :
                            (pointer.X > bounds.Right ? pointer.X - (double)bounds.Right : 0.0);
                        double yDistance = pointer.Y < bounds.Top ? bounds.Top - (double)pointer.Y :
                            (pointer.Y > bounds.Bottom ? pointer.Y - (double)bounds.Bottom : 0.0);
                        minimumDistance = Math.Min(minimumDistance,
                            Math.Sqrt(xDistance * xDistance + yDistance * yDistance));
                    }
                }
                if (text.Length == 0 || !haveBounds)
                    return null;
                selectionRectangles.Sort(delegate(Rectangle first, Rectangle second)
                {
                    int comparison = first.Top.CompareTo(second.Top);
                    if (comparison != 0) return comparison;
                    comparison = first.Left.CompareTo(second.Left);
                    if (comparison != 0) return comparison;
                    comparison = first.Height.CompareTo(second.Height);
                    return comparison != 0 ? comparison : first.Width.CompareTo(second.Width);
                });
                return new SelectionSnapshot
                {
                    Text = text.ToString(),
                    Bounds = selectionBounds,
                    Rectangles = selectionRectangles.ToArray(),
                    ForegroundWindow = window,
                    ProcessId = processId,
                    IsEditable = editable,
                    SourceProvider = element
                };
            }
            catch (ElementNotAvailableException) { return null; }
            catch (InvalidOperationException) { return null; }
            catch (COMException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
        }

        private static bool TryConvertRectangle(double x, double y, double width, double height,
            out Rectangle rectangle)
        {
            rectangle = Rectangle.Empty;
            if (!IsFinite(x) || !IsFinite(y) || !IsFinite(width) || !IsFinite(height) ||
                width <= 0.0 || height <= 0.0)
                return false;
            double left = Math.Floor(x);
            double top = Math.Floor(y);
            double right = Math.Ceiling(x + width);
            double bottom = Math.Ceiling(y + height);
            if (!IsFinite(right) || !IsFinite(bottom) || left < -1000000.0 || top < -1000000.0 ||
                right > 1000000.0 || bottom > 1000000.0 || right <= left || bottom <= top)
                return false;
            rectangle = Rectangle.FromLTRB((int)left, (int)top, (int)right, (int)bottom);
            return true;
        }

        private static bool IsFinite(double value)
        {
            return !Double.IsNaN(value) && !Double.IsInfinity(value);
        }

        private static Rectangle GetVirtualScreenBounds()
        {
            // SM_X/Y/CX/CYVIRTUALSCREEN. System-DPI-aware hosts receive physical
            // coordinates, matching TextPatternRange.GetBoundingRectangles().
            int x = GetSystemMetrics(76);
            int y = GetSystemMetrics(77);
            int width = GetSystemMetrics(78);
            int height = GetSystemMetrics(79);
            if (width <= 0 || height <= 0)
                return Rectangle.Empty;
            Rectangle bounds;
            return TryConvertRectangle(x, y, width, height, out bounds) ? bounds : Rectangle.Empty;
        }

        private static IntPtr WindowHandle(int handle)
        {
            // UIA exposes HWND as Int32. Preserve its unsigned bits on x64.
            return IntPtr.Size == 8 ? new IntPtr((long)(uint)handle) : new IntPtr(handle);
        }
    }
}
