using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace StreamLauncher
{
    /// <summary>
    /// 起動リストのアプリを上から順に起動し、必要ならボタンを UI Automation で押す。
    /// 「全部終了」で逆順に閉じる。
    /// </summary>
    internal sealed class MainForm : Form
    {
        private readonly string _startupCommand;
        private readonly bool _justUpdated;
        private Updater.ReleaseInfo _pendingUpdate;
        private bool _updating;
        private CancellationTokenSource _cts;
        private bool _closing;
        private bool _loadingEditor;
        private bool _suppressCheck;
        private AppEntry _current;

        private CheckedListBox _list;
        private Button _btnAdd, _btnRemove, _btnUp, _btnDown;
        private ContextMenuStrip _addMenu;

        private GroupBox _gEdit;
        private TextBox _tbName, _tbPath, _tbExe, _tbArgs, _tbClicks, _tbRelated;
        private Label _lbDetected;
        private NumericUpDown _numWait, _numTimeout;
        private CheckBox _cbMouse, _cbCloseOnExit, _cbConfirmKill;
        private Button _btnBrowseExe, _btnBrowseFolder, _btnRecord;

        private CheckBox _cbSkip, _cbAutoRun, _cbConfirmClose, _cbExitAfterClose;
        private Button _btnSave, _btnTree, _btnStartMenu, _btnUpdate, _btnCloseAll, _btnCancel, _btnRun, _btnMonitor;
        private MonitorForm _monitor;
        private TextBox _log;

        public MainForm(string startupCommand, bool justUpdated)
        {
            _startupCommand = startupCommand;
            _justUpdated = justUpdated;
            BuildUi();
            var s = Settings.LoadOrDefault();
            _cbSkip.Checked = s.SkipIfRunning;
            _cbAutoRun.Checked = s.AutoRunOnOpen;
            _cbConfirmClose.Checked = s.ConfirmCloseAll;
            _cbExitAfterClose.Checked = s.ExitAfterCloseAll;
            Shortcuts.RefreshIfMoved(Application.ExecutablePath);
            UpdateStartMenuButton();
            foreach (var a in s.Apps) _list.Items.Add(a, a.Enabled);
            if (_list.Items.Count > 0) _list.SelectedIndex = 0;
            else LoadEditor(null);
        }

        // ------------------------------------------------------------ 画面

        private static T Add<T>(Control parent, int x, int y, int w, int h, string text = null) where T : Control, new()
        {
            var c = new T { Location = new Point(x, y), Size = new Size(w, h) };
            if (text != null) c.Text = text;
            parent.Controls.Add(c);
            return c;
        }

        private static Label Hint(Control parent, int x, int y, int w, int h, string text)
        {
            var l = Add<Label>(parent, x, y, w, h, text);
            l.ForeColor = SystemColors.GrayText;
            return l;
        }

        private void BuildUi()
        {
            SuspendLayout();
            Text = $"配信スタートランチャー v{Updater.CurrentVersionText}";
            Font = new Font("Yu Gothic UI", 9F);
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(760, 650);
            MinimumSize = new Size(776, 560);
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // --- 起動リスト
            Add<Label>(this, 10, 8, 200, 18, "起動リスト(上から順に起動)");
            _list = Add<CheckedListBox>(this, 10, 28, 200, 286);
            _list.IntegralHeight = false;
            _list.CheckOnClick = false;
            _btnAdd = Add<Button>(this, 10, 320, 98, 28, "追加 ▼");
            _btnRemove = Add<Button>(this, 112, 320, 98, 28, "削除");
            _btnUp = Add<Button>(this, 10, 352, 98, 28, "↑ 上へ");
            _btnDown = Add<Button>(this, 112, 352, 98, 28, "↓ 下へ");

            _addMenu = new ContextMenuStrip();
            _addMenu.Items.Add("アプリを選んで追加…", null, (s, e) => AddFromExe());
            _addMenu.Items.Add(new ToolStripSeparator());
            _addMenu.Items.Add("OBS", null, (s, e) => AddApp(Presets.Obs()));
            _addMenu.Items.Add("CastCraft(Screen も起動)", null, (s, e) => AddApp(Presets.CastCraft()));
            _addMenu.Items.Add("VSeeFace", null, (s, e) => AddApp(Presets.VSeeFace()));
            _addMenu.Items.Add("VRCast", null, (s, e) => AddApp(Presets.VRCast()));
            _addMenu.Items.Add("CommentDeck(コメント表示)", null, (s, e) => AddApp(Presets.CommentDeck(), beforeObs: true));
            _addMenu.Items.Add("LiveAnnouncer(配信開始の告知)", null, (s, e) => AddApp(Presets.LiveAnnouncer()));

            // --- 選択中アプリの設定
            _gEdit = Add<GroupBox>(this, 220, 4, 530, 376, "アプリの設定");
            Add<Label>(_gEdit, 12, 24, 115, 20, "名前");
            _tbName = Add<TextBox>(_gEdit, 130, 21, 240, 23);

            Add<Label>(_gEdit, 12, 56, 115, 20, "場所");
            _tbPath = Add<TextBox>(_gEdit, 130, 53, 236, 23);
            _btnBrowseExe = Add<Button>(_gEdit, 372, 52, 70, 25, "exe…");
            _btnBrowseFolder = Add<Button>(_gEdit, 446, 52, 72, 25, "フォルダ…");
            _lbDetected = Add<Label>(_gEdit, 130, 79, 390, 18, "");

            Add<Label>(_gEdit, 12, 104, 115, 20, "exe ファイル名");
            _tbExe = Add<TextBox>(_gEdit, 130, 101, 150, 23);
            Hint(_gEdit, 286, 104, 234, 20, "(フォルダ指定時にこの名前を探します)");

            Add<Label>(_gEdit, 12, 136, 115, 20, "起動引数");
            _tbArgs = Add<TextBox>(_gEdit, 130, 133, 388, 23);

            Add<Label>(_gEdit, 12, 168, 115, 20, "起動後の待機(秒)");
            _numWait = Add<NumericUpDown>(_gEdit, 130, 165, 70, 23);
            _numWait.Maximum = 600;
            Hint(_gEdit, 206, 168, 314, 20, "(次のアプリを起動するまで待つ時間)");

            Add<Label>(_gEdit, 12, 200, 115, 20, "起動後に押すボタン");
            _tbClicks = Add<TextBox>(_gEdit, 130, 197, 180, 66);
            _tbClicks.Multiline = true;
            _tbClicks.ScrollBars = ScrollBars.Vertical;
            _tbClicks.AcceptsReturn = true;
            Hint(_gEdit, 318, 199, 202, 36, "1行に1つ、上から順に押します。\r\n名前で押せないボタンは位置で記録:");
            _btnRecord = Add<Button>(_gEdit, 318, 237, 130, 26, "位置を記録…");

            Add<Label>(_gEdit, 12, 275, 115, 20, "ボタン検索の上限(秒)");
            _numTimeout = Add<NumericUpDown>(_gEdit, 130, 272, 70, 23);
            _numTimeout.Minimum = 5;
            _numTimeout.Maximum = 600;

            Add<Label>(_gEdit, 12, 307, 115, 20, "関連プロセス名");
            _tbRelated = Add<TextBox>(_gEdit, 130, 304, 150, 23);
            Hint(_gEdit, 286, 307, 234, 20, "(ボタンで起動するアプリ。確認・終了用)");

            _cbMouse = Add<CheckBox>(_gEdit, 14, 340, 160, 22, "マウスでクリックする");
            _cbCloseOnExit = Add<CheckBox>(_gEdit, 180, 340, 150, 22, "全部終了で閉じる");
            _cbConfirmKill = Add<CheckBox>(_gEdit, 336, 340, 186, 22, "強制終了の前に確認する");

            // --- 全体
            _cbSkip = Add<CheckBox>(this, 12, 388, 260, 22, "起動済みのアプリはスキップする");
            _cbAutoRun = Add<CheckBox>(this, 280, 388, 270, 22, "開いたらすぐに起動を始める");
            _cbConfirmClose = Add<CheckBox>(this, 556, 388, 194, 22, "全部終了の前に確認する");
            _cbExitAfterClose = Add<CheckBox>(this, 12, 412, 400, 22, "全部終了したらランチャーも閉じる");
            _btnMonitor = Add<Button>(this, 636, 406, 114, 30, "配信モニター");

            _btnSave = Add<Button>(this, 10, 440, 80, 34, "設定を保存");
            _btnTree = Add<Button>(this, 94, 440, 90, 34, "UI要素一覧");
            _btnStartMenu = Add<Button>(this, 188, 440, 150, 34, "スタートメニューに登録");
            _btnUpdate = Add<Button>(this, 342, 440, 100, 34, "更新を確認");
            _btnCloseAll = Add<Button>(this, 446, 440, 90, 34, "全部終了");
            _btnCloseAll.ForeColor = Color.Firebrick;
            _btnCancel = Add<Button>(this, 540, 440, 90, 34, "中止");
            _btnRun = Add<Button>(this, 636, 440, 114, 34, "起動");
            _btnRun.Font = new Font("Yu Gothic UI", 10F, FontStyle.Bold);
            _btnCancel.Enabled = false;

            _log = Add<TextBox>(this, 10, 484, 740, 156);
            _log.Multiline = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.ReadOnly = true;
            _log.BackColor = SystemColors.Window;
            _log.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            // --- イベント
            _list.SelectedIndexChanged += (s, e) => LoadEditor(_list.SelectedItem as AppEntry);
            _list.ItemCheck += (s, e) =>
            {
                if (!_suppressCheck) ((AppEntry)_list.Items[e.Index]).Enabled = e.NewValue == CheckState.Checked;
            };
            _btnAdd.Click += (s, e) => _addMenu.Show(_btnAdd, new Point(0, _btnAdd.Height));
            _btnRemove.Click += (s, e) => RemoveSelected();
            _btnUp.Click += (s, e) => MoveSelected(-1);
            _btnDown.Click += (s, e) => MoveSelected(+1);

            _tbName.TextChanged += (s, e) => Edit(a => a.Name = _tbName.Text, refreshItem: true);
            _tbPath.TextChanged += (s, e) => Edit(a => a.AppPath = _tbPath.Text.Trim());
            _tbExe.TextChanged += (s, e) => Edit(a => a.ExeName = _tbExe.Text.Trim());
            _tbPath.Leave += (s, e) => UpdateDetected();
            _tbExe.Leave += (s, e) => UpdateDetected();
            _tbArgs.TextChanged += (s, e) => Edit(a => a.Args = _tbArgs.Text.Trim());
            _numWait.ValueChanged += (s, e) => Edit(a => a.WaitAfterSec = (int)_numWait.Value);
            _tbClicks.TextChanged += (s, e) => Edit(a => a.ClickButtons = SplitLines(_tbClicks.Text));
            _numTimeout.ValueChanged += (s, e) => Edit(a => a.ClickTimeoutSec = (int)_numTimeout.Value);
            _tbRelated.TextChanged += (s, e) => Edit(a => a.RelatedProcess = _tbRelated.Text.Trim());
            _cbMouse.CheckedChanged += (s, e) => Edit(a => a.UseMouseClick = _cbMouse.Checked);
            _cbCloseOnExit.CheckedChanged += (s, e) => Edit(a => a.CloseOnExit = _cbCloseOnExit.Checked);
            _cbConfirmKill.CheckedChanged += (s, e) => Edit(a => a.ConfirmBeforeKill = _cbConfirmKill.Checked);
            _btnBrowseExe.Click += (s, e) => BrowseExe();
            _btnBrowseFolder.Click += (s, e) => BrowseFolder();
            _btnRecord.Click += async (s, e) => await RecordPositionAsync();

            _btnSave.Click += (s, e) => { if (TrySave(CollectSettings())) Log("設定を保存しました"); };
            _btnTree.Click += async (s, e) => await ShowUiTreeAsync();
            _btnCloseAll.Click += async (s, e) => await CloseAllAsync();
            _btnCancel.Click += (s, e) => _cts?.Cancel();
            _btnRun.Click += async (s, e) => await RunAsync();
            _btnUpdate.Click += async (s, e) => await UpdateAsync();
            _btnStartMenu.Click += (s, e) => ToggleStartMenu();
            _btnMonitor.Click += (s, e) => OpenMonitor();
            FormClosing += (s, e) =>
            {
                _cts?.Cancel();
                if (_monitor != null && !_monitor.IsDisposed) _monitor.Close();
            };
            Shown += async (s, e) =>
            {
                // Stream Deck などから2つ目が起動されたときの命令を受け取る
                SingleInstance.Listen(cmd =>
                {
                    try { BeginInvoke(new Action(async () => await HandleCommandAsync(cmd, fromOtherInstance: true))); }
                    catch { }
                });
                if (_justUpdated) Log($"v{Updater.CurrentVersionText} に更新しました");
                _ = AutoCheckUpdateAsync();
                // 更新直後の再起動では自動起動しない(アプリは起動済みのはず)
                if (!_justUpdated) await HandleCommandAsync(_startupCommand, fromOtherInstance: false);
            };

            ResumeLayout(false);
            PerformLayout();
        }

        private static string[] SplitLines(string text) =>
            text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();

        // ------------------------------------------------------------ リスト操作

        /// <param name="beforeObs">OBS より前に入れる(OBS が読み込むものを先に起動するため)。</param>
        private void AddApp(AppEntry a, bool beforeObs = false)
        {
            _suppressCheck = true;
            int obs = -1;
            if (beforeObs)
                for (int k = 0; k < _list.Items.Count && obs < 0; k++)
                    if (_list.Items[k] is AppEntry e && string.Equals(e.ProcessName, "obs64", StringComparison.OrdinalIgnoreCase)) obs = k;
            int i;
            if (obs >= 0) { _list.Items.Insert(obs, a); _list.SetItemChecked(obs, a.Enabled); i = obs; }
            else i = _list.Items.Add(a, a.Enabled);
            _suppressCheck = false;
            _list.SelectedIndex = i;
            if (a.ResolveExe() == null) Log($"{a} の場所が見つかりませんでした。「exe…」か「フォルダ…」で指定してください");
        }

        private void AddFromExe()
        {
            using (var d = new OpenFileDialog { Filter = "アプリ (*.exe)|*.exe", Title = "追加するアプリを選択" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                var a = new AppEntry
                {
                    Name = ProductNameOf(d.FileName),
                    AppPath = d.FileName,
                    ExeName = Path.GetFileName(d.FileName),
                };
                AddApp(a);
            }
        }

        private static string ProductNameOf(string exe)
        {
            try
            {
                var n = FileVersionInfo.GetVersionInfo(exe).ProductName;
                if (!string.IsNullOrWhiteSpace(n)) return n.Trim();
            }
            catch { }
            return Path.GetFileNameWithoutExtension(exe);
        }

        private void RemoveSelected()
        {
            int i = _list.SelectedIndex;
            if (i < 0) return;
            if (MessageBox.Show(this, $"「{_list.Items[i]}」をリストから削除しますか?", "削除",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _list.Items.RemoveAt(i);
            if (_list.Items.Count > 0) _list.SelectedIndex = Math.Min(i, _list.Items.Count - 1);
            else LoadEditor(null);
        }

        private void MoveSelected(int delta)
        {
            int i = _list.SelectedIndex, j = i + delta;
            if (i < 0 || j < 0 || j >= _list.Items.Count) return;
            var item = _list.Items[i];
            bool chk = _list.GetItemChecked(i);
            _suppressCheck = true;
            _list.Items.RemoveAt(i);
            _list.Items.Insert(j, item);
            _list.SetItemChecked(j, chk);
            _suppressCheck = false;
            _list.SelectedIndex = j;
        }

        private void RefreshCurrentItem()
        {
            int i = _list.SelectedIndex;
            if (i < 0) return;
            bool chk = _list.GetItemChecked(i);
            _suppressCheck = true;
            _loadingEditor = true;
            _list.Items[i] = _list.Items[i]; // 表示名を更新
            _list.SetItemChecked(i, chk);
            _list.SelectedIndex = i;
            _loadingEditor = false;
            _suppressCheck = false;
        }

        // ------------------------------------------------------------ 編集欄

        private void LoadEditor(AppEntry a)
        {
            if (_loadingEditor) return;
            _current = a;
            _loadingEditor = true;
            _gEdit.Enabled = a != null;
            _tbName.Text = a?.Name ?? "";
            _tbPath.Text = a?.AppPath ?? "";
            _tbExe.Text = a?.ExeName ?? "";
            _tbArgs.Text = a?.Args ?? "";
            _numWait.Value = a == null ? 0 : Math.Min(Math.Max(a.WaitAfterSec, 0), 600);
            _tbClicks.Text = a == null ? "" : string.Join("\r\n", a.ClickButtons ?? new string[0]);
            _numTimeout.Value = a == null ? 60 : Math.Min(Math.Max(a.ClickTimeoutSec, 5), 600);
            _tbRelated.Text = a?.RelatedProcess ?? "";
            _cbMouse.Checked = a?.UseMouseClick ?? false;
            _cbCloseOnExit.Checked = a?.CloseOnExit ?? false;
            _cbConfirmKill.Checked = a?.ConfirmBeforeKill ?? false;
            _loadingEditor = false;
            UpdateDetected();
        }

        private void Edit(Action<AppEntry> apply, bool refreshItem = false)
        {
            if (_loadingEditor || _current == null) return;
            apply(_current);
            if (refreshItem) RefreshCurrentItem();
        }

        private void UpdateDetected()
        {
            if (_current == null) { _lbDetected.Text = ""; return; }
            var exe = _current.ResolveExe();
            if (exe != null)
            {
                _lbDetected.Text = "→ " + exe;
                _lbDetected.ForeColor = Color.SeaGreen;
            }
            else
            {
                _lbDetected.Text = string.IsNullOrWhiteSpace(_current.ExeName) && Directory.Exists(_current.AppPath ?? "")
                    ? "exe ファイル名を入力してください"
                    : "実行ファイルが見つかりません";
                _lbDetected.ForeColor = Color.Firebrick;
            }
        }

        private string CurrentDir()
        {
            var cur = (_tbPath.Text ?? "").Trim().Trim('"');
            try
            {
                if (File.Exists(cur)) return Path.GetDirectoryName(cur);
                if (Directory.Exists(cur)) return cur;
            }
            catch { }
            return null;
        }

        private void BrowseExe()
        {
            using (var d = new OpenFileDialog { Filter = "アプリ (*.exe)|*.exe", Title = "アプリを選択" })
            {
                var dir = CurrentDir();
                if (dir != null) d.InitialDirectory = dir;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                _tbPath.Text = d.FileName;
                _tbExe.Text = Path.GetFileName(d.FileName);
                if (string.IsNullOrWhiteSpace(_tbName.Text) || _tbName.Text == "新しいアプリ")
                    _tbName.Text = ProductNameOf(d.FileName);
                UpdateDetected();
            }
        }

        private void BrowseFolder()
        {
            using (var d = new FolderBrowserDialog { Description = "アプリのフォルダを選択してください" })
            {
                var dir = CurrentDir();
                if (dir != null) d.SelectedPath = dir;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                _tbPath.Text = d.SelectedPath;
                UpdateDetected();
            }
        }

        // ------------------------------------------------------------ 共通

        private Settings CollectSettings() => new Settings
        {
            Apps = _list.Items.Cast<AppEntry>().ToList(),
            SkipIfRunning = _cbSkip.Checked,
            AutoRunOnOpen = _cbAutoRun.Checked,
            ConfirmCloseAll = _cbConfirmClose.Checked,
            ExitAfterCloseAll = _cbExitAfterClose.Checked,
        };

        // ------------------------------------------------------------ 外部からの命令・スタートメニュー

        private async Task HandleCommandAsync(string command, bool fromOtherInstance)
        {
            if (fromOtherInstance) BringToFrontNow();
            switch (command)
            {
                case "start":
                    if (_cts != null) { Log("すでに起動処理中です"); return; }
                    await RunAsync();
                    break;
                case "closeall":
                    await CloseAllAsync();
                    break;
                default: // "show"
                    if (!fromOtherInstance && _cbAutoRun.Checked) await RunAsync();
                    break;
            }
        }

        private void OpenMonitor()
        {
            if (_monitor == null || _monitor.IsDisposed) _monitor = new MonitorForm();
            _monitor.Show();
            if (_monitor.WindowState == FormWindowState.Minimized) _monitor.WindowState = FormWindowState.Normal;
            _monitor.Activate();
        }

        private void BringToFrontNow()
        {
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Show();
            TopMost = true;  // 他のウィンドウの後ろに隠れないよう一瞬だけ最前面に
            TopMost = false;
            Activate();
        }

        private void UpdateStartMenuButton()
        {
            _btnStartMenu.Text = Shortcuts.IsRegistered ? "スタートメニュー登録を解除" : "スタートメニューに登録";
        }

        private void ToggleStartMenu()
        {
            try
            {
                if (Shortcuts.IsRegistered)
                {
                    Shortcuts.Unregister();
                    Log("スタートメニューの登録を解除しました");
                }
                else
                {
                    Shortcuts.Register(Application.ExecutablePath);
                    Log("スタートメニューに登録しました:「StreamLauncher」「StreamLauncher 配信開始」「StreamLauncher 全部終了」");
                    Log("   VSD Craft / Stream Deck の「開く」アクションで、これらをボタンに割り当てられます");
                }
            }
            catch (Exception ex) { Log("スタートメニューを変更できませんでした: " + ex.Message); }
            UpdateStartMenuButton();
        }

        private bool TrySave(Settings s)
        {
            try { s.Save(); return true; }
            catch (Exception ex) { Log("設定を保存できませんでした: " + ex.Message); return false; }
        }

        private void Log(string msg)
        {
            if (IsDisposed) return;
            _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\r\n");
        }

        private void SetBusy(bool busy)
        {
            if (IsDisposed) return;
            _btnRun.Enabled = !busy;
            _btnTree.Enabled = !busy;
            _btnCancel.Enabled = busy && _cts != null;
        }

        private static string ProcOf(string exe) => Path.GetFileNameWithoutExtension(exe);

        // ------------------------------------------------------------ 起動

        private async Task RunAsync()
        {
            if (_cts != null || _closing) return;
            var s = CollectSettings();
            TrySave(s);
            _cts = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                await RunSequenceAsync(s, _cts.Token);
                Log("完了しました");
                if (MonitorSettings.Load().OpenAfterLaunch) OpenMonitor();
            }
            catch (OperationCanceledException) { Log("中止しました"); }
            catch (Exception ex) { Log("エラー: " + ex.Message); }
            finally
            {
                _cts.Dispose();
                _cts = null;
                SetBusy(false);
            }
        }

        private async Task RunSequenceAsync(Settings s, CancellationToken ct)
        {
            var targets = s.Apps.Where(a => a.Enabled).ToList();
            if (targets.Count == 0) throw new Exception("起動するアプリがありません(リストのチェックを入れてください)");

            // 途中で止まらないよう、先に全部の exe を確認
            var exes = new Dictionary<AppEntry, string>();
            foreach (var a in targets)
                exes[a] = a.ResolveExe() ?? throw new Exception($"{a} の実行ファイルが見つかりません: {a.AppPath}");

            int n = 0;
            foreach (var a in targets)
            {
                n++;
                var exe = exes[a];
                bool started = StartApp($"{n}. {a}", exe, a.Args, s.SkipIfRunning);
                if (started && a.WaitAfterSec > 0)
                {
                    Log($"   {a.WaitAfterSec} 秒待機…");
                    await Task.Delay(TimeSpan.FromSeconds(a.WaitAfterSec), ct);
                }

                var clicks = (a.ClickButtons ?? new string[0]).Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
                if (clicks.Count == 0) continue;
                if (s.SkipIfRunning && AppLocator.IsRunning(a.RelatedProcess))
                {
                    Log($"   {a.RelatedProcess} は起動済みなのでボタン操作をスキップ");
                    continue;
                }

                var proc = ProcOf(exe);
                for (int i = 0; i < clicks.Count; i++)
                {
                    // 2つ目以降(確認ダイアログなど)はすぐ出るはずなので短め
                    int timeout = i == 0 ? a.ClickTimeoutSec : 15;
                    var point = ParsePointRef(a, clicks[i]);
                    if (point != null)
                    {
                        await ClickPositionAsync(proc, clicks[i], point, timeout, ct);
                        continue;
                    }
                    Log($"   「{clicks[i]}」を探しています…(最大 {timeout} 秒)");
                    var hit = await WaitElementAsync(proc, clicks[i], timeout, ct)
                              ?? throw new Exception($"{a} の「{clicks[i]}」が見つかりませんでした。「UI要素一覧」で実際の名前を確認するか、「位置を記録」を使ってください");
                    await ClickAsync(hit, a.UseMouseClick);
                }

                if (!string.IsNullOrWhiteSpace(a.RelatedProcess))
                {
                    var end = DateTime.Now.AddSeconds(20);
                    while (!AppLocator.IsRunning(a.RelatedProcess) && DateTime.Now < end)
                        await Task.Delay(500, ct);
                    Log(AppLocator.IsRunning(a.RelatedProcess)
                        ? $"   {a.RelatedProcess} の起動を確認"
                        : $"   注意: {a.RelatedProcess} の起動を確認できませんでした(「マウスでクリックする」を試してください)");
                }
            }
        }

        /// <summary>起動したら true、起動済みでスキップしたら false。</summary>
        private bool StartApp(string label, string exe, string args, bool skipIfRunning)
        {
            if (skipIfRunning && AppLocator.IsRunning(ProcOf(exe)))
            {
                Log($"{label} は起動済みなのでスキップ");
                return false;
            }
            Log($"{label} を起動: {exe}");
            AppLocator.Start(exe, args);
            return true;
        }

        private static async Task<Uia.Hit> WaitElementAsync(string processName, string name, int timeoutSec, CancellationToken ct)
        {
            var deadline = DateTime.Now.AddSeconds(timeoutSec);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var hit = await Task.Run(() => Uia.Find(processName, name), ct);
                if (hit != null) return hit;
                if (DateTime.Now > deadline) return null;
                await Task.Delay(1000, ct);
            }
        }

        // ------------------------------------------------------------ 位置で押す

        private static readonly System.Text.RegularExpressions.Regex PointRef =
            new System.Text.RegularExpressions.Regex(@"^\[位置(\d+)\]$");

        /// <summary>「[位置N]」なら記録済みの位置、それ以外(ボタン名)なら null。</summary>
        private static ClickPoint ParsePointRef(AppEntry a, string line)
        {
            var m = PointRef.Match(line.Trim());
            if (!m.Success) return null;
            int n = int.Parse(m.Groups[1].Value);
            if (a.Points == null || n < 1 || n > a.Points.Count)
                throw new Exception($"{a} の {line} は記録されていません。「位置を記録」でもう一度記録してください");
            return a.Points[n - 1];
        }

        private async Task ClickPositionAsync(string proc, string label, ClickPoint cp, int timeoutSec, CancellationToken ct)
        {
            Log($"   {label} の画面が出るのを待っています…(最大 {timeoutSec} 秒)");
            var deadline = DateTime.Now.AddSeconds(timeoutSec);
            IntPtr hwnd = IntPtr.Zero;
            Point? target = null, fallback = null;
            bool activated = false;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                hwnd = await Task.Run(() => Uia.MainWindow(proc), ct);
                if (hwnd != IntPtr.Zero)
                {
                    // 他のウィンドウに隠れていると画像を探せないので前面に出す
                    if (!activated) { Native.Activate(hwnd); activated = true; await Task.Delay(300, ct); }
                    var h = hwnd;
                    var (expected, matched) = await Task.Run(() => PositionClicker.Locate(h, cp), ct);
                    if (expected != Point.Empty) fallback = expected;
                    if (matched != null) { target = matched; break; }
                }
                if (DateTime.Now > deadline) break;
                await Task.Delay(500, ct);
            }

            if (target == null)
            {
                if (fallback == null) throw new Exception($"{label}: ウィンドウが見つかりませんでした");
                Log("   画像が見つからなかったので、記録した位置をそのままクリックします");
                target = fallback;
            }
            Native.Activate(hwnd);
            await Task.Delay(200, ct);
            Native.Click(target.Value.X, target.Value.Y);
            Log($"   {label} をクリックしました");
        }

        private async Task RecordPositionAsync()
        {
            var a = _current;
            if (a == null) return;
            var proc = a.ProcessName;
            if (proc == null || !AppLocator.IsRunning(proc))
            {
                Log($"{a} が起動していません。先に起動して、押したいボタンが見える状態にしてください");
                return;
            }
            if (MessageBox.Show(this,
                    $"OK を押すと 3 秒後にマウスの位置を記録します。\r\nそれまでに、{a} の押したいボタンの上にマウスを置いてください。",
                    "位置を記録", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
                return;

            _btnRecord.Enabled = false;
            try
            {
                for (int i = 3; i > 0; i--) { Log($"   {i}…"); await Task.Delay(1000); }
                var cp = PositionClicker.Record(proc);
                if (a.Points == null) a.Points = new List<ClickPoint>();
                a.Points.Add(cp);
                var line = $"[位置{a.Points.Count}]";
                var text = _tbClicks.Text.TrimEnd();
                _tbClicks.Text = (text.Length > 0 ? text + "\r\n" : "") + line; // TextChanged で ClickButtons に反映
                Log($"{line} を記録しました。「起動後に押すボタン」に追加しています(順番は入れ替えられます)");
            }
            catch (Exception ex)
            {
                Log("記録できませんでした: " + ex.Message);
            }
            finally
            {
                _btnRecord.Enabled = true;
                BringToFrontNow();
            }
        }

        private async Task ClickAsync(Uia.Hit hit, bool useMouse)
        {
            Log($"   発見: {hit.TypeName} '{hit.Name}'");
            var how = await Task.Run(() => Uia.Click(hit, useMouse));
            Log($"   クリックしました ({how})");
        }

        // ------------------------------------------------------------ 全部終了

        private sealed class CloseTarget
        {
            public string Label;
            public string Process;
            public bool AskBeforeKill;
        }

        private async Task CloseAllAsync()
        {
            if (_closing) return;
            var s = CollectSettings();

            // 起動と逆順。関連プロセス(CastCraft Screen など)は本体より先に閉じる
            var targets = new List<CloseTarget>();
            foreach (var a in Enumerable.Reverse(s.Apps).Where(a => a.Enabled && a.CloseOnExit))
            {
                if (!string.IsNullOrWhiteSpace(a.RelatedProcess))
                    targets.Add(new CloseTarget { Label = $"{a} ({a.RelatedProcess.Trim()})", Process = a.RelatedProcess.Trim() });
                var proc = a.ProcessName;
                if (proc != null)
                    targets.Add(new CloseTarget { Label = a.ToString(), Process = proc, AskBeforeKill = a.ConfirmBeforeKill });
            }

            var running = targets.Where(t => AppLocator.IsRunning(t.Process)).ToList();
            if (running.Count == 0)
            {
                Log("終了するアプリはありません");
                if (s.ExitAfterCloseAll) await ExitLauncherAsync(s);
                return;
            }

            var names = string.Join("、", running.Select(t => t.Label));
            if (s.ConfirmCloseAll && MessageBox.Show(this, $"{names} を終了します。\r\n配信・録画中の場合は止まります。よろしいですか?",
                    "全部終了", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            _cts?.Cancel(); // 起動処理中なら止める
            _closing = true;
            _btnCloseAll.Enabled = false;
            SetBusy(true);
            bool allClosed = true;
            try
            {
                foreach (var t in running)
                {
                    if (!AppLocator.IsRunning(t.Process)) continue;
                    Log($"{t.Label} を終了しています…");
                    AppLocator.RequestClose(t.Process);
                    if (await WaitExitAsync(t.Process, t.AskBeforeKill ? 15 : 5))
                    {
                        Log($"   {t.Label} を終了しました");
                        continue;
                    }

                    if (t.AskBeforeKill)
                    {
                        var ans = MessageBox.Show(this,
                            $"{t.Label} が終了しません。\r\n(アプリ側で確認ダイアログが出ていないか確認してください)\r\n\r\n強制終了しますか?",
                            "全部終了", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                        // ダイアログを見ている間に自分で閉じた場合
                        if (!AppLocator.IsRunning(t.Process)) { Log($"   {t.Label} を終了しました"); continue; }
                        if (ans != DialogResult.Yes) { Log($"   {t.Label} は終了しませんでした"); allClosed = false; continue; }
                    }

                    AppLocator.Kill(t.Process);
                    if (await WaitExitAsync(t.Process, 5))
                    {
                        Log($"   {t.Label} を強制終了しました");
                    }
                    else
                    {
                        Log($"   {t.Label} を終了できませんでした");
                        allClosed = false;
                    }
                }
                Log(allClosed ? "全部終了しました" : "終了できなかったアプリがあります");
            }
            catch (Exception ex)
            {
                Log("エラー: " + ex.Message);
                allClosed = false;
            }
            finally
            {
                _closing = false;
                if (!IsDisposed) _btnCloseAll.Enabled = true;
                SetBusy(_cts != null);
            }

            // 閉じきれなかったアプリがあるときは、ログを見られるようランチャーは残す
            if (allClosed && s.ExitAfterCloseAll) await ExitLauncherAsync(s);
        }

        private async Task ExitLauncherAsync(Settings s)
        {
            TrySave(s);
            Log("ランチャーを閉じます");
            await Task.Delay(800); // ログが一瞬見えるように
            if (!IsDisposed) Close();
        }

        private static async Task<bool> WaitExitAsync(string processName, int timeoutSec)
        {
            var end = DateTime.Now.AddSeconds(timeoutSec);
            while (DateTime.Now < end)
            {
                if (!AppLocator.IsRunning(processName)) return true;
                await Task.Delay(300);
            }
            return !AppLocator.IsRunning(processName);
        }

        // ------------------------------------------------------------ 更新

        private async Task AutoCheckUpdateAsync()
        {
            try
            {
                var info = await Updater.CheckAsync();
                if (info == null || IsDisposed) return;
                ShowUpdateAvailable(info);
            }
            catch { } // 起動時の自動確認はオフラインでも静かに失敗させる
        }

        private void ShowUpdateAvailable(Updater.ReleaseInfo info)
        {
            _pendingUpdate = info;
            _btnUpdate.Text = $"v{info.Version} に更新";
            _btnUpdate.ForeColor = Color.SeaGreen;
            _btnUpdate.Font = new Font(Font, FontStyle.Bold);
            Log($"新しいバージョン v{info.Version} があります。「v{info.Version} に更新」で更新できます");
        }

        private async Task UpdateAsync()
        {
            if (_updating) return;
            if (_cts != null || _closing)
            {
                Log("起動・終了の処理中は更新できません。終わってからもう一度押してください");
                return;
            }
            _updating = true;
            _btnUpdate.Enabled = false;
            try
            {
                var info = _pendingUpdate;
                if (info == null)
                {
                    Log("更新を確認しています…");
                    info = await Updater.CheckAsync();
                    if (info == null)
                    {
                        Log($"最新バージョンです(v{Updater.CurrentVersionText})");
                        return;
                    }
                    ShowUpdateAvailable(info);
                }

                if (MessageBox.Show(this,
                        $"v{Updater.CurrentVersionText} → v{info.Version} に更新します。\r\n更新後、ランチャーは自動で再起動します(起動中の配信アプリはそのままです)。",
                        "更新", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
                    return;

                TrySave(CollectSettings());
                Log($"v{info.Version} をダウンロードしています…");
                var exe = Application.ExecutablePath;
                await Updater.InstallAsync(info, exe);
                Process.Start(new ProcessStartInfo(exe, "--updated")
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(exe),
                })?.Dispose();
                Close();
            }
            catch (UnauthorizedAccessException)
            {
                OfferManualDownload("このフォルダには書き込めないため、自動で更新できませんでした。\r\n(Program Files などに置いている場合は、デスクトップなどに移すと自動更新できます)");
            }
            catch (Exception ex)
            {
                Log("更新できませんでした: " + ex.Message);
                OfferManualDownload("更新できませんでした。\r\n" + ex.Message);
            }
            finally
            {
                _updating = false;
                if (!IsDisposed) _btnUpdate.Enabled = true;
            }
        }

        private void OfferManualDownload(string message)
        {
            if (MessageBox.Show(this, message + "\r\n\r\nダウンロードページを開きますか?", "更新",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            {
                try { Process.Start(new ProcessStartInfo(Updater.ReleasesPage) { UseShellExecute = true })?.Dispose(); }
                catch { }
            }
        }

        // ------------------------------------------------------------ UI要素一覧

        private async Task ShowUiTreeAsync()
        {
            var a = _current;
            var proc = a?.ProcessName;
            if (proc == null || !AppLocator.IsRunning(proc))
            {
                Log($"{(a?.ToString() ?? "アプリ")} が起動していません。リストで選んだアプリを先に起動してください");
                return;
            }
            _btnTree.Enabled = false;
            try
            {
                var lines = await Task.Run(() => Uia.Dump(proc));
                Log($"{a} の UI 要素({lines.Count} 件):");
                if (lines.Count > 0) _log.AppendText(string.Join("\r\n", lines) + "\r\n");
                else Log("要素が取れません。アプリを一度終了してから「起動」で起動し直してください(Electron 製アプリは起動引数 --force-renderer-accessibility が必要です)");
            }
            catch (Exception ex) { Log("エラー: " + ex.Message); }
            finally { if (!IsDisposed) _btnTree.Enabled = _cts == null && !_closing; }
        }
    }
}
