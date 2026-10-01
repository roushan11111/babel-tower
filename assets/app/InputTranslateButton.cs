using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;

namespace SwipeTranslate
{
    // Native input assistance deliberately has no clipboard or keyboard fallback.
    // Browser contenteditable inputs are handled by the companion extension.
    // Construct and dispose this controller on the application's UI thread.
    sealed class InputTranslationController : IDisposable
    {
        const int ReadTimeoutMilliseconds = 650;
        readonly Func<string, string, string, CancellationToken, Task<string>> translate;
        readonly Func<Options> options;
        readonly Action<string> status;
        readonly InputTranslationButton button = new InputTranslationButton();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        Task<EditableInputSnapshot> reader;
        EditableInputSnapshot visibleInput;
        EditableInputSnapshot requestedInput;
        CancellationTokenSource request;
        bool enabled, disposed, polling, committing;

        public InputTranslationController(Func<string, string, string, CancellationToken, Task<string>> translate,
            Func<Options> options, Action<string> status)
        {
            if (translate == null || options == null) throw new ArgumentNullException();
            this.translate = translate;
            this.options = options;
            this.status = status ?? delegate { };
            // Keep the handle on the UI thread before any asynchronous continuation.
            IntPtr handle = button.Handle;
            button.TranslateClicked += TranslateClicked;
            timer.Interval = 250;
            timer.Tick += Poll;
            timer.Start();
        }

        public bool Enabled
        {
            get { return enabled; }
            set
            {
                if (disposed) return;
                enabled = value;
                if (!enabled) InvalidateInput();
            }
        }

        // Call after language settings change, so an old result never uses new settings.
        public void CancelPending()
        {
            if (!disposed) InvalidateInput();
        }

        async void Poll(object sender, EventArgs args)
        {
            if (disposed || !enabled || polling || committing) return;
            if (visibleInput != null && !EditableInputAccess.NativeStillFocused(visibleInput)) InvalidateInput();
            polling = true;
            try
            {
                EditableInputSnapshot current = await ReadOnceAsync(CancellationToken.None);
                if (disposed || !enabled || committing) return;
                if (current == null)
                {
                    InvalidateInput();
                    return;
                }
                if (requestedInput != null && !EditableInputAccess.SameDraft(requestedInput, current))
                    CancelRequest();
                visibleInput = current;
                button.Busy = request != null;
                button.ShowAt(current.Bounds);
            }
            catch { if (!disposed) InvalidateInput(); }
            finally { polling = false; }
        }

        async Task<EditableInputSnapshot> ReadOnceAsync(CancellationToken cancellation)
        {
            // A blocked provider occupies at most one reader. Do not stack more
            // ThreadPool tasks when an application stops answering UI Automation.
            if (reader != null && !reader.IsCompleted) return null;
            cancellation.ThrowIfCancellationRequested();
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(ReadTimeoutMilliseconds);
            Task<EditableInputSnapshot> current = Task.Run(delegate { return EditableInputAccess.Capture(); });
            reader = current;
            Task completed = await Task.WhenAny(current, Task.Delay(ReadTimeoutMilliseconds, cancellation));
            cancellation.ThrowIfCancellationRequested();
            if (completed != current || DateTime.UtcNow > deadline) return null;
            return await current;
        }

