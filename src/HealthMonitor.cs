using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace StreamLauncher
{
    internal enum HealthLevel { Ok, Warn, Critical }

    internal sealed class Problem
    {
        public string Key;
        public HealthLevel Level;
        public string Message;
    }

    /// <summary>必ず有効にしておくフィルターの状態。</summary>
    internal sealed class FilterState
    {
        public string Source, Filter;
        public bool? Enabled; // null = 見つからない
    }

    /// <summary>ある時点の配信の状態と、検出した問題。</summary>
    internal sealed class HealthSnapshot
    {
        public bool Connected;
        public string DisconnectReason;
        public bool Streaming;
        public bool Reconnecting;
        public double StreamSec;
        public double Kbps;
        public double TargetKbps;      // 0 = 不明
        public bool TargetIsObserved;  // 設定値が読めず、実測の最大値を基準にしている
        public double NetDropPct;
        public double Congestion;
        public double EncodeSkippedRecent; // 直近 10 秒に増えたフレーム数
        public double RenderSkippedRecent;
        public double CpuPct;
        public double MemoryMb;
        public double DiskGb = -1;
        public bool Recording;
        public double RecordSec;
        public string MicName;
        public bool MicMuted;
        public double MicDb = -100;
        public double MicSilentSec;
        public bool MicClipping;
        public List<FilterState> Filters = new List<FilterState>();
        public string ReplayBuffer;    // null = 確認しない / "動作中" / "停止中" / "使えません"
        public bool HasRates;          // レート計算に使えるだけのデータ(4 秒分以上)がたまったか
        public List<Problem> Problems = new List<Problem>();
    }

    /// <summary>
    /// obs-websocket の統計を定期的に集め、直近の変化から問題を判定する。
    /// </summary>
    internal sealed class HealthMonitor
    {
        public const double MicActiveDb = -45; // これより大きい音が入っていれば「音あり」
        public const double MicClipDb = -1;    // これを超えたら音割れ

        private sealed class Sample
        {
            public DateTime T;
            public double StreamBytes, StreamSkipped, StreamTotal;
            public double EncSkipped, RenderSkipped, Cpu;
        }

        private static readonly TimeSpan Window = TimeSpan.FromSeconds(30);  // ドロップ率・ビットレート
        private static readonly TimeSpan Recent = TimeSpan.FromSeconds(10);  // CPU・遅延の「増え始め」
        private static readonly TimeSpan ClipHold = TimeSpan.FromSeconds(10);

        private readonly ObsClient _obs;
        private readonly Queue<Sample> _samples = new Queue<Sample>();
        private readonly object _micLock = new object();
        private MonitorSettings _s;
        private string _micName;
        private bool _micMuted;
        private double _micDb = -100;
        private DateTime _lastMicActive = DateTime.UtcNow;
        private DateTime _lastClip = DateTime.MinValue;
        private bool _wasStreaming;

        private double _configuredKbps;
        private DateTime _targetResolvedAt = DateTime.MinValue;
        private double _observedMaxKbps;
        private List<FilterState> _filters = new List<FilterState>();
        private DateTime _filtersCheckedAt = DateTime.MinValue;

        public HealthMonitor(ObsClient obs, MonitorSettings settings)
        {
            _obs = obs;
            _s = settings;
            _obs.EventReceived += OnEvent;
        }

        public string MicName => _micName;

        public void UpdateSettings(MonitorSettings s)
        {
            _s = s;
            _micName = null;                         // マイクを選び直す
            _targetResolvedAt = DateTime.MinValue;   // ビットレートの基準を読み直す
            _filtersCheckedAt = DateTime.MinValue;
        }

        public DateTime LastMicActive { get { lock (_micLock) return _lastMicActive; } }

        // ------------------------------------------------------------ マイク

        /// <summary>見張るマイクを決める(設定がなければ最初のマイク入力)。</summary>
        public async Task ResolveMicAsync()
        {
            if (_micName != null) return;
            if (!string.IsNullOrWhiteSpace(_s.MicInput)) { _micName = _s.MicInput.Trim(); }
            else
            {
                var list = await _obs.RequestAsync("GetInputList");
                foreach (var o in J.A(list, "inputs"))
                {
                    var inp = J.D(o);
                    if (J.S(inp, "inputKind").StartsWith("wasapi_input_capture", StringComparison.Ordinal))
                    {
                        _micName = J.S(inp, "inputName");
                        break;
                    }
                }
            }
            lock (_micLock) _lastMicActive = DateTime.UtcNow;
        }

        public async Task<List<string>> GetInputNamesAsync()
        {
            var list = await _obs.RequestAsync("GetInputList");
            return J.A(list, "inputs").Select(o => J.S(J.D(o), "inputName")).Where(n => n.Length > 0).ToList();
        }

        private void OnEvent(string type, Dictionary<string, object> data)
        {
            var mic = _micName;
            if (mic == null) return;
            if (type == "InputVolumeMeters")
            {
                foreach (var o in J.A(data, "inputs"))
                {
                    var inp = J.D(o);
                    if (J.S(inp, "inputName") != mic) continue;
                    double peak = 0;
                    foreach (var ch in J.A(inp, "inputLevelsMul"))
                    {
                        // [magnitude, peak, inputPeak](0〜1 の倍率)
                        if (ch is object[] v && v.Length >= 2)
                            peak = Math.Max(peak, Convert.ToDouble(v[1], System.Globalization.CultureInfo.InvariantCulture));
                    }
                    var db = peak > 0 ? 20 * Math.Log10(peak) : -100;
                    var now = DateTime.UtcNow;
                    lock (_micLock)
                    {
                        _micDb = db;
                        if (db > MicActiveDb) _lastMicActive = now;
                        if (db > MicClipDb) _lastClip = now;
                    }
                }
            }
            else if (type == "InputMuteStateChanged" && J.S(data, "inputName") == mic)
            {
                _micMuted = J.B(data, "inputMuted");
            }
        }

        // ------------------------------------------------------------ ビットレートの基準

        /// <summary>OBS の出力設定から配信ビットレート(映像+音声, kbps)を読む。読めなければ 0。</summary>
        public async Task<double> ReadConfiguredKbpsAsync()
        {
            if (_s.TargetBitrateKbps > 0) return _s.TargetBitrateKbps;
            try
            {
                async Task<string> Param(string cat, string name) =>
                    J.S(await _obs.RequestAsync("GetProfileParameter", new Dictionary<string, object>
                    { ["parameterCategory"] = cat, ["parameterName"] = name }), "parameterValue");

                var mode = await Param("Output", "Mode");
                if (mode == "Simple")
                {
                    double.TryParse(await Param("SimpleOutput", "VBitrate"), out var v);
                    double.TryParse(await Param("SimpleOutput", "ABitrate"), out var a);
                    return v > 0 ? v + a : 0;
                }
                // 詳細モードのエンコーダ設定は websocket で取れないので、同じ PC ならプロファイルのファイルを読む
                if (!_s.IsLocal) return 0;
                var profile = J.S(await _obs.RequestAsync("GetProfileList"), "currentProfileName");
                var dir = FindProfileDir(profile);
                if (dir == null) return 0;
                var enc = J.D(new JavaScriptSerializer().DeserializeObject(File.ReadAllText(Path.Combine(dir, "streamEncoder.json"), Encoding.UTF8)));
                var rc = J.S(enc, "rate_control");
                if (rc.Length > 0 && rc != "CBR" && rc != "VBR" && rc != "ABR") return 0; // CQP/CRF などは固定値がない
                var video = J.N(enc, "bitrate");
                double.TryParse(await Param("AdvOut", "Track1Bitrate"), out var audio);
                return video > 0 ? video + audio : 0;
            }
            catch { return 0; }
        }

        private static string FindProfileDir(string profileName)
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"obs-studio\basic\profiles");
            if (!Directory.Exists(root)) return null;
            foreach (var d in Directory.GetDirectories(root))
            {
                var ini = Path.Combine(d, "basic.ini");
                if (!File.Exists(ini)) continue;
                var nameLine = File.ReadLines(ini, Encoding.UTF8).FirstOrDefault(l => l.StartsWith("Name=", StringComparison.Ordinal));
                if (nameLine != null && nameLine.Substring(5).Trim() == profileName && File.Exists(Path.Combine(d, "streamEncoder.json")))
                    return d;
            }
            return null;
        }

        // ------------------------------------------------------------ フィルター・リプレイバッファ

        /// <summary>設定の「ソース名:フィルター名」を読み取る。</summary>
        public List<(string Source, string Filter)> RequiredFilters() =>
            (_s.RequiredFilters ?? "").Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim()).Where(l => l.Length > 0)
                .Select(l => { int i = l.IndexOf(':'); if (i < 0) i = l.IndexOf('：'); return i > 0 ? (l.Substring(0, i).Trim(), l.Substring(i + 1).Trim()) : (null, null); })
                .Where(p => p.Item1 != null && p.Item2.Length > 0).ToList();

        public async Task<List<FilterState>> CheckFiltersAsync()
        {
            var result = new List<FilterState>();
            foreach (var (src, flt) in RequiredFilters())
            {
                var st = new FilterState { Source = src, Filter = flt };
                try
                {
                    st.Enabled = J.B(await _obs.RequestAsync("GetSourceFilter", new Dictionary<string, object>
                    { ["sourceName"] = src, ["filterName"] = flt }), "filterEnabled");
                }
                catch (ObsRequestException) { st.Enabled = null; }
                result.Add(st);
            }
            return result;
        }

        public async Task<string> CheckReplayBufferAsync()
        {
            try { return J.B(await _obs.RequestAsync("GetReplayBufferStatus"), "outputActive") ? "動作中" : "停止中"; }
            catch (ObsRequestException) { return "使えません"; }
        }

        // ------------------------------------------------------------ 定期チェック

        public async Task<HealthSnapshot> PollAsync()
        {
            await ResolveMicAsync();
            var now = DateTime.UtcNow;
            if (now - _targetResolvedAt > TimeSpan.FromMinutes(1))
            {
                _configuredKbps = await ReadConfiguredKbpsAsync();
                _targetResolvedAt = now;
            }
            var stats = await _obs.RequestAsync("GetStats");
            var stream = await _obs.RequestAsync("GetStreamStatus");
            var rec = await _obs.RequestAsync("GetRecordStatus");
            if (_micName != null)
            {
                try { _micMuted = J.B(await _obs.RequestAsync("GetInputMute", new Dictionary<string, object> { ["inputName"] = _micName }), "inputMuted"); }
                catch (ObsRequestException) { _micName = null; } // 入力の名前が変わった → 次回選び直す
            }
            bool streaming = J.B(stream, "outputActive");
            if (now - _filtersCheckedAt > TimeSpan.FromSeconds(10)) // フィルターは 10 秒ごとで十分
            {
                _filters = await CheckFiltersAsync();
                _filtersCheckedAt = now;
            }
            string replay = _s.CheckReplayBuffer ? await CheckReplayBufferAsync() : null;

            var cur = new Sample
            {
                T = now,
                StreamBytes = J.N(stream, "outputBytes"),
                StreamSkipped = J.N(stream, "outputSkippedFrames"),
                StreamTotal = J.N(stream, "outputTotalFrames"),
                EncSkipped = J.N(stats, "outputSkippedFrames"),
                RenderSkipped = J.N(stats, "renderSkippedFrames"),
                Cpu = J.N(stats, "cpuUsage"),
            };
            // 配信・録画をやり直すとカウンタが 0 に戻るので、そのときは集計し直す
            if (_samples.Count > 0)
            {
                var last = _samples.Last();
                if (cur.StreamBytes < last.StreamBytes || cur.EncSkipped < last.EncSkipped || cur.RenderSkipped < last.RenderSkipped) _samples.Clear();
            }
            _samples.Enqueue(cur);
            while (_samples.Count > 1 && now - _samples.Peek().T > Window) _samples.Dequeue();
            var first = _samples.Peek();
            var recentFirst = _samples.FirstOrDefault(x => now - x.T <= Recent) ?? cur;
            var dt = (now - first.T).TotalSeconds;

            var snap = new HealthSnapshot
            {
                Connected = true,
                Streaming = streaming,
                Reconnecting = J.B(stream, "outputReconnecting"),
                StreamSec = J.N(stream, "outputDuration") / 1000.0,
                Recording = J.B(rec, "outputActive"),
                RecordSec = J.N(rec, "outputDuration") / 1000.0,
                Kbps = dt > 0 ? (cur.StreamBytes - first.StreamBytes) * 8 / 1000.0 / dt : 0,
                NetDropPct = Ratio(cur.StreamSkipped - first.StreamSkipped, cur.StreamTotal - first.StreamTotal),
                Congestion = J.N(stream, "outputCongestion"),
                EncodeSkippedRecent = cur.EncSkipped - recentFirst.EncSkipped,
                RenderSkippedRecent = cur.RenderSkipped - recentFirst.RenderSkipped,
                CpuPct = _samples.Where(x => now - x.T <= Recent).Average(x => x.Cpu),
                MemoryMb = J.N(stats, "memoryUsage"),
                DiskGb = J.N(stats, "availableDiskSpace", -1024) / 1024.0,
                MicName = _micName,
                MicMuted = _micMuted,
                Filters = _filters,
                ReplayBuffer = replay,
                HasRates = dt >= 4, // 開いた直後(サンプル 1 つ)だとビットレートが 0 に見えるため
            };
            lock (_micLock)
            {
                snap.MicDb = _micDb;
                snap.MicSilentSec = (now - _lastMicActive).TotalSeconds;
                snap.MicClipping = now - _lastClip <= ClipHold;
            }
            // 配信が始まった瞬間から無音を数える
            if (snap.Streaming && !_wasStreaming)
            {
                lock (_micLock) { _lastMicActive = now; snap.MicSilentSec = 0; }
                _observedMaxKbps = 0;
            }
            _wasStreaming = snap.Streaming;

            // ビットレートの基準: 設定値。読めなければ、この配信で 30 秒平均が一番高かった値
            if (snap.Streaming && snap.StreamSec >= 15 && dt >= Window.TotalSeconds * 0.8)
                _observedMaxKbps = Math.Max(_observedMaxKbps, snap.Kbps);
            snap.TargetKbps = _configuredKbps > 0 ? _configuredKbps : _observedMaxKbps;
            snap.TargetIsObserved = _configuredKbps <= 0;

            Evaluate(snap);
            return snap;
        }

        /// <summary>OBS に接続できないときの状態。配信中に切れたなら重大な問題として扱う。</summary>
        public HealthSnapshot DisconnectedSnapshot(string reason)
        {
            var snap = new HealthSnapshot { Connected = false, DisconnectReason = reason };
            if (_wasStreaming)
                snap.Problems.Add(new Problem { Key = "obs-lost", Level = HealthLevel.Critical, Message = "配信中に OBS との接続が切れました(OBS が落ちた可能性があります)" });
            _samples.Clear();
            return snap;
        }

        private static double Ratio(double part, double total) => total > 0 ? part / total * 100 : 0;

        private void Evaluate(HealthSnapshot s)
        {
            var p = s.Problems;
            void Add(string key, HealthLevel level, string msg) => p.Add(new Problem { Key = key, Level = level, Message = msg });

            if (s.Streaming)
            {
                if (s.Reconnecting)
                    Add("reconnect", HealthLevel.Critical, "配信が再接続中です(回線が切れています)");
                if (s.StreamSec >= 15 && s.HasRates && !s.Reconnecting) // 開始直後・モニターを開いた直後は値が安定しないので待つ
                {
                    if (s.NetDropPct >= _s.DropWarnPercent)
                        Add("netdrop", s.NetDropPct >= _s.DropWarnPercent * 5 ? HealthLevel.Critical : HealthLevel.Warn,
                            $"回線が不安定です(直近 30 秒でフレームの {s.NetDropPct:0.0}% が送れていません)");
                    if (s.TargetKbps > 0 && s.Kbps < s.TargetKbps * _s.BitrateWarnPercent / 100.0)
                        Add("bitrate", HealthLevel.Warn,
                            $"ビットレートが下がっています({s.Kbps:0} kbps / {(s.TargetIsObserved ? "最大" : "設定")} {s.TargetKbps:0} kbps)");
                    if (s.Congestion >= 0.5)
                        Add("congestion", HealthLevel.Warn, $"回線が混雑しています(混雑度 {s.Congestion:0.00})");
                }
                if (s.MicName != null)
                {
                    if (s.MicMuted)
                        Add("micmute", HealthLevel.Warn, $"マイク({s.MicName})がミュートされています");
                    else if (s.MicSilentSec >= _s.MicSilentSec)
                        Add("micsilent", HealthLevel.Warn, $"マイク({s.MicName})の音が {s.MicSilentSec:0} 秒入っていません");
                }
                foreach (var f in s.Filters)
                {
                    if (f.Enabled == false)
                        Add("filter:" + f.Source + ":" + f.Filter, HealthLevel.Warn, $"「{f.Source}」のフィルター「{f.Filter}」が無効になっています");
                    else if (f.Enabled == null)
                        Add("filter:" + f.Source + ":" + f.Filter, HealthLevel.Warn, $"「{f.Source}」にフィルター「{f.Filter}」が見つかりません");
                }
                if (s.ReplayBuffer == "停止中")
                    Add("replay", HealthLevel.Warn, "リプレイバッファが止まっています");
                else if (s.ReplayBuffer == "使えません")
                    Add("replay", HealthLevel.Warn, "リプレイバッファが使えません(OBS の 設定 → 出力 で有効にしてください)");
            }
            if ((s.Streaming || s.Recording) && s.HasRates)
            {
                if (s.EncodeSkippedRecent > 0)
                    Add("encode", HealthLevel.Warn, $"エンコードが追いついていません(直近 10 秒で {s.EncodeSkippedRecent:0} フレーム)。出力設定を軽くしてください");
                if (s.RenderSkippedRecent > 0)
                    Add("render", HealthLevel.Warn, $"描画が追いついていません(直近 10 秒で {s.RenderSkippedRecent:0} フレーム)。GPU の負荷が高い可能性があります");
                if (s.CpuPct >= _s.CpuWarnPercent)
                    Add("cpu", HealthLevel.Warn, $"OBS の CPU 使用率が高いです(直近 10 秒の平均 {s.CpuPct:0}%)");
                if (s.MicClipping && s.MicName != null && !s.MicMuted)
                    Add("clip", HealthLevel.Warn, $"マイク({s.MicName})が音割れしています(ピークが {MicClipDb:0} dB を超えました)");
            }
            if (s.Recording && s.DiskGb >= 0 && s.DiskGb < _s.DiskWarnGb)
                Add("disk", HealthLevel.Warn, $"録画先の空き容量が少なくなっています(残り {s.DiskGb:0.0} GB)");
        }
    }
}
