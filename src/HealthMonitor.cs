using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StreamLauncher
{
    internal enum HealthLevel { Ok, Warn, Critical }

    internal sealed class Problem
    {
        public string Key;
        public HealthLevel Level;
        public string Message;
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
        public double NetDropPct;
        public double EncodeLagPct;
        public double RenderLagPct;
        public double CpuPct;
        public double DiskGb = -1;
        public bool Recording;
        public double RecordSec;
        public string MicName;
        public bool MicMuted;
        public double MicDb = -100;
        public double MicSilentSec;
        /// <summary>レート計算に使えるだけのデータ(4 秒分以上)がたまったか。</summary>
        public bool HasRates;
        public List<Problem> Problems = new List<Problem>();
    }

    /// <summary>
    /// obs-websocket の統計を定期的に集め、直近 10 秒の変化から問題を判定する。
    /// </summary>
    internal sealed class HealthMonitor
    {
        public const double MicActiveDb = -45; // これより大きい音が入っていれば「音あり」

        private sealed class Sample
        {
            public DateTime T;
            public double StreamBytes, StreamSkipped, StreamTotal;
            public double EncSkipped, EncTotal, RenderSkipped, RenderTotal, Cpu;
        }

        private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

        private readonly ObsClient _obs;
        private readonly Queue<Sample> _samples = new Queue<Sample>();
        private readonly object _micLock = new object();
        private MonitorSettings _s;
        private string _micName;
        private bool _micMuted;
        private double _micDb = -100;
        private DateTime _lastMicActive = DateTime.UtcNow;
        private bool _wasStreaming;

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
            _micName = null; // 次の接続確認でマイクを選び直す
        }

        /// <summary>直近 seconds 秒以内にマイクに音が入ったか。</summary>
        public bool MicActiveWithin(TimeSpan span)
        {
            lock (_micLock) return DateTime.UtcNow - _lastMicActive <= span;
        }

        public DateTime LastMicActive { get { lock (_micLock) return _lastMicActive; } }

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
                    lock (_micLock)
                    {
                        _micDb = db;
                        if (db > MicActiveDb) _lastMicActive = DateTime.UtcNow;
                    }
                }
            }
            else if (type == "InputMuteStateChanged" && J.S(data, "inputName") == mic)
            {
                _micMuted = J.B(data, "inputMuted");
            }
        }

        public async Task<HealthSnapshot> PollAsync()
        {
            await ResolveMicAsync();
            var stats = await _obs.RequestAsync("GetStats");
            var stream = await _obs.RequestAsync("GetStreamStatus");
            var rec = await _obs.RequestAsync("GetRecordStatus");
            if (_micName != null)
            {
                try { _micMuted = J.B(await _obs.RequestAsync("GetInputMute", new Dictionary<string, object> { ["inputName"] = _micName }), "inputMuted"); }
                catch (ObsRequestException) { _micName = null; } // 入力の名前が変わった → 次回選び直す
            }

            var now = DateTime.UtcNow;
            var cur = new Sample
            {
                T = now,
                StreamBytes = J.N(stream, "outputBytes"),
                StreamSkipped = J.N(stream, "outputSkippedFrames"),
                StreamTotal = J.N(stream, "outputTotalFrames"),
                EncSkipped = J.N(stats, "outputSkippedFrames"),
                EncTotal = J.N(stats, "outputTotalFrames"),
                RenderSkipped = J.N(stats, "renderSkippedFrames"),
                RenderTotal = J.N(stats, "renderTotalFrames"),
                Cpu = J.N(stats, "cpuUsage"),
            };
            // 配信をやり直すとカウンタが 0 に戻るので、そのときは集計し直す
            if (_samples.Count > 0 && cur.StreamBytes < _samples.Last().StreamBytes) _samples.Clear();
            _samples.Enqueue(cur);
            while (_samples.Count > 1 && now - _samples.Peek().T > Window) _samples.Dequeue();
            var first = _samples.Peek();
            var dt = (now - first.T).TotalSeconds;

            var snap = new HealthSnapshot
            {
                Connected = true,
                Streaming = J.B(stream, "outputActive"),
                Reconnecting = J.B(stream, "outputReconnecting"),
                StreamSec = J.N(stream, "outputDuration") / 1000.0,
                Recording = J.B(rec, "outputActive"),
                RecordSec = J.N(rec, "outputDuration") / 1000.0,
                Kbps = dt > 0 ? (cur.StreamBytes - first.StreamBytes) * 8 / 1000.0 / dt : 0,
                NetDropPct = Ratio(cur.StreamSkipped - first.StreamSkipped, cur.StreamTotal - first.StreamTotal),
                EncodeLagPct = Ratio(cur.EncSkipped - first.EncSkipped, cur.EncTotal - first.EncTotal),
                RenderLagPct = Ratio(cur.RenderSkipped - first.RenderSkipped, cur.RenderTotal - first.RenderTotal),
                CpuPct = _samples.Average(x => x.Cpu),
                DiskGb = J.N(stats, "availableDiskSpace", -1024) / 1024.0,
                MicName = _micName,
                MicMuted = _micMuted,
                HasRates = dt >= 4, // 開いた直後(サンプル 1 つ)だとビットレートが 0 に見えるため
            };
            lock (_micLock)
            {
                snap.MicDb = _micDb;
                snap.MicSilentSec = (now - _lastMicActive).TotalSeconds;
            }
            // 配信が始まった瞬間から無音を数える
            if (snap.Streaming && !_wasStreaming) lock (_micLock) { _lastMicActive = now; snap.MicSilentSec = 0; }
            _wasStreaming = snap.Streaming;

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
                    if (s.NetDropPct >= _s.DropWarnPercent * 5)
                        Add("netdrop", HealthLevel.Critical, $"回線が不安定です(フレームの {s.NetDropPct:0.0}% が送れていません)");
                    else if (s.NetDropPct >= _s.DropWarnPercent)
                        Add("netdrop", HealthLevel.Warn, $"回線が不安定です(フレームの {s.NetDropPct:0.0}% が送れていません)");
                    if (s.Kbps < _s.MinBitrateKbps)
                        Add("bitrate", HealthLevel.Warn, $"ビットレートが下がっています({s.Kbps:0} kbps)");
                }
                if (s.MicName != null)
                {
                    if (s.MicMuted)
                        Add("micmute", HealthLevel.Warn, $"マイク({s.MicName})がミュートされています");
                    else if (s.MicSilentSec >= _s.MicSilentSec)
                        Add("micsilent", HealthLevel.Warn, $"マイク({s.MicName})の音が {s.MicSilentSec:0} 秒入っていません");
                }
            }
            if ((s.Streaming || s.Recording) && s.HasRates)
            {
                if (s.EncodeLagPct >= _s.DropWarnPercent)
                    Add("encode", HealthLevel.Warn, $"エンコードが追いついていません({s.EncodeLagPct:0.0}%)。出力設定を軽くしてください");
                if (s.RenderLagPct >= _s.DropWarnPercent)
                    Add("render", HealthLevel.Warn, $"描画が追いついていません({s.RenderLagPct:0.0}%)。GPU の負荷が高い可能性があります");
                if (s.CpuPct >= _s.CpuWarnPercent)
                    Add("cpu", HealthLevel.Warn, $"OBS の CPU 使用率が高いです({s.CpuPct:0}%)");
            }
            if (s.Recording && s.DiskGb >= 0 && s.DiskGb < _s.DiskWarnGb)
                Add("disk", HealthLevel.Warn, $"録画先の空き容量が少なくなっています(残り {s.DiskGb:0.0} GB)");
        }
    }
}
