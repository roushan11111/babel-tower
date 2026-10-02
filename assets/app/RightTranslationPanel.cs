using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SwipeTranslate
{
    // Selected text and the user's draft have separate controls. A late model
    // response must never overwrite an edited draft or a newer translation.
    sealed class RightTranslationPanel : Form
    {
        readonly Func<string, string, string, CancellationToken, Task<string>> translate;
        readonly Func<Options> getOptions;
        readonly Action<Options> applyOptions;
        readonly FloatingTranslationButton launcher;
        bool launcherPositioned;
        public event Action SettingsRequested;
        internal FloatingTranslationButton Launcher { get { return launcher; } }
        readonly ComboBox source = new ComboBox();
        readonly ComboBox target = new ComboBox();
        readonly CheckBox pin = new CheckBox();
        readonly Label languageStatus = new Label();
        readonly TabControl tabs = new TabControl();
        readonly TabPage selectedTab = new TabPage("划选译文");
        readonly TabPage manualTab = new TabPage("输入翻译");
        readonly Label originalCaption = new Label();
        readonly Label translatedCaption = new Label();
        readonly TextBox selectedOriginal = new TextBox();
        readonly TextBox selectedTranslation = new TextBox();
        readonly TextBox draft = new TextBox();
        readonly TextBox manualTranslation = new TextBox();
        readonly Button submit = new Button();
        readonly Button cancel = new Button();
        readonly Label manualStatus = new Label();
        CancellationTokenSource pending;
        int revision;
        bool loadingLanguages;
        bool languageDraftChanged;
        bool disposingPanel;

        protected override bool ShowWithoutActivation { get { return true; } }

        public RightTranslationPanel(
            Func<string, string, string, CancellationToken, Task<string>> translate,
            Func<Options> getOptions, Action<Options> applyOptions)
        {
            if (translate == null) throw new ArgumentNullException("translate");
            if (getOptions == null) throw new ArgumentNullException("getOptions");
            if (applyOptions == null) throw new ArgumentNullException("applyOptions");
            this.translate = translate;
            this.getOptions = getOptions;
            this.applyOptions = applyOptions;
            Text = "巴别塔 · 悬浮翻译面板";
            Name = "RightTranslationPanel";
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(382, 590);
            MinimumSize = new Size(326, 498);
            BackColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            TopMost = true;
            launcher = new FloatingTranslationButton(delegate { TogglePanel(Screen.FromControl(launcher).WorkingArea); },
                delegate { var handler = SettingsRequested; if (handler != null) handler(); });
            launcher.Docked += delegate
            {
                if (Visible && !disposingPanel) PlaceByLauncher(launcher.Bounds, Screen.FromControl(launcher).WorkingArea);
            };

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14),
                ColumnCount = 1, RowCount = 6, BackColor = Color.White };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            var heading = new Label { Text = "巴别塔  Babel Tower", Dock = DockStyle.Fill,
                Font = new Font(Font.FontFamily, 14, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
            var collapse = new Button { Name = "CollapseTranslationPanel", Text = "收起", Dock = DockStyle.Fill,
                FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 5, 0, 7) };
            collapse.FlatAppearance.BorderColor = Color.FromArgb(225, 225, 231);
            collapse.Click += delegate { CollapsePanel(); };
            header.Controls.Add(heading, 0, 0); header.Controls.Add(collapse, 1, 0);
            var labels = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            labels.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            labels.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            labels.Controls.Add(new Label { Text = "原文语言", Dock = DockStyle.Fill }, 0, 0);
            labels.Controls.Add(new Label { Text = "译文语言", Dock = DockStyle.Fill }, 1, 0);
            var languages = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            languages.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            languages.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            source.Name = "PanelSourceLanguage"; target.Name = "PanelTargetLanguage";
            source.DropDownStyle = target.DropDownStyle = ComboBoxStyle.DropDownList;
            source.Dock = target.Dock = DockStyle.Fill;
            source.Margin = new Padding(0, 0, 6, 0); target.Margin = Padding.Empty;
            foreach (var language in LanguageChoice.All)
            {
                source.Items.Add(language);
                if (language.Code != "auto") target.Items.Add(language);
            }
            languages.Controls.Add(source, 0, 0); languages.Controls.Add(target, 1, 0);
            var tools = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = false };
            var save = new Button { Name = "SavePanelLanguages", Text = "保存语言", Size = new Size(94, 28), Margin = new Padding(0, 2, 7, 0) };
            save.Click += delegate { TrySaveLanguages(); };
            pin.Name = "PinRightPanel"; pin.Text = "置顶"; pin.Checked = true;
            pin.AutoSize = true; pin.Margin = new Padding(0, 7, 8, 0);
            pin.CheckedChanged += delegate { TopMost = pin.Checked; };
            var align = new Button { Name = "AlignRightPanel", Text = "靠右", Size = new Size(62, 28), Margin = new Padding(0, 2, 0, 0) };
            align.Click += delegate { PlaceByLauncher(launcher.Bounds, Screen.FromControl(launcher).WorkingArea); };
            tools.Controls.Add(save); tools.Controls.Add(pin); tools.Controls.Add(align);

            tabs.Name = "PanelTranslationTabs"; tabs.Dock = DockStyle.Fill; tabs.Margin = new Padding(0, 4, 0, 0);
            selectedTab.Name = "SelectionTranslationTab"; selectedTab.Padding = new Padding(9);
            manualTab.Name = "ManualTranslationTab"; manualTab.Padding = new Padding(9);
            tabs.TabPages.Add(selectedTab); tabs.TabPages.Add(manualTab);
            tabs.SelectedIndexChanged += delegate { if (tabs.SelectedTab == selectedTab) selectedTab.Text = "划选译文"; };
            selectedTab.Enter += delegate { selectedTab.Text = "划选译文"; };
            BuildSelectionTab(); BuildManualTab();

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            languageStatus.Name = "PanelLanguageStatus"; languageStatus.Text = "本地 Hy-MT2";
            languageStatus.ForeColor = Color.DimGray; languageStatus.Dock = DockStyle.Fill;
            languageStatus.TextAlign = ContentAlignment.MiddleLeft;
            var author = new LinkLabel { Name = "PanelAuthorLink", Text = "X  @HanJaKKK", Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft };
            author.LinkClicked += delegate
            {
                try { Process.Start(new ProcessStartInfo("https://x.com/HanJaKKK") { UseShellExecute = true }); }
                catch { languageStatus.Text = "请手动访问 @HanJaKKK。"; }
            };
            var douyin = new Label { Name = "PanelDouyinAuthor", Text = "抖音  YZRJ88", Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(75, 89, 105), TextAlign = ContentAlignment.MiddleRight };
            footer.Controls.Add(languageStatus, 0, 0); footer.SetColumnSpan(languageStatus, 2);
            footer.Controls.Add(author, 0, 1); footer.Controls.Add(douyin, 1, 1);
            layout.Controls.Add(header, 0, 0); layout.Controls.Add(labels, 0, 1);
            layout.Controls.Add(languages, 0, 2); layout.Controls.Add(tools, 0, 3);
            layout.Controls.Add(tabs, 0, 4); layout.Controls.Add(footer, 0, 5);
            Controls.Add(layout);
            LoadLanguages(getOptions());
            EventHandler languageEdited = delegate
            {
                if (loadingLanguages) return;
                languageDraftChanged = true;
                CancelManualTranslation();
                manualTranslation.Clear();
                manualStatus.Text = "方向已修改，点击翻译使用当前选择。";
                languageStatus.ForeColor = Color.DimGray;
                languageStatus.Text = "保存后用于划选翻译";
            };
            source.SelectedIndexChanged += languageEdited;
            target.SelectedIndexChanged += languageEdited;
        }

        void BuildSelectionTab()
        {
            var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty };
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 62));
            originalCaption.Text = "原文"; originalCaption.Dock = DockStyle.Fill;
            translatedCaption.Text = "译文"; translatedCaption.Dock = DockStyle.Fill;
            translatedCaption.TextAlign = ContentAlignment.MiddleLeft;
            ConfigureTextBox(selectedOriginal, "PanelSelectedOriginal", true);
            ConfigureTextBox(selectedTranslation, "PanelSelectedTranslation", true);
            selectedTranslation.Font = new Font(Font.FontFamily, 11);
            content.Controls.Add(originalCaption, 0, 0); content.Controls.Add(selectedOriginal, 0, 1);
            content.Controls.Add(translatedCaption, 0, 2); content.Controls.Add(selectedTranslation, 0, 3);
            selectedTab.Controls.Add(content);
        }

        void BuildManualTab()
        {
            var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Margin = Padding.Empty };
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 44));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 56));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            ConfigureTextBox(draft, "PanelManualDraft", false); draft.MaxLength = 4000;
            ConfigureTextBox(manualTranslation, "PanelManualTranslation", true);
            manualTranslation.Font = new Font(Font.FontFamily, 11);
            draft.TextChanged += delegate
            {
                CancelManualTranslation();
                manualTranslation.Clear();
                manualStatus.Text = String.IsNullOrWhiteSpace(draft.Text) ? "写下句子，再点击翻译。" : "点击翻译，或按 Ctrl + Enter。";
                manualStatus.ForeColor = Color.DimGray;
            };
            draft.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Control && e.KeyCode == Keys.Enter)
                {
                    e.Handled = true; e.SuppressKeyPress = true;
                    StartManualTranslation();
                }
            };
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            submit.Name = "TranslatePanelDraft"; submit.Text = "翻译"; submit.Size = new Size(96, 30);
            submit.Margin = new Padding(0, 5, 9, 0);
            submit.Click += delegate { StartManualTranslation(); };
            cancel.Name = "CancelPanelTranslation"; cancel.Text = "取消"; cancel.Size = new Size(70, 30);
            cancel.Margin = new Padding(0, 5, 0, 0); cancel.Enabled = false;
            cancel.Click += delegate { CancelManualTranslation(); manualStatus.Text = "已取消，输入内容仍保留。"; };
            actions.Controls.Add(submit); actions.Controls.Add(cancel);
            manualStatus.Name = "PanelManualStatus"; manualStatus.Text = "写下句子，再点击翻译。";
            manualStatus.ForeColor = Color.DimGray; manualStatus.Dock = DockStyle.Fill;
            content.Controls.Add(new Label { Text = "原文输入", Dock = DockStyle.Fill }, 0, 0);
            content.Controls.Add(draft, 0, 1); content.Controls.Add(actions, 0, 2);
            content.Controls.Add(new Label { Text = "译文", Dock = DockStyle.Fill }, 0, 3);
            content.Controls.Add(manualTranslation, 0, 4); content.Controls.Add(manualStatus, 0, 5);
            manualTab.Controls.Add(content);
        }

        static void ConfigureTextBox(TextBox box, string name, bool readOnly)
        {
            box.Name = name; box.Multiline = true; box.ReadOnly = readOnly;
            box.ScrollBars = ScrollBars.Vertical; box.WordWrap = true;
            box.Dock = DockStyle.Fill; box.BorderStyle = BorderStyle.FixedSingle;
            box.Margin = Padding.Empty; box.BackColor = readOnly ? Color.FromArgb(248, 250, 253) : Color.White;
        }

        static void SelectLanguage(ComboBox combo, string code)
        {
            foreach (LanguageChoice language in combo.Items)
                if (language.Code == code) { combo.SelectedItem = language; return; }
            combo.SelectedIndex = 0;
        }

        static string LanguageName(string code)
        {
            foreach (var language in LanguageChoice.All) if (language.Code == code) return language.Name;
            return "未指定";
        }

        void LoadLanguages(Options current)
        {
            var previousSource = source.SelectedItem as LanguageChoice;
            var previousTarget = target.SelectedItem as LanguageChoice;
            if (previousSource != null && previousTarget != null &&
                (previousSource.Code != current.SourceLanguage || previousTarget.Code != current.TargetLanguage))
            {
                CancelManualTranslation();
                manualTranslation.Clear();
                manualStatus.Text = "翻译方向已更新，请点击翻译。";
            }
            loadingLanguages = true;
            try { SelectLanguage(source, current.SourceLanguage); SelectLanguage(target, current.TargetLanguage); }
            finally { loadingLanguages = false; }
            languageDraftChanged = false;
        }

        public void RefreshOptions()
        {
            if (!IsDisposed && !disposingPanel && !languageDraftChanged) LoadLanguages(getOptions());
        }

        internal Options ReadLanguageDraft()
        {
            var current = getOptions().Copy();
            var sourceLanguage = source.SelectedItem as LanguageChoice;
            var targetLanguage = target.SelectedItem as LanguageChoice;
            current.SourceLanguage = sourceLanguage == null ? null : sourceLanguage.Code;
            current.TargetLanguage = targetLanguage == null ? null : targetLanguage.Code;
            current.Validate();
            return current;
        }

        internal bool TrySaveLanguages()
        {
            try
            {
                applyOptions(ReadLanguageDraft());
                languageDraftChanged = false;
                languageStatus.ForeColor = Color.FromArgb(26, 115, 80);
                languageStatus.Text = "语言已保存并生效";
                return true;
            }
            catch (Exception ex)
            {
                languageStatus.ForeColor = Color.FromArgb(160, 44, 44);
                languageStatus.Text = ex is InvalidOperationException ? ex.Message : "语言未保存，请稍后重试。";
                return false;
            }
        }

        public void PresentResult(string original, string translated, string sourceLanguage, string targetLanguage)
        {
            if (IsDisposed || disposingPanel) return;
            if (InvokeRequired)
            {
                BeginInvoke((Action)delegate { PresentResult(original, translated, sourceLanguage, targetLanguage); });
                return;
            }
            UpdateSelectionResult(original, translated, sourceLanguage, targetLanguage);
            // Retain results quietly: receiving a selection must not expand
            // the panel or steal focus from the source application.
            launcher.SetUnread(!Visible);
        }

        internal void UpdateSelectionResult(string original, string translated, string sourceLanguage, string targetLanguage)
        {
            selectedOriginal.Text = original ?? String.Empty;
            selectedTranslation.Text = translated ?? String.Empty;
            originalCaption.Text = "原文 · " + LanguageName(sourceLanguage);
            translatedCaption.Text = "译文 · " + LanguageName(targetLanguage);
            if (tabs.SelectedTab != selectedTab) selectedTab.Text = "划选译文 · 新";
        }

        public void ShowAtRight()
        {
            if (IsDisposed || disposingPanel) return;
            ShowLauncherAtRight();
            if (!Visible) TogglePanel(Screen.FromControl(launcher).WorkingArea);
            else { BringToFront(); Activate(); }
        }

        public void ShowLauncherAtRight()
        {
            SyncLauncher(true);
        }

        public void SyncLauncher(bool enabled, Rectangle? workingArea = null)
        {
            if (IsDisposed || disposingPanel) return;
            if (!enabled) { CollapsePanel(); launcher.Hide(); return; }
            Rectangle area = workingArea ?? (launcherPositioned ? Screen.FromRectangle(launcher.Bounds).WorkingArea : Screen.FromPoint(Cursor.Position).WorkingArea);
            launcher.PlaceAtRight(area, launcherPositioned ? launcher.Top : area.Top + (area.Height - launcher.Height) / 2);
            launcherPositioned = true;
            if (!launcher.Visible) launcher.Show();
        }

        public void SetLauncherStatus(bool enabled, bool ready, string label)
        {
            if (!IsDisposed && !disposingPanel) launcher.SetStatus(enabled, ready, label);
        }

        internal void TogglePanel(Rectangle workingArea, bool activate = true)
        {
            if (IsDisposed || disposingPanel) return;
            if (Visible) { CollapsePanel(); return; }
            RefreshOptions();
            PlaceByLauncher(launcher.Bounds, workingArea);
            launcher.SetUnread(false);
            Show();
            BringToFront();
            if (activate) Activate();
        }

        internal void CollapsePanel()
        {
            CancelManualTranslation();
            Hide();
        }

        internal void PlaceByLauncher(Rectangle anchor, Rectangle workingArea)
        {
            PlaceAtRight(workingArea);
            int left = Math.Max(workingArea.Left, anchor.Left - Width - 10);
            left = Math.Min(left, workingArea.Right - Width);
            int top = Math.Max(workingArea.Top, Math.Min(anchor.Top - 12, workingArea.Bottom - Height));
            Location = new Point(left, top);
        }

        internal void PlaceAtRight(Rectangle workingArea)
        {
            // A resized panel stays inside its monitor, including monitors with
            // a negative origin and a taskbar on a different edge.
            int availableWidth = Math.Max(1, workingArea.Width - 16);
            int availableHeight = Math.Max(1, workingArea.Height - 16);
            MinimumSize = new Size(Math.Min(326, availableWidth), Math.Min(498, availableHeight));
            Size = new Size(Math.Min(Width, availableWidth), Math.Min(Height, availableHeight));
            Location = new Point(workingArea.Right - Width - Math.Min(8, Math.Max(0, workingArea.Width - Width)),
                workingArea.Top + Math.Min(8, Math.Max(0, workingArea.Height - Height)));
        }

        async void StartManualTranslation()
        {
            await TranslateManualAsync();
        }

        internal async Task TranslateManualAsync()
        {
            CancelManualTranslation();
            if (IsDisposed || disposingPanel) return;
            string original = draft.Text;
            if (String.IsNullOrWhiteSpace(original))
            {
                manualStatus.Text = "请先写下要翻译的句子。";
                return;
            }
            Options requestOptions;
            try { requestOptions = ReadLanguageDraft(); }
            catch (InvalidOperationException ex) { manualStatus.Text = ex.Message; return; }
            var cancellation = new CancellationTokenSource();
            pending = cancellation;
            int requestRevision = revision;
            manualTranslation.Clear();
            submit.Enabled = false; submit.Text = "翻译中…"; cancel.Enabled = true;
            manualStatus.ForeColor = Color.DimGray;
            manualStatus.Text = "正在本地翻译，输入内容继续保留。";
            try
            {
                string result = await translate(original, requestOptions.SourceLanguage, requestOptions.TargetLanguage, cancellation.Token);
                if (!IsCurrent(requestRevision, cancellation)) return;
                manualTranslation.Text = result ?? String.Empty;
                manualStatus.ForeColor = Color.FromArgb(26, 115, 80);
                manualStatus.Text = "翻译完成，可以选中译文使用。";
            }
            catch (OperationCanceledException)
            {
                if (IsCurrent(requestRevision, cancellation)) manualStatus.Text = "已取消，输入内容仍保留。";
            }
            catch (Exception)
            {
                if (IsCurrent(requestRevision, cancellation))
                {
                    manualStatus.ForeColor = Color.FromArgb(160, 44, 44);
                    manualStatus.Text = "翻译未完成，请确认本地模型已就绪后重试。";
                }
            }
            finally
            {
                if (!IsDisposed && !disposingPanel && ReferenceEquals(pending, cancellation))
                {
                    pending = null;
                    submit.Enabled = true; submit.Text = "翻译"; cancel.Enabled = false;
                }
                cancellation.Dispose();
            }
        }

        bool IsCurrent(int requestRevision, CancellationTokenSource cancellation)
        {
            return !IsDisposed && !disposingPanel && requestRevision == revision &&
                ReferenceEquals(pending, cancellation) && !cancellation.IsCancellationRequested;
        }

        internal void CancelManualTranslation()
        {
            revision++;
            var previous = pending;
            pending = null;
            if (previous != null) previous.Cancel();
            if (!IsDisposed && !disposingPanel)
            {
                submit.Enabled = true; submit.Text = "翻译"; cancel.Enabled = false;
                if (previous != null) manualStatus.Text = "已取消，输入内容仍保留。";
            }
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            if (Visible && launcher != null) launcher.SetUnread(false);
            if (!Visible && pending != null) CancelManualTranslation();
            base.OnVisibleChanged(e);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { CollapsePanel(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && !disposingPanel)
            {
                e.Cancel = true;
                CancelManualTranslation();
                Hide();
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposingPanel)
            {
                disposingPanel = true;
                CancelManualTranslation();
                launcher.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