        async void TranslateClicked(object sender, EventArgs args)
        {
            if (disposed || !enabled || request != null || visibleInput == null || committing) return;
            EditableInputSnapshot expected = visibleInput;
            if (!EditableInputAccess.NativeStillFocused(expected)) { InvalidateInput(); return; }
            var cancellation = new CancellationTokenSource();
            request = cancellation;
            button.Busy = true;
            try
            {
                // A click may arrive during the regular focus poll. Let that
                // bounded read finish, then obtain a fresh click-time snapshot.
                if (reader != null && !reader.IsCompleted)
                {
                    Task finished = await Task.WhenAny(reader, Task.Delay(ReadTimeoutMilliseconds, cancellation.Token));
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (finished != reader) throw new OperationCanceledException();
                }
                // Use the full current draft at the deliberate click, rather than
                // text captured while the user was still typing.
                EditableInputSnapshot current = await ReadOnceAsync(cancellation.Token);
                if (current == null || !EditableInputAccess.SameIdentity(expected, current))
                    throw new OperationCanceledException();
                requestedInput = current;
                Options languages = options().Copy();
                languages.Validate();
                status("正在本地翻译输入框草稿");
                string translated = await translate(current.Text, languages.SourceLanguage,
                    languages.TargetLanguage, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (!EditableInputAccess.IsTranslationUsable(translated))
                    throw new InvalidOperationException("输入译文为空或过长");
                if (disposed || !enabled || request != cancellation ||
                    !EditableInputAccess.NativeStillFocused(current)) throw new OperationCanceledException();
                // No poll can race a write. Await an already running reader; if it
                // fails to finish promptly, keep the original draft unchanged.
                committing = true;
                if (reader != null && !reader.IsCompleted)
                {
                    Task finished = await Task.WhenAny(reader, Task.Delay(ReadTimeoutMilliseconds, cancellation.Token));
                    if (finished != reader) throw new OperationCanceledException();
                }
                cancellation.Token.ThrowIfCancellationRequested();
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(ReadTimeoutMilliseconds);
                InputWriteResult result = await Task.Run(delegate
                {
                    return EditableInputAccess.TryApply(current, translated, deadline, cancellation.Token);
                });
                if (disposed) return;
                if (result == InputWriteResult.Applied) status("输入框已换成译文，请检查后自行发送");
                else if (result == InputWriteResult.Unconfirmed) status("输入写入结果未确认，请检查原输入框");
                else status("草稿或焦点已变化，已放弃输入替换");
            }
            catch (OperationCanceledException) { if (!disposed) status("输入翻译已取消，草稿未自动发送"); }
            catch { if (!disposed) status("输入翻译未完成，请保留并检查原草稿"); }
            finally
            {
                committing = false;
                if (request == cancellation)
                {
                    request = null;
                    requestedInput = null;
                    if (!disposed) button.Busy = false;
                }
                cancellation.Dispose();
            }
        }

        void CancelRequest()
        {
            if (request != null) { try { request.Cancel(); } catch (ObjectDisposedException) { } }
            requestedInput = null;
        }

        void InvalidateInput()
        {
            CancelRequest();
            visibleInput = null;
            button.Hide();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            enabled = false;
            timer.Stop();
            timer.Dispose();
            CancelRequest();
            button.Dispose();
        }
    }

    enum InputWriteResult { Declined, Applied, Unconfirmed }

    sealed class EditableInputSnapshot
    {
        internal string Text;
        internal Rectangle Bounds;
        internal IntPtr Foreground;
        internal IntPtr Handle;
        internal int ProcessId;
        internal int[] RuntimeId;
        internal ValuePattern Value;
    }

    static class EditableInputAccess
    {
        const int MaximumDraftLength = 4000;
        const int MaximumTranslationLength = 8000;
        const int GaRoot = 2, GwlStyle = -16, EsPassword = 0x20, EsReadOnly = 0x800;
        static readonly int OwnProcessId = Process.GetCurrentProcess().Id;

