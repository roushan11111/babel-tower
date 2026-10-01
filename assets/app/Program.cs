using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Web.Script.Serialization;

namespace SwipeTranslate
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Native.SetProcessDpiAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (Array.IndexOf(args, "--fixture") >= 0)
            {
                Application.Run(new Fixture());
                return;
            }
            bool created;
            using (var single = new Mutex(true, "Local\\SwipeTranslate.Prototype.v1", out created))
            {
                if (!created) return;
                try { Application.Run(new TranslatorContext(Array.IndexOf(args, "--background") < 0)); }
                catch (Exception ex) { MessageBox.Show("巴别塔启动失败：" + ex.Message, "巴别塔"); }
            }
        }
    }

    class Options
    {
        public string TargetLanguage = "zh-CN";
        public string SourceLanguage = "auto";
        public bool CoverSelection = true;
        public bool RecognizeBlueSelection = true;
        public static string PathName { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json"); } }
        public static Options Load()
        {
            return Load(PathName);
        }
        internal static Options Load(string path)
        {
            try
            {
                var loaded = new JavaScriptSerializer().Deserialize<Options>(File.ReadAllText(path)) ?? new Options();
                loaded.Validate();
                return loaded;
            }
            catch { return new Options(); }
        }
        public void Save()
        {
            Save(PathName);
        }
        internal void Save(string path)
        {
            Validate();
            string temporary = path + ".tmp";
            try
            {
                File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(this), new System.Text.UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }
        internal Options Copy()
        {
            return new Options { SourceLanguage = SourceLanguage, TargetLanguage = TargetLanguage,
                CoverSelection = CoverSelection, RecognizeBlueSelection = RecognizeBlueSelection };
        }
        internal void CopyFrom(Options other)
        {
            other.Validate();
            SourceLanguage = other.SourceLanguage;
            TargetLanguage = other.TargetLanguage;
            CoverSelection = other.CoverSelection;
            RecognizeBlueSelection = other.RecognizeBlueSelection;
        }
        internal void Validate()
        {
            if (!LanguageChoice.Contains(SourceLanguage, true) || !LanguageChoice.Contains(TargetLanguage, false))
                throw new InvalidOperationException("请选择原文语言和译文语言。");
            if (String.Equals(SourceLanguage, TargetLanguage, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("原文和译文语言相同，请选择不同语言。");
        }
    }

    sealed class LanguageChoice
    {
        public readonly string Name;
        public readonly string Code;
        public LanguageChoice(string name, string code) { Name = name; Code = code; }
        public override string ToString() { return Name; }
        internal static readonly LanguageChoice[] All = {
            new LanguageChoice("自动识别", "auto"), new LanguageChoice("简体中文", "zh-CN"),
            new LanguageChoice("繁体中文", "zh-TW"), new LanguageChoice("英文", "en"),
            new LanguageChoice("日文", "ja"), new LanguageChoice("韩文", "ko"),
            new LanguageChoice("法文", "fr"), new LanguageChoice("德文", "de"),
            new LanguageChoice("西班牙文", "es"), new LanguageChoice("俄文", "ru") };
        internal static bool Contains(string code, bool source)
        {
            foreach (var language in All)
                if ((source || language.Code != "auto") && String.Equals(language.Code, code, StringComparison.Ordinal)) return true;
            return false;
        }
    }

    sealed class TranslatorContext : ApplicationContext
    {
        readonly Control dispatcher = new Control();
        readonly TranslationEngine engine = new TranslationEngine();
        readonly TranslationPopup popup = new TranslationPopup();
        readonly InlineOverlay inline = new InlineOverlay();
        readonly Options options = Options.Load();
        readonly NotifyIcon tray;
        readonly MouseWatcher mouse;
        readonly EscapeWatcher escape;
        readonly System.Windows.Forms.Timer lifetime = new System.Windows.Forms.Timer();
        readonly CancellationTokenSource startup = new CancellationTokenSource();
        readonly Task modelPreparation;
        readonly ToolStripMenuItem pause = new ToolStripMenuItem("暂停划选翻译");
        CancellationTokenSource pending;
        Task<SelectionReadResult> captureTask;
        Task<CapturedSelection> imageTask;
        Task<SelectionSnapshot> imageRecheckTask;
        bool enabled = true;
        int revision;
        IntPtr selectionWindow;
        DateTime visibleUntil;
        string lastText;
        IntPtr lastWindow;
        DateTime lastAt;
        SettingsWindow settings;
        string statusText = "正在准备本地翻译";

        public TranslatorContext(bool showWelcome)
        {
            var handle = dispatcher.Handle;
            tray = new NotifyIcon();
            tray.Icon = SystemIcons.Information;
            tray.Text = "巴别塔：正在准备本地翻译…";
            var menu = new ContextMenuStrip();
            pause.Click += delegate { enabled = !enabled; pause.Text = enabled ? "暂停划选翻译" : "恢复划选翻译"; Dismiss(); };
            menu.Items.Add(pause);
            menu.Items.Add("设置 · 语言与显示", null, delegate { ShowSettings(); });
            menu.Items.Add("查看运行状态", null, delegate
            {
                MessageBox.Show("当前状态：" + statusText + "\n\n检测到鼠标按下：" + mouse.DownCount +
                    " 次\n检测到划选：" + mouse.SelectionCount + " 次\n本地蓝色选区识别：" +
                    (options.RecognizeBlueSelection ? "开启" : "关闭") + "\n\n状态不包含选中文字或截图。", "巴别塔运行状态");
            });
            menu.Items.Add("打开测试页", null, delegate { OpenFixture(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出巴别塔", null, delegate { ExitThread(); });
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { ShowSettings(); };
            tray.Visible = true;
            mouse = new MouseWatcher { EnableImageProbe = options.RecognizeBlueSelection };
            mouse.BeforeProbe += HideBeforeProbe;
            mouse.Gesture += OnGesture;
            escape = new EscapeWatcher();
            escape.Pressed += OnEscape;
            lifetime.Interval = 100;
            lifetime.Tick += delegate
            {
                mouse.RefreshIfIdle();
                if ((popup.Visible || inline.Visible || (pending != null && selectionWindow != IntPtr.Zero)) && (DateTime.UtcNow > visibleUntil ||
                    Native.GetForegroundWindow() != selectionWindow || (Native.GetAsyncKeyState(27) & 0x8000) != 0)) Dismiss();
            };
            lifetime.Start();
            ReportStage("started", "正在准备本地翻译");
            modelPreparation = PrepareLocalModelAsync();
            if (showWelcome) ShowSettings();
        }

        async Task PrepareLocalModelAsync()
        {
            try
            {
                await LocalModelHost.EnsureStartedAsync(startup.Token);
                // A fixed sample prepares GPU kernels before real selections.
                await LocalModelHost.WarmUpAsync(startup.Token);
                if (!dispatcher.IsDisposed && pending == null) ReportStage("ready", "本地腾讯 Hy-MT2，已就绪");
            }
            catch (OperationCanceledException)
            {
                if (!startup.IsCancellationRequested && !dispatcher.IsDisposed)
                    ReportStage("model_slow", "本地模型准备较慢，请稍后划选");
            }
            catch (Exception)
            {
                if (!dispatcher.IsDisposed) ReportStage("model_not_ready", "本地翻译暂未就绪，可重新划选");
            }
        }

        void ApplySettings(Options changed)
        {
            // Persist first: a failed save must not silently change the running configuration.
            changed.Save();
            Dismiss();
            options.CopyFrom(changed);
            mouse.EnableImageProbe = options.RecognizeBlueSelection;
            lastText = null;
            lastWindow = IntPtr.Zero;
        }

        void OnEscape()
        {
            if (dispatcher.IsDisposed || !dispatcher.IsHandleCreated) return;
            revision++;
            try
            {
                // Queue the UI work so the keyboard callback returns immediately.
                dispatcher.BeginInvoke((Action)delegate
                {
                    if (!dispatcher.IsDisposed) Dismiss();
                });
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        void OnGesture(bool selectionCandidate, Point start, Point point, IntPtr window, SelectionGestureProbe probe)
        {
            if (dispatcher.IsDisposed) return;
            dispatcher.BeginInvoke((Action)delegate
            {
                if (popup.Visible && popup.Bounds.Contains(point)) return;
                uint pid;
                Native.GetWindowThreadProcessId(window, out pid);
                if (pid == Process.GetCurrentProcess().Id) return;
                Dismiss();
                if (enabled && selectionCandidate)
                {
                    ReportStage("gesture_seen", "检测到划选，正在读取");
                    TranslateSelection(start, point, window, probe);
                }
            });
        }

        void HideBeforeProbe(Point point)
        {
            // Hook callbacks run on the installing WinForms thread. Hide only;
            // cancellation stays queued so it cannot slow the native hook.
            if (popup.Visible && popup.Bounds.Contains(point)) return;
            revision++;
            inline.Hide();
            popup.Hide();
        }

        void ReportStage(string stage, string label)
        {
            statusText = label;
            if (!dispatcher.IsDisposed) tray.Text = "巴别塔：" + label;
            RuntimeDiagnostic.Write(stage, mouse == null ? 0 : mouse.DownCount,
                mouse == null ? 0 : mouse.SelectionCount, options.RecognizeBlueSelection);
        }

        async Task<CapturedSelection> CaptureSelectionAsync(Point start, Point point, IntPtr window,
            SelectionGestureProbe probe, string sourceLanguage, string targetLanguage, bool recognizeSelection, CancellationToken token)
        {
            // An unresponsive text provider cannot block the image fallback or
            // cause an unbounded number of UIA worker threads.
            if (captureTask == null || captureTask.IsCompleted)
            {
                captureTask = Task.Run(() => SelectionReader.CaptureWithStatus(window, point));
                ObserveLateFailure(captureTask);
                Task finished = await Task.WhenAny(captureTask, Task.Delay(2000, token));
                token.ThrowIfCancellationRequested();
                if (finished == captureTask)
                {
                    SelectionReadResult reading = await captureTask;
                    if (reading.SensitiveDenied)
                    {
                        ReportStage("protected_selection", "已跳过受保护的输入内容");
                        return new CapturedSelection { IsProtected = true };
                    }
                    if (reading.Snapshot != null)
                    {
                        ReportStage("uia_read", "已读取选中文字");
                        return new CapturedSelection { Snapshot = reading.Snapshot };
                    }
                    ReportStage("uia_empty", "文字接口没有提供选区");
                }
                else ReportStage("uia_timeout", "文字读取较慢，尝试本地识别");
            }
            else ReportStage("uia_busy", "文字接口仍在处理上一段内容");
            if (!recognizeSelection) { ReportStage("ocr_disabled", "本地蓝色选区识别已关闭"); return null; }
            if (probe == null) { ReportStage("probe_missing", "没有确认这次划选的起点"); return null; }
            if (Native.GetForegroundWindow() != window) return null;
            if (imageTask != null && !imageTask.IsCompleted) { ReportStage("ocr_busy", "本地识别仍在处理上一段内容"); return null; }
            ReportStage("ocr_reading", "正在本地识别蓝色选区");
            imageTask = Task.Run(delegate
            {
                token.ThrowIfCancellationRequested();
                using (SelectionImageSnapshot image = SelectionImageReader.Capture(window, start, point, probe))
                {
                    if (image == null) return null;
                    string text, reason;
                    if (!LocalSelectionOcr.TryRead(image.Image, sourceLanguage, targetLanguage, out text, out reason))
                    {
                        return new CapturedSelection { Error = String.IsNullOrEmpty(reason) ?
                            "暂时无法识别这段选中文字，请选择更清晰或更短的文字后重试。" : reason };
                    }
                    token.ThrowIfCancellationRequested();
                    return new CapturedSelection { Snapshot = image.ToSelectionSnapshot(text),
                        FromImage = true, Fingerprint = image.Fingerprint, ImageMetadata = image };
                }
            });
            ObserveLateFailure(imageTask);
            Task completed = await Task.WhenAny(imageTask, Task.Delay(6000, token));
            token.ThrowIfCancellationRequested();
            CapturedSelection captured = completed == imageTask ? await imageTask : null;
            if (captured != null && captured.Snapshot != null) ReportStage("ocr_read", "已识别选中文字");
            else ReportStage(completed == imageTask ? "image_unconfirmed" : "ocr_timeout", "没有可靠识别出这个选区");
            return captured;
        }

        async void TranslateSelection(Point start, Point point, IntPtr window, SelectionGestureProbe probe)
        {
            int ticket = revision;
            // A settings save cancels this request. Keep worker language hints
            // fixed until cancellation has reached any background OCR work.
            Options requestOptions = options.Copy();
            var cancellation = new CancellationTokenSource();
            pending = cancellation;
            selectionWindow = window;
            visibleUntil = DateTime.UtcNow.AddSeconds(150);
            try
            {
                await Task.Delay(110, cancellation.Token);
                if (ticket != revision || Native.GetForegroundWindow() != window) return;
                CapturedSelection captured = await CaptureSelectionAsync(start, point, window, probe,
                    requestOptions.SourceLanguage, requestOptions.TargetLanguage, requestOptions.RecognizeBlueSelection, cancellation.Token);
                if (ticket != revision || Native.GetForegroundWindow() != window) return;
                if (captured != null && captured.IsProtected) return;
                if (captured != null && captured.Error != null) throw new InvalidOperationException(captured.Error);
                var snapshot = captured == null ? null : captured.Snapshot;
                if (snapshot == null)
                {
                    visibleUntil = DateTime.UtcNow.AddSeconds(5);
                    popup.Present("没能读取这个选区。可以先点空白处，再完整划选一行文字。", Rectangle.Empty, point, false, false);
                    return;
                }
                if (ticket != revision || Native.GetForegroundWindow() != window) return;
                string text = snapshot.Text.Trim();
                if (text.Length == 0) return;
                if (text == lastText && window == lastWindow && DateTime.UtcNow - lastAt < TimeSpan.FromMilliseconds(500)) return;
                lastText = text; lastWindow = window; lastAt = DateTime.UtcNow;
                selectionWindow = window;
                visibleUntil = DateTime.UtcNow.AddSeconds(140);
                popup.Present(modelPreparation.IsCompleted ? "正在翻译…" : "正在准备本地翻译，请稍候…", snapshot.Bounds, point, false, false);
                if (popup.Bounds.IntersectsWith(snapshot.Bounds)) popup.Hide();
                ReportStage("model_wait", "正在准备或调用本地翻译");
                await Task.WhenAny(modelPreparation, Task.Delay(130000, cancellation.Token));
                cancellation.Token.ThrowIfCancellationRequested();
                if (ticket != revision || Native.GetForegroundWindow() != window) return;
                if (!modelPreparation.IsCompleted) throw new TimeoutException("本地模型正在准备，请稍后重新划选。");
                // A transient preparation failure must not block later requests.
                // Give the actual translation its own full timeout after warmup.
                visibleUntil = DateTime.UtcNow.AddSeconds(50);
                ReportStage("translating", "正在本地翻译");
                string result = await engine.TranslateAsync(text, requestOptions.SourceLanguage, requestOptions.TargetLanguage, cancellation.Token);
                if (ticket != revision || Native.GetForegroundWindow() != window) return;
                // The user may change the selection or the app may move it while
                // a translation is running. Never cover an obsolete range.
                SelectionSnapshot current;
                ReportStage("rechecking", "翻译完成，正在确认选区");
                if (captured.FromImage)
                {
                    if (imageRecheckTask != null && !imageRecheckTask.IsCompleted) return;
                    imageRecheckTask = Task.Run(delegate
                    {
                        using (SelectionImageSnapshot fresh = SelectionImageReader.RecaptureCurrent(captured.ImageMetadata))
                        {
                            if (fresh == null) return null;
                            string freshText, reason;
                            if (!LocalSelectionOcr.TryRead(fresh.Image, requestOptions.SourceLanguage, requestOptions.TargetLanguage, out freshText, out reason) ||
                                NormalizeOcrText(freshText) != NormalizeOcrText(text)) return null;
                            return fresh.ToSelectionSnapshot(snapshot.Text);
                        }
                    });
                    ObserveLateFailure(imageRecheckTask);
                    Task rechecked = await Task.WhenAny(imageRecheckTask, Task.Delay(4000, cancellation.Token));
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (ticket != revision) return;
                    if (rechecked != imageRecheckTask) throw new TimeoutException("选区复核较慢，请重新划选较短的文字。");
                    current = await imageRecheckTask;
                }
                else
                {
                    if (captureTask != null && !captureTask.IsCompleted) return;
                    captureTask = Task.Run(() => SelectionReader.RecaptureWithStatus(snapshot, point));
                    ObserveLateFailure(captureTask);
                    Task rechecked = await Task.WhenAny(captureTask, Task.Delay(2000, cancellation.Token));
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (ticket != revision) return;
                    if (rechecked != captureTask) throw new TimeoutException("选区复核较慢，请重新划选较短的文字。");
                    SelectionReadResult reading = await captureTask;
                    if (reading.SensitiveDenied) { popup.Hide(); ReportStage("protected_selection", "已跳过受保护的输入内容"); return; }
                    current = reading.Snapshot;
                }
                if (Native.GetForegroundWindow() != window || ticket != revision) return;
                if (current == null || current.Text != snapshot.Text)
                {
                    ReportStage("selection_changed", "选区无法再次确认，已放弃覆盖");
                    visibleUntil = DateTime.UtcNow.AddSeconds(5);
                    popup.Present("选区已经变化，或无法再次确认，已放弃这次覆盖。", snapshot.Bounds, point, false, false);
                    return;
                }
                snapshot = current;
                bool covered = requestOptions.CoverSelection && inline.Present(result, snapshot.Rectangles, requestOptions.TargetLanguage);
                visibleUntil = covered ? DateTime.MaxValue : DateTime.UtcNow.AddSeconds(25);
                if (!covered) popup.Present(result, snapshot.Bounds, point, false, false);
                else popup.Hide();
                ReportStage(covered ? "covered" : "popup", covered ? "译文已覆盖显示" : "完整译文已显示在浮窗");
                TestEvidence.RecordTranslation(window, covered ? inline.Bounds : popup.Bounds, text, result);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (ticket == revision && Native.GetForegroundWindow() == window)
                {
                    selectionWindow = window;
                    visibleUntil = DateTime.UtcNow.AddSeconds(8);
                    ReportStage("error", "本次识别或翻译未完成");
                    popup.Present(ex.Message, Rectangle.Empty, point, false, true);
                }
            }
            finally
            {
                if (pending == cancellation) pending = null;
                cancellation.Cancel();
                cancellation.Dispose();
            }
        }

        static void ObserveLateFailure(Task task)
        {
            task.ContinueWith(delegate(Task failed) { var observed = failed.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        internal static string NormalizeOcrText(string text)
        {
            return System.Text.RegularExpressions.Regex.Replace(text ?? String.Empty, @"\s+", " ").Trim();
        }

        void Dismiss()
        {
            revision++;
            if (pending != null) { try { pending.Cancel(); } catch (ObjectDisposedException) { } }
            popup.Hide();
            inline.Hide();
            selectionWindow = IntPtr.Zero;
        }
        void OpenFixture()
        {
            Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--fixture") { UseShellExecute = true });
        }
        void ShowSettings()
        {
            Dismiss();
            if (settings == null || settings.IsDisposed) settings = new SettingsWindow(options, ApplySettings, OpenFixture);
            settings.Show(); settings.Activate();
        }
        protected override void ExitThreadCore()
        {
            Dismiss();
            startup.Cancel();
            lifetime.Stop(); lifetime.Dispose();
            escape.Pressed -= OnEscape;
            escape.Dispose();
            mouse.BeforeProbe -= HideBeforeProbe;
            mouse.Dispose();
            tray.Visible = false; tray.Dispose();
            popup.Dispose(); inline.Dispose(); engine.Dispose(); dispatcher.Dispose(); startup.Dispose();
            if (settings != null) settings.Dispose();
            base.ExitThreadCore();
        }
    }

    sealed class CapturedSelection
    {
        public SelectionSnapshot Snapshot;
        public bool IsProtected;
        public bool FromImage;
        public string Fingerprint;
        public SelectionImageSnapshot ImageMetadata;
        public string Error;
    }

    static class LocalModelHost
    {
        public static async Task WarmUpAsync(CancellationToken token)
        {
            using (var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
            using (var client = new HttpClient(handler))
            {
                client.Timeout = TimeSpan.FromSeconds(90);
                var request = new {
                    model = TranslationEngine.DefaultOllamaModel,
                    messages = new[] { new { role = "user", content = "将以下文本翻译为中文，注意只需要输出翻译后的结果，不要额外解释：\nThe translation tool is ready." } },
                    stream = false, keep_alive = "30m",
                    options = new { num_ctx = 8192, num_predict = 64, temperature = 0.7, top_p = 0.6, top_k = 20, repeat_penalty = 1.05 }
                };
                using (var content = new StringContent(new JavaScriptSerializer().Serialize(request), System.Text.Encoding.UTF8, "application/json"))
                using (var response = await client.PostAsync(TranslationEngine.DefaultOllamaEndpoint + "/api/chat", content, token))
                {
                    if (!response.IsSuccessStatusCode) throw new InvalidOperationException("本地模型暂未就绪。");
                }
            }
        }

        public static async Task EnsureStartedAsync(CancellationToken token)
        {
            using (var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
            using (var client = new HttpClient(handler))
            {
                client.Timeout = TimeSpan.FromMilliseconds(800);
                if (await IsRunningAsync(client, token)) return;
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs", "Ollama", "ollama app.exe");
                if (!File.Exists(path)) throw new InvalidOperationException("请先安装本机 Ollama，再启动巴别塔。");
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
                for (int attempt = 0; attempt < 30; attempt++)
                {
                    await Task.Delay(250, token);
                    if (await IsRunningAsync(client, token)) return;
                }
                throw new InvalidOperationException("本地翻译服务尚未启动，请打开 Ollama 后重新启动巴别塔。");
            }
        }

        static async Task<bool> IsRunningAsync(HttpClient client, CancellationToken token)
        {
            try
            {
                using (var response = await client.GetAsync(TranslationEngine.DefaultOllamaEndpoint + "/api/version", token))
                    return response.IsSuccessStatusCode;
            }
            catch (HttpRequestException) { return false; }
            catch (OperationCanceledException) { token.ThrowIfCancellationRequested(); return false; }
        }
    }

    sealed class TranslationPopup : Form
    {
        readonly TextBox text = new TextBox();
        readonly Label footer = new Label();
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x80; return cp; }
        }
        public TranslationPopup()
        {
            Text = "巴别塔 · 自动译文";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(250, 252, 255);
            Padding = new Padding(15);
            text.Font = new Font("Microsoft YaHei UI", 12f);
            text.ForeColor = Color.FromArgb(28, 41, 59);
            text.BackColor = BackColor;
            text.BorderStyle = BorderStyle.None;
            text.ReadOnly = true; text.Multiline = true;
            text.WordWrap = true; text.ScrollBars = ScrollBars.Vertical;
            text.Location = new Point(16, 14);
            footer.Font = new Font("Microsoft YaHei UI", 8f);
            footer.ForeColor = Color.FromArgb(100, 116, 139);
            footer.Text = "巴别塔  ·  点击别处收起  ·  Esc 关闭";
            footer.AutoSize = true;
            Controls.Add(text); Controls.Add(footer);
        }
        public void Present(string content, Rectangle selection, Point pointer, bool cover, bool error)
        {
            text.Text = content;
            text.ForeColor = error ? Color.FromArgb(160, 44, 44) : Color.FromArgb(28, 41, 59);
            var area = Screen.FromPoint(pointer).WorkingArea;
            int width = Math.Min(440, Math.Max(240, area.Width - 40));
            if (cover && !selection.IsEmpty) width = Math.Min(520, Math.Max(240, selection.Width + 32));
            width = Math.Min(width, area.Width - 16);
            Size measured = TextRenderer.MeasureText(content, text.Font,
                new Size(width - 32, 700), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
            int height = Math.Min(Math.Min(420, area.Height - 16), Math.Max(76, measured.Height + 58));
            text.Size = new Size(width - 32, height - 52);
            footer.Location = new Point(16, height - 28);
            Size = new Size(width, height);
            int x = !selection.IsEmpty ? selection.Left : pointer.X;
            int y = !selection.IsEmpty ? (cover ? selection.Top : selection.Bottom + 8) : pointer.Y + 16;
            if (y + height > area.Bottom) y = Math.Max(area.Top + 8, (!selection.IsEmpty ? selection.Top : pointer.Y) - height - 8);
            Location = new Point(Math.Max(area.Left + 8, Math.Min(x, area.Right - width - 8)), Math.Max(area.Top + 8, y));
            Show();
            Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Color.FromArgb(176, 197, 228))) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x21) { m.Result = new IntPtr(3); return; } // MA_NOACTIVATE
            base.WndProc(ref m);
        }
    }

    sealed class SettingsWindow : Form
    {
        readonly ComboBox source = new ComboBox();
        readonly ComboBox target = new ComboBox();
        readonly RadioButton cover = new RadioButton();
        readonly RadioButton floating = new RadioButton();
        readonly CheckBox recognize = new CheckBox();
        readonly Label status = new Label();
        readonly Action<Options> apply;

        public SettingsWindow(Options current, Action<Options> apply, Action fixture)
        {
            this.apply = apply;
            Text = "巴别塔";
            ClientSize = new Size(420, 458); StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            BackColor = Color.White;
            var heading = new Label { Text = "巴别塔  Babel Tower", Font = new Font(Font.FontFamily, 17, FontStyle.Bold), Location = new Point(24, 18), AutoSize = true };
            var subtitle = new Label { Text = "划选文字，松开鼠标，译文出现。", Location = new Point(25, 53), AutoSize = true, ForeColor = Color.DimGray };
            var toChinese = new Button { Name = "EnglishToChinese", Text = "英文 → 简体中文", Location = new Point(24, 83), Size = new Size(180, 30) };
            toChinese.Click += delegate { SetDirection("en", "zh-CN"); };
            var toEnglish = new Button { Name = "ChineseToEnglish", Text = "简体中文 → 英文", Location = new Point(216, 83), Size = new Size(180, 30) };
            toEnglish.Click += delegate { SetDirection("zh-CN", "en"); };
            var sourceLabel = new Label { Text = "原文语言", Location = new Point(24, 128), AutoSize = true };
            var targetLabel = new Label { Text = "译文语言", Location = new Point(216, 128), AutoSize = true };
            source.Name = "SourceLanguage"; source.DropDownStyle = ComboBoxStyle.DropDownList;
            target.Name = "TargetLanguage"; target.DropDownStyle = ComboBoxStyle.DropDownList;
            source.Location = new Point(24, 150); source.Size = new Size(180, 28);
            target.Location = new Point(216, 150); target.Size = new Size(180, 28);
            foreach (var language in LanguageChoice.All)
            {
                source.Items.Add(language);
                if (language.Code != "auto") target.Items.Add(language);
            }
            var display = new GroupBox { Text = "译文显示", Location = new Point(24, 193), Size = new Size(372, 67) };
            cover.Name = "CoverSelection"; cover.Text = "覆盖选区"; cover.AutoSize = true; cover.Location = new Point(15, 29);
            floating.Name = "FloatingTranslation"; floating.Text = "显示在浮窗"; floating.AutoSize = true; floating.Location = new Point(204, 29);
            display.Controls.Add(cover); display.Controls.Add(floating);
            recognize.Name = "LocalRecognition"; recognize.Text = "读取不到文字时，本地识别蓝色选区";
            recognize.AutoSize = true; recognize.Location = new Point(25, 274);
            var detail = new Label { Text = "点击别处或按 Esc 收起译文。原文内容继续保留。\n识别与翻译都在本机完成，不使用剪贴板。\n覆盖空间不足时会用浮窗；部分界面仍不支持。", Location = new Point(25, 308), Size = new Size(372, 57), ForeColor = Color.DimGray };
            status.Name = "SaveStatus"; status.Text = "修改后点击保存，下一次划选立即生效。";
            status.Location = new Point(25, 370); status.Size = new Size(372, 22); status.ForeColor = Color.DimGray;
            var save = new Button { Name = "SaveSettings", Text = "保存并生效", Location = new Point(24, 402), Size = new Size(116, 31) };
            save.Click += delegate { TrySaveDraft(); };
            var demo = new Button { Text = "打开测试页", Location = new Point(152, 402), Size = new Size(116, 31) };
            demo.Click += delegate { fixture(); };
            var close = new Button { Text = "收起到托盘", Location = new Point(280, 402), Size = new Size(116, 31) };
            close.Click += delegate { Hide(); };
            var author = new LinkLabel { Name = "AuthorLink", Text = "Twitter  @HanJaKKK", Location = new Point(25, 437), AutoSize = true };
            author.Links.Add(9, 9, "https://twitter.com/HanJaKKK");
            // No navigation occurs until the user deliberately clicks this link.
            author.LinkClicked += delegate(object sender, LinkLabelLinkClickedEventArgs e)
            {
                try { Process.Start(new ProcessStartInfo((string)e.Link.LinkData) { UseShellExecute = true }); }
                catch { status.Text = "无法打开浏览器，请手动访问 Twitter @HanJaKKK。"; }
            };
            Controls.AddRange(new Control[] { heading, subtitle, toChinese, toEnglish, sourceLabel, targetLabel,
                source, target, display, recognize, detail, status, save, demo, close, author });
            LoadOptions(current);
            EventHandler edited = delegate
            {
                status.ForeColor = Color.DimGray;
                status.Text = "修改后点击保存，下一次划选立即生效。";
            };
            source.SelectedIndexChanged += edited; target.SelectedIndexChanged += edited;
            cover.CheckedChanged += edited; floating.CheckedChanged += edited; recognize.CheckedChanged += edited;
            AcceptButton = save;
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        }

        internal void SetDirection(string sourceCode, string targetCode)
        {
            SelectLanguage(source, sourceCode); SelectLanguage(target, targetCode);
        }
        static void SelectLanguage(ComboBox combo, string code)
        {
            foreach (LanguageChoice language in combo.Items)
                if (language.Code == code) { combo.SelectedItem = language; return; }
            combo.SelectedIndex = 0;
        }
        internal void LoadOptions(Options current)
        {
            SetDirection(current.SourceLanguage, current.TargetLanguage);
            cover.Checked = current.CoverSelection;
            floating.Checked = !current.CoverSelection;
            recognize.Checked = current.RecognizeBlueSelection;
        }
        internal Options ReadDraft()
        {
            var sourceLanguage = source.SelectedItem as LanguageChoice;
            var targetLanguage = target.SelectedItem as LanguageChoice;
            var draft = new Options { SourceLanguage = sourceLanguage == null ? null : sourceLanguage.Code,
                TargetLanguage = targetLanguage == null ? null : targetLanguage.Code,
                CoverSelection = cover.Checked, RecognizeBlueSelection = recognize.Checked };
            draft.Validate();
            return draft;
        }
        internal bool TrySaveDraft()
        {
            try
            {
                apply(ReadDraft());
                status.ForeColor = Color.FromArgb(26, 115, 80);
                status.Text = "已保存并生效，可以收起窗口后划选文字。";
                return true;
            }
            catch (Exception ex)
            {
                status.ForeColor = Color.FromArgb(160, 44, 44);
                status.Text = ex is InvalidOperationException ? ex.Message : "设置未保存，请确认程序所在文件夹可以写入。";
                return false;
            }
        }
    }

    sealed class Fixture : Form
    {
        public Fixture()
        {
            Text = "巴别塔测试页";
            Size = new Size(760, 420); StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 12);
            BackColor = Color.White;
            var title = new Label { Text = "选择对应的语言方向，再划选下面的英文或中文。", Location = new Point(24, 24), AutoSize = true };
            var reading = new RichTextBox { ReadOnly = true, Text = "This is a large job, so I will work in stages.\nThe original text stays unchanged.\n这项工作很大，所以我会分阶段完成。\n识别与翻译都在本机完成。", Location = new Point(25, 70), Size = new Size(690, 150), Font = new Font("Microsoft YaHei UI", 14), BorderStyle = BorderStyle.FixedSingle, BackColor = Color.FromArgb(247, 249, 252), DetectUrls = false };
            var editableTitle = new Label { Text = "输入框也可以测试：", Location = new Point(24, 242), AutoSize = true };
            var editable = new TextBox { Text = "Good morning. Have a wonderful day.", Location = new Point(25, 280), Size = new Size(690, 45), Font = new Font("Segoe UI", 16) };
            var note = new Label { Text = "这是一张测试页；巴别塔不修改原文、不改动剪贴板。", Location = new Point(24, 335), AutoSize = true, ForeColor = Color.DimGray };
            Controls.Add(title); Controls.Add(reading); Controls.Add(editableTitle); Controls.Add(editable); Controls.Add(note);
        }
    }

    static class RuntimeDiagnostic
    {
        // Explicit troubleshooting only. Overwrite a single stage snapshot;
        // never record selected text, pixels, application names or titles.
        public static void Write(string stage, int mouseDowns, int selections, bool ocrEnabled)
        {
            string path = Environment.GetEnvironmentVariable("SWIPE_TRANSLATE_DIAGNOSTIC_STATUS");
            if (String.IsNullOrEmpty(path)) return;
            try
            {
                File.WriteAllText(path, new JavaScriptSerializer().Serialize(new {
                    version = "20261001-global-fix", stage = stage, mouseDowns = mouseDowns,
                    selections = selections, ocrEnabled = ocrEnabled, utc = DateTime.UtcNow.ToString("o") }));
            }
            catch { }
        }
    }

    static class TestEvidence
    {
        // Explicit diagnostic mode only; only the bundled synthetic fixture may be recorded.
        public static void RecordTranslation(IntPtr window, Rectangle bounds, string original, string result)
        {
            string path = Environment.GetEnvironmentVariable("SWIPE_TRANSLATE_TEST_EVIDENCE");
            if (String.IsNullOrEmpty(path)) return;
            uint pid; Native.GetWindowThreadProcessId(window, out pid);
            try
            {
                using (var process = Process.GetProcessById((int)pid))
                {
                    if (process.MainWindowTitle != "巴别塔测试页") return;
                    string[] known = { "This is a large job, so I will work in stages.", "Good morning. Have a wonderful day.", "This is a large job", "Good morning.", "这项工作很大，所以我会分阶段完成。", "识别与翻译都在本机完成。" };
                    bool permitted = false;
                    foreach (string sample in known) if (original == sample) permitted = true;
                    if (!permitted) return;
                    File.WriteAllText(path, new JavaScriptSerializer().Serialize(new { source = original, translation = result, x = bounds.X, y = bounds.Y, width = bounds.Width, height = bounds.Height, utc = DateTime.UtcNow.ToString("o") }));
                }
            }
            catch { }
        }
    }

    sealed class MouseWatcher : IDisposable
    {
        Native.HookProc callback;
        IntPtr hook;
        Point down;
        IntPtr downWindow;
        bool pressed;
        int downTick;
        int lastUpTick;
        Point lastUp;
        SelectionGestureProbe probe;
        bool probeNeedsRefresh;
        int probeRefreshAttempts;
        DateTime lastHookRefresh = DateTime.UtcNow;
        public int DownCount { get; private set; }
        public int SelectionCount { get; private set; }
        public bool EnableImageProbe { get; set; }
        public event Action<Point> BeforeProbe;
        public event Action<bool, Point, Point, IntPtr, SelectionGestureProbe> Gesture;
        public MouseWatcher()
        {
            EnableImageProbe = true;
            callback = Handle;
            hook = Native.SetWindowsHookEx(14, callback, Native.GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        IntPtr Handle(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                try
                {
                    var input = (Native.MouseData)Marshal.PtrToStructure(lParam, typeof(Native.MouseData));
                    Point point = new Point(input.pt.X, input.pt.Y);
                    int message = wParam.ToInt32();
                    if (message == 0x201)
                    {
                        DownCount++;
                        down = point;
                        var before = BeforeProbe;
                        if (before != null) before(point);
                        downWindow = Native.GetAncestor(Native.WindowFromPoint(input.pt), 2);
                        if (downWindow == IntPtr.Zero) downWindow = Native.GetForegroundWindow();
                        probe = EnableImageProbe ? SelectionImageReader.CreateProbe(downWindow, down) : null;
                        int oldBlue = 0;
                        if (probe != null && probe.PreviouslyBlue != null)
                            foreach (bool blue in probe.PreviouslyBlue) if (blue) oldBlue++;
                        probeNeedsRefresh = oldBlue >= 24;
                        probeRefreshAttempts = 0;
                        pressed = true; downTick = Environment.TickCount;
                        Raise(false, point, Native.GetForegroundWindow());
                    }
                    else if (message == 0x200 && pressed && probeNeedsRefresh && probeRefreshAttempts < 3)
                    {
                        probeRefreshAttempts++;
                        SelectionGestureProbe refreshed = SelectionImageReader.CreateProbe(downWindow, down);
                        SelectionGestureProbe effective;
                        if (SelectionImageReader.TryRefreshProbeOnFirstMove(probe, refreshed, out effective))
                        {
                            probe = effective;
                            probeNeedsRefresh = false;
                        }
                    }
                    else if (message == 0x202 && pressed)
                    {
                        pressed = false;
                        bool dragged = Math.Abs(point.X - down.X) + Math.Abs(point.Y - down.Y) >= 5;
                        bool twice = unchecked(Environment.TickCount - lastUpTick) < SystemInformation.DoubleClickTime &&
                            Math.Abs(point.X - lastUp.X) + Math.Abs(point.Y - lastUp.Y) < 6;
                        lastUpTick = Environment.TickCount; lastUp = point;
                        // Windows can activate the clicked app after the down hook.
                        // At release, use the app that now owns the selection.
                        if ((dragged || twice) && unchecked(Environment.TickCount - downTick) < 30000)
                        {
                            SelectionCount++;
                            Raise(true, point, Native.GetForegroundWindow());
                        }
                    }
                    else if (message == 0x204 || message == 0x20A || message == 0x20E) Raise(false, point, Native.GetForegroundWindow());
                }
                catch { }
            }
            return Native.CallNextHookEx(hook, code, wParam, lParam);
        }
        void Raise(bool candidate, Point point, IntPtr window) { var handler = Gesture; if (handler != null) handler(candidate, down, point, window, candidate ? probe : null); }
        public void RefreshIfIdle()
        {
            bool buttonDown = (Native.GetAsyncKeyState(1) & 0x8000) != 0;
            if (pressed && !buttonDown && unchecked(Environment.TickCount - downTick) > 2000)
            {
                // A removed hook can miss release and leave the local flag set.
                pressed = false;
                probe = null;
                lastHookRefresh = DateTime.MinValue;
            }
            if (hook == IntPtr.Zero || pressed || buttonDown ||
                DateTime.UtcNow - lastHookRefresh < TimeSpan.FromSeconds(30)) return;
            // Windows may remove a slow low-level hook silently. Periodically
            // renew it while the mouse is idle; never interrupt a real drag.
            IntPtr renewed = Native.SetWindowsHookEx(14, callback, Native.GetModuleHandle(null), 0);
            lastHookRefresh = DateTime.UtcNow;
            if (renewed == IntPtr.Zero) return;
            IntPtr previous = hook;
            hook = renewed;
            Native.UnhookWindowsHookEx(previous);
        }
        public void Dispose() { if (hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(hook); hook = IntPtr.Zero; } }
    }

    sealed class EscapeWatcher : IDisposable
    {
        // Keep the managed delegate alive for the entire native hook lifetime.
        readonly Native.HookProc callback;
        IntPtr hook;
        public event Action Pressed;

        public EscapeWatcher()
        {
            callback = Handle;
            hook = Native.SetWindowsHookEx(13, callback, Native.GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        IntPtr Handle(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && lParam != IntPtr.Zero &&
                (wParam == new IntPtr(0x0100) || wParam == new IntPtr(0x0104)))
            {
                try
                {
                    // The first DWORD is vkCode. Do not retain or process other
                    // keys, characters, modifiers, or keyboard payload fields.
                    if (Marshal.ReadInt32(lParam) == 27)
                    {
                        var handler = Pressed;
                        if (handler != null) handler();
                    }
                }
                catch { }
            }
            // Observing Escape must not consume or alter the original input.
            return Native.CallNextHookEx(hook, code, wParam, lParam);
        }

        public void Dispose()
        {
            if (hook == IntPtr.Zero) return;
            IntPtr installed = hook;
            hook = IntPtr.Zero;
            Native.UnhookWindowsHookEx(installed);
            GC.KeepAlive(callback);
        }
    }

    static class Native
    {
        public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)] public struct NativePoint { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct MouseData { public NativePoint pt; public uint mouseData, flags, time; public UIntPtr extra; }
        [DllImport("user32.dll", EntryPoint = "SetProcessDPIAware")] public static extern bool SetProcessDpiAware();
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(NativePoint point);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int kind, HookProc callback, IntPtr module, uint thread);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto)] public static extern IntPtr GetModuleHandle(string name);
    }
}
