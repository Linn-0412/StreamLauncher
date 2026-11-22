using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Media;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace StreamLauncher
{
    /// <summary>配信の健康状態モニター(obs-websocket)。</summary>
    internal sealed class MonitorForm : Form
    {
        private static readonly Color OkColor = Color.SeaGreen;
        private static readonly Color WarnColor = Color.DarkOrange;
        private static readonly Color BadColor = Color.Firebrick;
        private static readonly Color MutedColor = SystemColors.GrayText;
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        private MonitorSettings _s;
        private readonly ObsClient _obs = new ObsClient();
        private readonly HealthMonitor _health;
        private readonly Dictionary<string, Problem> _active = new Dictionary<string, Problem>();
        private readonly Dictionary<string, DateTime> _lastDiscord = new Dictionary<string, DateTime>();
        private bool _closing;
        private bool _busy;
        private string _lastConnectError;

        private Label _lblStatus, _banner;
        private readonly Dictionary<string, Label> _values = new Dictionary<string, Label>();
        private CheckBox _cbTop;
        private Button _btnCheck, _btnSettings;
        private TextBox _log;
        private NotifyIcon _tray;

        public MonitorForm()
        {
            _s = MonitorSettings.Load();
            _health = new HealthMonitor(_obs, _s);
            BuildUi();
            Shown += async (s, e) => await LoopAsync();
            FormClosing += (s, e) => { _closing = true; _obs.Close(); _tray.Visible = false; _tray.Dispose(); };
        }

        // ------------------------------------------------------------ 画面

        private static T Add<T>(Control parent, int x, int y, int w, int h, string text = null) where T : Control, new()
        {
            var c = new T { Location = new Point(x, y), Size = new Size(w, h) };
            if (text != null) c.Text = text;
            parent.Controls.Add(c);
            return c;
        }

        private void BuildUi()
        {
            SuspendLayout();
            Text = "配信モニター";
            Font = new Font("Yu Gothic UI", 9F);
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(380, 504);
            MinimumSize = new Size(396, 444);
            MaximizeBox = false;
            StartPosition = FormStartPosition.Manual;
            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - 420, wa.Top + 40);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            TopMost = _s.AlwaysOnTop;

            _lblStatus = Add<Label>(this, 10, 8, 360, 30, "接続しています…");
            _lblStatus.Font = new Font("Yu Gothic UI", 13F, FontStyle.Bold);

            string[] rows = { "ビットレート", "回線(送信)", "エンコード", "描画", "マイク", "フィルター", "CPU / メモリ", "ディスク空き", "録画" };
            int y = 46;
            foreach (var r in rows)
            {
                Add<Label>(this, 14, y, 110, 22, r).ForeColor = MutedColor;
                var v = Add<Label>(this, 124, y, 246, 22, "—");
                v.Font = new Font("Yu Gothic UI", 9.5F, FontStyle.Bold);
                _values[r] = v;
                y += 24;
            }

            _banner = Add<Label>(this, 10, 268, 360, 62);
            _banner.Padding = new Padding(6);
            _banner.Visible = false;
            _banner.Font = new Font("Yu Gothic UI", 9.5F, FontStyle.Bold);

            _btnCheck = Add<Button>(this, 10, 338, 110, 30, "事前チェック");
            _btnSettings = Add<Button>(this, 126, 338, 80, 30, "設定");
            _cbTop = Add<CheckBox>(this, 216, 342, 154, 22, "常に手前に表示");
            _cbTop.Checked = _s.AlwaysOnTop;

            _log = Add<TextBox>(this, 10, 376, 360, 118);
            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.BackColor = SystemColors.Window;
            _log.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            _tray = new NotifyIcon { Icon = Icon, Text = "配信モニター", Visible = true };
            _tray.DoubleClick += (s, e) => { Show(); WindowState = FormWindowState.Normal; Activate(); };

            _btnCheck.Click += async (s, e) => await RunPreflightAsync();
            _btnSettings.Click += (s, e) => OpenSettings();
            _cbTop.CheckedChanged += (s, e) => { TopMost = _cbTop.Checked; _s.AlwaysOnTop = _cbTop.Checked; TrySaveSettings(); };

            ResumeLayout(false);
            PerformLayout();
        }

        private void Log(string msg)
        {
            if (IsDisposed) return;
            _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\r\n");
        }

        private void TrySaveSettings()
        {
            try { _s.Save(); } catch (Exception ex) { Log("設定を保存できませんでした: " + ex.Message); }
        }

        private void SetValue(string row, string text, Color color)
        {
            var l = _values[row];
            l.Text = text;
            l.ForeColor = color;
        }

        // ------------------------------------------------------------ 監視ループ

        private async Task LoopAsync()
        {
            while (!_closing)
            {
                if (!_obs.IsConnected && !await TryConnectAsync())
                {
                    if (_closing) break;
                    await Task.Delay(5000);
                    continue;
                }
                try
                {
                    var snap = await _health.PollAsync();
                    if (_closing) break;
                    Render(snap);
                }
                catch (Exception ex)
                {
                    if (_closing) break;
                    if (!_obs.IsConnected) Render(_health.DisconnectedSnapshot(ex.Message));
                }
                await Task.Delay(2000);
            }
        }

        private async Task<bool> TryConnectAsync()
        {
            var cfg = ObsLocalConfig.Read();
            if (_s.IsLocal && cfg.Found && !cfg.Enabled)
            {
                ShowDisconnected("OBS の WebSocket サーバーが無効です",
                    "OBS の「ツール → WebSocket サーバー設定」で「WebSocket サーバーを有効にする」にチェックを入れてください");
                return false;
            }
            int port = _s.Port > 0 ? _s.Port : cfg.Port;
            string password = !string.IsNullOrEmpty(_s.Password) ? _s.Password : (_s.IsLocal ? cfg.Password : "");
            try
            {
                await _obs.ConnectAsync(string.IsNullOrWhiteSpace(_s.Host) ? "localhost" : _s.Host.Trim(), port, password,
                    ObsClient.DefaultEventSubscriptions, TimeSpan.FromSeconds(3));
                _lastConnectError = null;
                Log("OBS に接続しました");
                return true;
            }
            catch (Exception ex)
            {
                var msg = AppLocator.IsRunning("obs64") || AppLocator.IsRunning("obs32") || !_s.IsLocal
                    ? ex.Message
                    : "OBS が起動していません";
                Render(_health.DisconnectedSnapshot(msg));
                return false;
            }
        }

        private void ShowDisconnected(string title, string detail)
        {
            _lblStatus.Text = "✕ " + title;
            _lblStatus.ForeColor = BadColor;
            foreach (var l in _values.Values) { l.Text = "—"; l.ForeColor = MutedColor; }
            if (_lastConnectError != detail) { Log(detail); _lastConnectError = detail; }
        }

        private void Render(HealthSnapshot s)
        {
            if (IsDisposed) return;
            if (!s.Connected)
            {
                ShowDisconnected("OBS に接続できません", s.DisconnectReason);
            }
            else
            {
                if (s.Streaming)
                {
                    _lblStatus.Text = $"● 配信中 {TimeSpan.FromSeconds(s.StreamSec):hh\\:mm\\:ss}";
                    _lblStatus.ForeColor = Color.Crimson;
                }
                else
                {
                    _lblStatus.Text = "○ 配信していません";
                    _lblStatus.ForeColor = MutedColor;
                }

                bool warmingUp = s.Streaming && (s.StreamSec < 15 || !s.HasRates);
                var target = s.TargetKbps > 0 ? $" / {(s.TargetIsObserved ? "最大" : "設定")} {s.TargetKbps:0}" : "";
                if (!s.Streaming) SetValue("ビットレート", s.TargetKbps > 0 && !s.TargetIsObserved ? $"— (設定 {s.TargetKbps:0} kbps)" : "—", MutedColor);
                else SetValue("ビットレート", $"{s.Kbps:0}{target} kbps" + (warmingUp ? "(計測中)" : ""),
                    warmingUp ? MutedColor : s.TargetKbps > 0 && s.Kbps < s.TargetKbps * _s.BitrateWarnPercent / 100.0 ? WarnColor : OkColor);

                if (!s.Streaming) SetValue("回線(送信)", "—", MutedColor);
                else if (s.Reconnecting) SetValue("回線(送信)", "再接続中", BadColor);
                else SetValue("回線(送信)", $"ドロップ {s.NetDropPct:0.0}% / 混雑 {s.Congestion:0.00}",
                    s.NetDropPct >= _s.DropWarnPercent * 5 ? BadColor : s.NetDropPct >= _s.DropWarnPercent || s.Congestion >= 0.5 ? WarnColor : OkColor);

                bool active = s.Streaming || s.Recording;
                SetValue("エンコード", active ? (s.EncodeSkippedRecent > 0 ? $"遅れ {s.EncodeSkippedRecent:0} フレーム(10 秒)" : "遅れなし") : "—",
                    active ? (s.EncodeSkippedRecent > 0 ? WarnColor : OkColor) : MutedColor);
                SetValue("描画", s.RenderSkippedRecent > 0 ? $"遅れ {s.RenderSkippedRecent:0} フレーム(10 秒)" : "遅れなし",
                    s.RenderSkippedRecent > 0 ? (active ? WarnColor : MutedColor) : OkColor);

                if (s.MicName == null) SetValue("マイク", "マイク入力が見つかりません", WarnColor);
                else if (s.MicMuted) SetValue("マイク", $"ミュート中({s.MicName})", s.Streaming ? WarnColor : MutedColor);
                else if (s.MicClipping) SetValue("マイク", $"音割れ!({s.MicDb:0} dB)", BadColor);
                else
                {
                    bool speaking = s.MicDb > HealthMonitor.MicActiveDb;
                    var silent = s.MicSilentSec >= _s.MicSilentSec && s.Streaming;
                    SetValue("マイク", speaking ? $"音あり {s.MicDb:0} dB" : $"無音 {s.MicSilentSec:0} 秒",
                        speaking ? OkColor : silent ? WarnColor : MutedColor);
                }

                if (s.Filters.Count == 0) SetValue("フィルター", "確認していません(設定で指定)", MutedColor);
                else
                {
                    var bad = s.Filters.Where(f => f.Enabled != true).ToList();
                    SetValue("フィルター", bad.Count == 0 ? $"OK({string.Join("、", s.Filters.Select(f => f.Filter))})"
                        : string.Join("、", bad.Select(f => f.Filter + (f.Enabled == null ? " 見つからない" : " 無効"))),
                        bad.Count == 0 ? OkColor : s.Streaming ? WarnColor : MutedColor);
                }

                SetValue("CPU / メモリ", $"{s.CpuPct:0}% / {s.MemoryMb:0} MB", s.CpuPct >= _s.CpuWarnPercent ? WarnColor : OkColor);
                SetValue("ディスク空き", s.DiskGb >= 0 ? $"{s.DiskGb:0.0} GB" : "—",
                    s.DiskGb < 0 ? MutedColor : s.DiskGb < _s.DiskWarnGb ? WarnColor : OkColor);
                var recText = s.Recording ? $"録画中 {TimeSpan.FromSeconds(s.RecordSec):hh\\:mm\\:ss}" : "停止中";
                if (s.ReplayBuffer != null) recText += $" / リプレイ {s.ReplayBuffer}";
                SetValue("録画", recText,
                    s.Streaming && s.ReplayBuffer != null && s.ReplayBuffer != "動作中" ? WarnColor : s.Recording ? Color.Crimson : MutedColor);
            }
            UpdateProblems(s.Problems);
        }

        // ------------------------------------------------------------ 通知

        private void UpdateProblems(List<Problem> current)
        {
            var now = current.ToDictionary(p => p.Key);
            foreach (var p in current.Where(p => !_active.ContainsKey(p.Key)))
            {
                Log((p.Level == HealthLevel.Critical ? "🚨 " : "⚠ ") + p.Message);
                Notify(p);
            }
            foreach (var old in _active.Values.Where(o => !now.ContainsKey(o.Key)).ToList())
                Log("✓ 解消: " + old.Message);
            _active.Clear();
            foreach (var p in current) _active[p.Key] = p;

            if (current.Count == 0) { _banner.Visible = false; return; }
            bool critical = current.Any(p => p.Level == HealthLevel.Critical);
            _banner.BackColor = critical ? Color.FromArgb(255, 220, 220) : Color.FromArgb(255, 240, 200);
            _banner.ForeColor = critical ? BadColor : Color.FromArgb(150, 80, 0);
            _banner.Text = string.Join("\r\n", current.OrderByDescending(p => p.Level).Select(p => "・" + p.Message));
            _banner.Visible = true;
        }

        private void Notify(Problem p)
        {
            if (_s.PlaySound)
                (p.Level == HealthLevel.Critical ? SystemSounds.Hand : SystemSounds.Exclamation).Play();
            if (_s.WindowsNotify)
            {
                try { _tray.ShowBalloonTip(5000, "配信モニター", p.Message, p.Level == HealthLevel.Critical ? ToolTipIcon.Error : ToolTipIcon.Warning); }
                catch { }
            }
            var hook = (_s.DiscordWebhook ?? "").Trim();
            if (hook.Length > 0)
            {
                // 同じ問題は 10 分に 1 回まで
                if (_lastDiscord.TryGetValue(p.Key, out var last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(10)) return;
                _lastDiscord[p.Key] = DateTime.UtcNow;
                _ = SendDiscordAsync(hook, (p.Level == HealthLevel.Critical ? "🚨 " : "⚠️ ") + p.Message);
            }
        }

        private async Task SendDiscordAsync(string webhook, string text)
        {
            try
            {
                var json = new JavaScriptSerializer().Serialize(new Dictionary<string, object> { ["content"] = "【配信モニター】" + text });
                using (var res = await Http.PostAsync(webhook, new StringContent(json, Encoding.UTF8, "application/json")))
                    if (!res.IsSuccessStatusCode) Log($"Discord に通知できませんでした({(int)res.StatusCode})");
            }
            catch (Exception ex) { Log("Discord に通知できませんでした: " + ex.Message); }
        }

        // ------------------------------------------------------------ 設定

        private void OpenSettings()
        {
            Func<Task<List<string>>> getInputs = async () => _obs.IsConnected ? await _health.GetInputNamesAsync() : new List<string>();
            using (var f = new MonitorSettingsForm(_s, getInputs))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                _s = f.Result;
                TrySaveSettings();
                _health.UpdateSettings(_s);
                TopMost = _cbTop.Checked = _s.AlwaysOnTop;
                _obs.Close(); // 接続先が変わったかもしれないので繋ぎ直す
                Log("設定を保存しました");
            }
        }

        // ------------------------------------------------------------ 事前チェック

        private async Task RunPreflightAsync()
        {
            if (_busy) return;
            _busy = true;
            _btnCheck.Enabled = false;
            try
            {
                using (var f = new PreflightForm())
                {
                    f.Show(this);
                    await RunChecksAsync(f);
                    f.Done();
                    while (f.Visible && !f.IsDisposed) await Task.Delay(200);
                }
            }
            finally
            {
                _busy = false;
                if (!IsDisposed) _btnCheck.Enabled = true;
            }
        }

        private async Task RunChecksAsync(PreflightForm f)
        {
            if (!_obs.IsConnected) await TryConnectAsync();
            if (!_obs.IsConnected)
            {
                f.Add(false, "OBS に接続", _lastConnectError ?? "接続できません。OBS が起動しているか、WebSocket サーバーが有効か確認してください");
                return;
            }
            f.Add(true, "OBS に接続", "OK");

            try
            {
                var stream = await _obs.RequestAsync("GetStreamStatus");
                f.Add(true, "配信状態", J.B(stream, "outputActive") ? "配信中です" : "まだ配信していません");

                var svc = await _obs.RequestAsync("GetStreamServiceSettings");
                var ss = J.D(J.Get(svc, "streamServiceSettings"));
                var where = J.S(ss, "service").Length > 0 ? J.S(ss, "service") : J.S(ss, "server");
                if (_s.AccountConnected)
                    f.Add(true, "配信先", $"{(where.Length > 0 ? where : "サービス")}(アカウント接続のため、ストリームキーは確認しません)");
                else
                    f.Add(J.S(ss, "key").Length > 0, "配信先", J.S(ss, "key").Length > 0
                        ? $"{where}(ストリームキー設定済み)"
                        : "ストリームキーが設定されていません(OBS の 設定 → 配信。アカウント接続の場合はモニターの設定で切り替えてください)");

                var scene = await _obs.RequestAsync("GetCurrentProgramScene");
                var sceneName = J.S(scene, "sceneName").Length > 0 ? J.S(scene, "sceneName") : J.S(scene, "currentProgramSceneName");
                f.Add(true, "現在のシーン", sceneName + "(配信開始時に映るシーンです)");

                var kbps = await _health.ReadConfiguredKbpsAsync();
                f.Add(kbps > 0, "ビットレート", kbps > 0
                    ? $"{kbps:0} kbps(これの {_s.BitrateWarnPercent}% を下回ったら警告します)"
                    : "設定値を読み取れませんでした(配信中の最大値を基準にします。モニターの設定で指定もできます)", warnOnly: true);

                var filters = await _health.CheckFiltersAsync();
                if (filters.Count == 0)
                    f.Add(true, "フィルター", "確認するフィルターはありません(モニターの設定で指定できます)");
                foreach (var flt in filters)
                    f.Add(flt.Enabled == true, "フィルター",
                        flt.Enabled == true ? $"{flt.Source}:{flt.Filter} は有効です"
                        : flt.Enabled == false ? $"{flt.Source}:{flt.Filter} が無効になっています"
                        : $"{flt.Source} にフィルター「{flt.Filter}」が見つかりません");

                if (_s.CheckReplayBuffer)
                {
                    var rb = await _health.CheckReplayBufferAsync();
                    f.Add(rb == "動作中", "リプレイバッファ", rb == "動作中" ? "動作中です"
                        : rb == "停止中" ? "止まっています(OBS の「リプレイバッファ開始」を押してください)"
                        : "使えません(OBS の 設定 → 出力 で有効にしてください)", warnOnly: true);
                }

                var stats = await _obs.RequestAsync("GetStats");
                var video = await _obs.RequestAsync("GetVideoSettings");
                double targetFps = J.N(video, "fpsDenominator") > 0 ? J.N(video, "fpsNumerator") / J.N(video, "fpsDenominator") : 0;
                double fps = J.N(stats, "activeFps");
                bool fpsOk = targetFps <= 0 || fps >= targetFps * 0.95;
                f.Add(fpsOk, "OBS の描画", $"{fps:0.0} / {targetFps:0.##} fps" + (fpsOk ? "" : "(設定の FPS に届いていません。PC の負荷を確認してください)"), warnOnly: true);

                double cpu = J.N(stats, "cpuUsage");
                f.Add(cpu < _s.CpuWarnPercent, "CPU", $"{cpu:0}%", warnOnly: true);

                double disk = J.N(stats, "availableDiskSpace", -1024) / 1024.0;
                f.Add(disk < 0 || disk >= _s.DiskWarnGb, "録画先の空き", disk >= 0 ? $"{disk:0.0} GB" : "不明", warnOnly: true);

                await _health.ResolveMicAsync();
                var mic = _health.MicName;
                if (mic == null)
                {
                    f.Add(false, "マイク", "OBS にマイク入力が見つかりません(モニターの設定でマイクを選べます)", warnOnly: true);
                    return;
                }
                bool muted = J.B(await _obs.RequestAsync("GetInputMute", new Dictionary<string, object> { ["inputName"] = mic }), "inputMuted");
                if (muted)
                {
                    f.Add(false, "マイク", $"{mic} がミュートされています");
                    return;
                }
                var start = DateTime.UtcNow;
                for (int i = 5; i > 0 && _health.LastMicActive <= start; i--)
                {
                    f.SetStatus($"マイクのテスト中です。マイクに向かって話してください…({i})");
                    await Task.Delay(1000);
                }
                f.SetStatus("");
                bool heard = _health.LastMicActive > start;
                f.Add(heard, "マイク", heard ? $"{mic}:音声を確認しました" : $"{mic}:5 秒間、音が入りませんでした(マイクの接続・選択を確認してください)", warnOnly: true);
            }
            catch (Exception ex)
            {
                f.Add(false, "チェック中にエラー", ex.Message);
            }
        }
    }

    /// <summary>事前チェックの結果表示。</summary>
    internal sealed class PreflightForm : Form
    {
        private readonly ListView _list;
        private readonly Label _status;
        private readonly Button _close;
        private int _ng, _warn;

        public PreflightForm()
        {
            Text = "事前チェック";
            Font = new Font("Yu Gothic UI", 9F);
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(520, 330);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            TopMost = true;

            _list = new ListView { Location = new Point(10, 10), Size = new Size(500, 240), View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable };
            _list.Columns.Add("", 34);
            _list.Columns.Add("項目", 110);
            _list.Columns.Add("結果", 340);
            Controls.Add(_list);
            _status = new Label { Location = new Point(10, 258), Size = new Size(500, 40), Font = new Font("Yu Gothic UI", 10F, FontStyle.Bold), ForeColor = Color.SteelBlue };
            Controls.Add(_status);
            _close = new Button { Location = new Point(420, 294), Size = new Size(90, 28), Text = "閉じる", Enabled = false };
            _close.Click += (s, e) => Close();
            Controls.Add(_close);
        }

        /// <param name="warnOnly">失敗しても「要確認」扱い(配信できないほどではない)</param>
        public void Add(bool ok, string item, string detail, bool warnOnly = false)
        {
            if (IsDisposed) return;
            var mark = ok ? "✓" : warnOnly ? "!" : "✕";
            var it = new ListViewItem(new[] { mark, item, detail })
            {
                ForeColor = ok ? Color.SeaGreen : warnOnly ? Color.DarkOrange : Color.Firebrick,
            };
            _list.Items.Add(it);
            if (!ok) { if (warnOnly) _warn++; else _ng++; }
        }

        public void SetStatus(string text)
        {
            if (!IsDisposed) _status.Text = text;
        }

        public void Done()
        {
            if (IsDisposed) return;
            _close.Enabled = true;
            _status.ForeColor = _ng > 0 ? Color.Firebrick : _warn > 0 ? Color.DarkOrange : Color.SeaGreen;
            _status.Text = _ng > 0 ? $"✕ 配信前に直してください({_ng} 件)"
                : _warn > 0 ? $"! 確認してください({_warn} 件)。配信はできます"
                : "✓ 配信の準備ができています";
        }
    }

    /// <summary>モニターの設定画面。</summary>
    internal sealed class MonitorSettingsForm : Form
    {
        public MonitorSettings Result { get; private set; }

        private readonly MonitorSettings _src;
        private readonly Func<Task<List<string>>> _getInputs;
        private TextBox _host, _password, _webhook, _filters;
        private NumericUpDown _port, _drop, _bitratePct, _target, _silent, _cpu, _disk;
        private ComboBox _mic, _target2;
        private CheckBox _sound, _toast, _openAfter, _replay;

        public MonitorSettingsForm(MonitorSettings s, Func<Task<List<string>>> getInputs)
        {
            _src = s;
            _getInputs = getInputs;
            Build();
            Shown += async (o, e) => await LoadInputsAsync();
        }

        private static T Add<T>(Control parent, int x, int y, int w, int h, string text = null) where T : Control, new()
        {
            var c = new T { Location = new Point(x, y), Size = new Size(w, h) };
            if (text != null) c.Text = text;
            parent.Controls.Add(c);
            return c;
        }

        private NumericUpDown Num(Control parent, int x, int y, decimal min, decimal max, decimal value, int decimals = 0)
        {
            var n = Add<NumericUpDown>(parent, x, y, 80, 23);
            n.Minimum = min; n.Maximum = max; n.DecimalPlaces = decimals;
            n.Increment = decimals > 0 ? 0.5m : 1;
            n.Value = Math.Min(max, Math.Max(min, value));
            return n;
        }

        private void Build()
        {
            Text = "配信モニターの設定";
            Font = new Font("Yu Gothic UI", 9F);
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(480, 704);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            var gObs = Add<GroupBox>(this, 10, 8, 460, 140, "OBS への接続");
            Add<Label>(gObs, 12, 26, 90, 20, "接続先");
            _host = Add<TextBox>(gObs, 110, 23, 160, 23); _host.Text = _src.Host;
            Add<Label>(gObs, 282, 26, 50, 20, "ポート");
            _port = Num(gObs, 334, 23, 0, 65535, _src.Port);
            Add<Label>(gObs, 12, 56, 90, 20, "パスワード");
            _password = Add<TextBox>(gObs, 110, 53, 160, 23); _password.UseSystemPasswordChar = true; _password.Text = _src.Password;
            var hint = Add<Label>(gObs, 282, 56, 172, 22, "空欄 = この PC の OBS の設定");
            hint.ForeColor = SystemColors.GrayText;
            Add<Label>(gObs, 12, 86, 90, 20, "配信先");
            _target2 = Add<ComboBox>(gObs, 110, 83, 230, 23);
            _target2.DropDownStyle = ComboBoxStyle.DropDownList;
            _target2.Items.AddRange(new object[] { "ストリームキーで接続", "アカウント接続(YouTube / Twitch 等)" });
            _target2.SelectedIndex = _src.AccountConnected ? 1 : 0;
            Add<Label>(gObs, 12, 112, 440, 22, "アカウント接続の場合、事前チェックでストリームキーを確認しません").ForeColor = SystemColors.GrayText;

            var gMic = Add<GroupBox>(this, 10, 154, 460, 60, "マイク");
            _mic = Add<ComboBox>(gMic, 12, 24, 300, 23);
            _mic.DropDownStyle = ComboBoxStyle.DropDown;
            _mic.Text = _src.MicInput;
            Add<Label>(gMic, 318, 27, 136, 20, "(空欄 = 自動)").ForeColor = SystemColors.GrayText;

            var gTh = Add<GroupBox>(this, 10, 220, 460, 200, "警告する条件");
            int y = 24;
            Add<Label>(gTh, 12, y + 3, 250, 20, "回線のドロップ(直近 30 秒, %)以上"); _drop = Num(gTh, 270, y, 0.1m, 50, (decimal)_src.DropWarnPercent, 1); y += 28;
            Add<Label>(gTh, 12, y + 3, 250, 20, "ビットレートが基準の(%)未満"); _bitratePct = Num(gTh, 270, y, 10, 100, _src.BitrateWarnPercent); y += 28;
            Add<Label>(gTh, 12, y + 3, 250, 20, "基準のビットレート(kbps)"); _target = Num(gTh, 270, y, 0, 100000, _src.TargetBitrateKbps);
            Add<Label>(gTh, 356, y + 3, 100, 20, "0 = OBS の設定").ForeColor = SystemColors.GrayText; y += 28;
            Add<Label>(gTh, 12, y + 3, 250, 20, "配信中のマイクの無音(秒)以上"); _silent = Num(gTh, 270, y, 5, 3600, _src.MicSilentSec); y += 28;
            Add<Label>(gTh, 12, y + 3, 250, 20, "OBS の CPU 使用率(10 秒平均, %)以上"); _cpu = Num(gTh, 270, y, 10, 100, _src.CpuWarnPercent); y += 28;
            Add<Label>(gTh, 12, y + 3, 250, 20, "録画先の空き(GB)未満"); _disk = Num(gTh, 270, y, 0, 10000, _src.DiskWarnGb);

            var gFl = Add<GroupBox>(this, 10, 426, 460, 128, "必ず有効にしておくフィルター・リプレイバッファ");
            _filters = Add<TextBox>(gFl, 12, 22, 250, 70);
            _filters.Multiline = true; _filters.ScrollBars = ScrollBars.Vertical; _filters.AcceptsReturn = true;
            _filters.Text = (_src.RequiredFilters ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n");
            Add<Label>(gFl, 270, 22, 184, 70, "1 行に「ソース名:フィルター名」\r\n例: マイク:ボイチェン\r\n配信中に無効になると警告します").ForeColor = SystemColors.GrayText;
            _replay = Add<CheckBox>(gFl, 12, 98, 440, 22, "配信中にリプレイバッファが止まっていたら警告する"); _replay.Checked = _src.CheckReplayBuffer;

            var gNo = Add<GroupBox>(this, 10, 560, 460, 104, "通知");
            _sound = Add<CheckBox>(gNo, 12, 22, 140, 22, "音を鳴らす"); _sound.Checked = _src.PlaySound;
            _toast = Add<CheckBox>(gNo, 160, 22, 160, 22, "Windows の通知"); _toast.Checked = _src.WindowsNotify;
            _openAfter = Add<CheckBox>(gNo, 12, 46, 440, 22, "ランチャーの起動が終わったら、このモニターを開く"); _openAfter.Checked = _src.OpenAfterLaunch;
            Add<Label>(gNo, 12, 74, 110, 20, "Discord Webhook");
            _webhook = Add<TextBox>(gNo, 126, 71, 322, 23); _webhook.Text = _src.DiscordWebhook;

            var ok = Add<Button>(this, 290, 670, 86, 28, "OK");
            var cancel = Add<Button>(this, 384, 670, 86, 28, "キャンセル");
            ok.Click += (s, e) => { Result = Collect(); DialogResult = DialogResult.OK; };
            cancel.DialogResult = DialogResult.Cancel;
            AcceptButton = ok;
            CancelButton = cancel;
        }

        private async Task LoadInputsAsync()
        {
            try
            {
                var names = await _getInputs();
                var cur = _mic.Text;
                _mic.Items.Clear();
                foreach (var n in names) _mic.Items.Add(n);
                _mic.Text = cur;
            }
            catch { }
        }

        private MonitorSettings Collect()
        {
            var s = new MonitorSettings
            {
                Host = _host.Text.Trim().Length > 0 ? _host.Text.Trim() : "localhost",
                Port = (int)_port.Value,
                MicInput = _mic.Text.Trim(),
                DropWarnPercent = (double)_drop.Value,
                BitrateWarnPercent = (int)_bitratePct.Value,
                TargetBitrateKbps = (int)_target.Value,
                MicSilentSec = (int)_silent.Value,
                RequiredFilters = _filters.Text.Trim(),
                CheckReplayBuffer = _replay.Checked,
                AccountConnected = _target2.SelectedIndex == 1,
                CpuWarnPercent = (int)_cpu.Value,
                DiskWarnGb = (int)_disk.Value,
                PlaySound = _sound.Checked,
                WindowsNotify = _toast.Checked,
                DiscordWebhook = _webhook.Text.Trim(),
                AlwaysOnTop = _src.AlwaysOnTop,
                OpenAfterLaunch = _openAfter.Checked,
            };
            s.Password = _password.Text;
            return s;
        }
    }
}