        [StructLayout(LayoutKind.Sequential)]
        struct NativeRectangle { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        struct GuiThreadInfo
        {
            public int Size, Flags;
            public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
            public NativeRectangle CaretRectangle;
        }
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window, int flags);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr window);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out NativeRectangle rectangle);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder text, int length);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr window, int index);

        internal static bool IsDraftUsable(string text)
        {
            return !String.IsNullOrWhiteSpace(text) && text.Length <= MaximumDraftLength && text.IndexOf('\0') < 0;
        }
        internal static bool IsTranslationUsable(string text)
        {
            return !String.IsNullOrWhiteSpace(text) && text.Length <= MaximumTranslationLength && text.IndexOf('\0') < 0;
        }
        internal static bool IsNativeEditClass(string className)
        {
            return String.Equals(className, "Edit", StringComparison.OrdinalIgnoreCase) ||
                (className != null && className.StartsWith("WindowsForms10.EDIT.", StringComparison.OrdinalIgnoreCase));
        }
        internal static bool SameIdentity(EditableInputSnapshot left, EditableInputSnapshot right)
        {
            if (left == null || right == null || left.Handle != right.Handle || left.Foreground != right.Foreground ||
                left.ProcessId != right.ProcessId || left.RuntimeId == null || right.RuntimeId == null ||
                left.RuntimeId.Length != right.RuntimeId.Length) return false;
            for (int i = 0; i < left.RuntimeId.Length; ++i) if (left.RuntimeId[i] != right.RuntimeId[i]) return false;
            return true;
        }
        internal static bool SameDraft(EditableInputSnapshot left, EditableInputSnapshot right)
        {
            return SameIdentity(left, right) && String.Equals(left.Text, right.Text, StringComparison.Ordinal);
        }
        internal static bool NativeStillFocused(EditableInputSnapshot snapshot)
        {
            if (snapshot == null || snapshot.Foreground == IntPtr.Zero || snapshot.Handle == IntPtr.Zero ||
                GetForegroundWindow() != snapshot.Foreground || !IsWindow(snapshot.Handle) ||
                !IsWindowVisible(snapshot.Handle) || !IsWindowEnabled(snapshot.Handle) ||
                GetAncestor(snapshot.Handle, GaRoot) != snapshot.Foreground) return false;
            uint processId;
            uint threadId = GetWindowThreadProcessId(snapshot.Foreground, out processId);
            if (processId != (uint)snapshot.ProcessId || processId == (uint)OwnProcessId) return false;
            uint controlProcessId;
            GetWindowThreadProcessId(snapshot.Handle, out controlProcessId);
            if (controlProcessId != processId) return false;
            var info = new GuiThreadInfo { Size = Marshal.SizeOf(typeof(GuiThreadInfo)) };
            if (!GetGUIThreadInfo(threadId, ref info) || info.Focus != snapshot.Handle) return false;
            int style = GetWindowLong(snapshot.Handle, GwlStyle);
            if ((style & (EsPassword | EsReadOnly)) != 0) return false;
            var className = new StringBuilder(256);
            GetClassName(snapshot.Handle, className, className.Capacity);
            return IsNativeEditClass(className.ToString());
        }

        // The caller runs this on a worker thread and bounds its response time.
        internal static EditableInputSnapshot Capture()
        {
            try
            {
                IntPtr foreground = GetForegroundWindow();
                uint processId;
                GetWindowThreadProcessId(foreground, out processId);
                if (foreground == IntPtr.Zero || processId == 0 || processId > Int32.MaxValue ||
                    processId == (uint)OwnProcessId) return null;
                AutomationElement focused = AutomationElement.FocusedElement;
                if (focused == null) return null;
                AutomationElement.AutomationElementInformation details = focused.Current;
                if (details.ProcessId != (int)processId || !details.HasKeyboardFocus || !details.IsKeyboardFocusable ||
                    !details.IsEnabled || details.IsOffscreen || details.IsPassword ||
                    details.ControlType != ControlType.Edit || details.NativeWindowHandle == 0) return null;
                var snapshot = new EditableInputSnapshot { Foreground = foreground, ProcessId = (int)processId,
                    Handle = new IntPtr(details.NativeWindowHandle), RuntimeId = focused.GetRuntimeId() };
                if (!NativeStillFocused(snapshot)) return null;
                object pattern;
                if (!focused.TryGetCurrentPattern(ValuePattern.Pattern, out pattern)) return null;
                ValuePattern value = pattern as ValuePattern;
                if (value == null || value.Current.IsReadOnly) return null;
                string text = value.Current.Value;
                if (!IsDraftUsable(text)) return null;
                NativeRectangle nativeBounds;
                if (!GetWindowRect(snapshot.Handle, out nativeBounds)) return null;
                var bounds = Rectangle.FromLTRB(nativeBounds.Left, nativeBounds.Top, nativeBounds.Right, nativeBounds.Bottom);
                if (bounds.Width < 36 || bounds.Height < 14 || bounds.Width > 4096 || bounds.Height > 1200) return null;
                System.Windows.Rect uiaBounds = details.BoundingRectangle;
                if (uiaBounds.IsEmpty || Double.IsInfinity(uiaBounds.X) || Double.IsInfinity(uiaBounds.Y) ||
                    Math.Abs(uiaBounds.Left - bounds.Left) > 8 || Math.Abs(uiaBounds.Top - bounds.Top) > 8 ||
                    Math.Abs(uiaBounds.Right - bounds.Right) > 8 || Math.Abs(uiaBounds.Bottom - bounds.Bottom) > 8) return null;
                if (!NativeStillFocused(snapshot)) return null;
                if (snapshot.RuntimeId == null || snapshot.RuntimeId.Length == 0 ||
                    Double.IsNaN(uiaBounds.Left) || Double.IsNaN(uiaBounds.Top) ||
                    Double.IsNaN(uiaBounds.Right) || Double.IsNaN(uiaBounds.Bottom)) return null;
                snapshot.Text = text;
                snapshot.Bounds = bounds;
                snapshot.Value = value;
                return snapshot;
            }
            catch { return null; }
        }

        internal static InputWriteResult TryApply(EditableInputSnapshot expected, string translation,
            DateTime deadline, CancellationToken cancellation)
        {
            if (expected == null || !IsTranslationUsable(translation) || cancellation.IsCancellationRequested ||
                DateTime.UtcNow > deadline) return InputWriteResult.Declined;
            if (!NativeStillFocused(expected)) return InputWriteResult.Declined;
            EditableInputSnapshot current = Capture();
            if (!SameDraft(expected, current) || cancellation.IsCancellationRequested || DateTime.UtcNow > deadline ||
                !NativeStillFocused(current)) return InputWriteResult.Declined;
            try
            {
                // This is the only write. SetValue replaces an editable value;
                // it never clicks Send, presses Enter, or moves keyboard focus.
                current.Value.SetValue(translation);
                return String.Equals(current.Value.Current.Value, translation, StringComparison.Ordinal) ?
                    InputWriteResult.Applied : InputWriteResult.Unconfirmed;
            }
            catch { return InputWriteResult.Unconfirmed; }
        }
    }

    sealed class InputTranslationButton : Form
    {
        internal event EventHandler TranslateClicked;
        bool busy;
        internal bool Busy { get { return busy; } set { if (busy != value) { busy = value; Invalidate(); } } }
        internal InputTranslationButton()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(30, 30);
            BackColor = Color.FromArgb(28, 91, 182);
            ForeColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold);
            Cursor = Cursors.Hand;
            AccessibleName = "翻译当前输入框草稿，不发送";
            DoubleBuffered = true;
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { CreateParams parameters = base.CreateParams; parameters.ExStyle |= 0x08000000 | 0x00000080; return parameters; }
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0021) { message.Result = new IntPtr(3); return; } // MA_NOACTIVATE
            base.WndProc(ref message);
        }
        protected override void OnPaint(PaintEventArgs args)
        {
            base.OnPaint(args);
            TextRenderer.DrawText(args.Graphics, Busy ? "…" : "译", Font, ClientRectangle, ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
        protected override void OnMouseUp(MouseEventArgs args)
        {
            base.OnMouseUp(args);
            if (!Busy && args.Button == MouseButtons.Left && ClientRectangle.Contains(args.Location))
            { EventHandler clicked = TranslateClicked; if (clicked != null) clicked(this, EventArgs.Empty); }
        }
        internal void ShowAt(Rectangle input)
        {
            Rectangle work = Screen.FromRectangle(input).WorkingArea;
            if (!work.IntersectsWith(input)) { Hide(); return; }
            int x = Math.Min(input.Right + 4, work.Right - Width);
            int y = Math.Max(work.Top, Math.Min(input.Top + (input.Height - Height) / 2, work.Bottom - Height));
            x = Math.Max(work.Left, x);
            Location = new Point(x, y);
            if (!Visible) Show();
        }
    }
}
