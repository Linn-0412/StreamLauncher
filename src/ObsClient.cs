using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace StreamLauncher
{
    /// <summary>JSON(Dictionary/object[])から値を取り出す小さなヘルパー。</summary>
    internal static class J
    {
        public static Dictionary<string, object> D(object o) => o as Dictionary<string, object> ?? new Dictionary<string, object>();

        public static object Get(Dictionary<string, object> d, string key) =>
            d != null && d.TryGetValue(key, out var v) ? v : null;

        public static double N(Dictionary<string, object> d, string key, double def = 0)
        {
            var v = Get(d, key);
            try { return v == null ? def : Convert.ToDouble(v, CultureInfo.InvariantCulture); }
            catch { return def; }
        }

        public static bool B(Dictionary<string, object> d, string key) => Get(d, key) is bool b && b;

        public static string S(Dictionary<string, object> d, string key) => Get(d, key) as string ?? "";

        public static object[] A(Dictionary<string, object> d, string key)
        {
            var v = Get(d, key);
            if (v is object[] arr) return arr;
            if (v is ArrayList list) return list.ToArray();
            return new object[0];
        }
    }

    /// <summary>OBS が保存している obs-websocket の設定(同じ PC ならパスワードを入力しなくて済む)。</summary>
    internal sealed class ObsLocalConfig
    {
        public bool Found;
        public bool Enabled;
        public int Port = 4455;
        public bool AuthRequired;
        public string Password = "";

        public static ObsLocalConfig Read()
        {
            var cfg = new ObsLocalConfig();
            try
            {
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"obs-studio\plugin_config\obs-websocket\config.json");
                if (!File.Exists(path)) return cfg;
                var d = J.D(new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path, Encoding.UTF8)));
                cfg.Found = true;
                cfg.Enabled = J.B(d, "server_enabled");
                cfg.Port = (int)J.N(d, "server_port", 4455);
                cfg.AuthRequired = J.B(d, "auth_required");
                cfg.Password = J.S(d, "server_password");
            }
            catch { }
            return cfg;
        }
    }

    internal sealed class ObsRequestException : Exception
    {
        public ObsRequestException(string message) : base(message) { }
    }

    /// <summary>obs-websocket v5 の最小クライアント(リクエスト/レスポンスとイベント受信)。</summary>
    internal sealed class ObsClient : IDisposable
    {
        // イベント購読: Inputs(8) | Outputs(64) | InputVolumeMeters(1<<16, 高頻度)
        public const int DefaultEventSubscriptions = 8 | 64 | (1 << 16);

        private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private readonly ConcurrentDictionary<string, TaskCompletionSource<Dictionary<string, object>>> _pending =
            new ConcurrentDictionary<string, TaskCompletionSource<Dictionary<string, object>>>();
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private ClientWebSocket _ws;
        private CancellationTokenSource _cts;
        private volatile bool _identified;

        /// <summary>イベント(eventType, eventData)。受信スレッドから呼ばれる。</summary>
        public event Action<string, Dictionary<string, object>> EventReceived;

        /// <summary>接続が切れたとき(理由)。受信スレッドから呼ばれる。</summary>
        public event Action<string> Disconnected;

        public bool IsConnected => _identified && _ws != null && _ws.State == WebSocketState.Open;

        public async Task ConnectAsync(string host, int port, string password, int eventSubscriptions, TimeSpan timeout)
        {
            Close();
            _ws = new ClientWebSocket();
            _cts = new CancellationTokenSource();
            using (var connectCts = new CancellationTokenSource(timeout))
            {
                await _ws.ConnectAsync(new Uri($"ws://{host}:{port}"), connectCts.Token).ConfigureAwait(false);

                var hello = await ReceiveAsync(connectCts.Token).ConfigureAwait(false);
                if ((int)J.N(hello, "op", -1) != 0) throw new IOException("OBS からの応答が想定と違います");
                var helloData = J.D(J.Get(hello, "d"));

                var identify = new Dictionary<string, object>
                {
                    ["rpcVersion"] = 1,
                    ["eventSubscriptions"] = eventSubscriptions,
                };
                if (J.Get(helloData, "authentication") is Dictionary<string, object> auth)
                {
                    if (string.IsNullOrEmpty(password))
                        throw new UnauthorizedAccessException("OBS の WebSocket にパスワードが設定されています。モニターの設定でパスワードを入力してください");
                    identify["authentication"] = MakeAuth(password, J.S(auth, "salt"), J.S(auth, "challenge"));
                }
                await SendAsync(new Dictionary<string, object> { ["op"] = 1, ["d"] = identify }).ConfigureAwait(false);

                var identified = await ReceiveAsync(connectCts.Token).ConfigureAwait(false);
                if ((int)J.N(identified, "op", -1) != 2) throw new IOException("OBS への接続を確立できませんでした");
            }
            _identified = true;
            var token = _cts.Token;
            _ = Task.Run(() => ReceiveLoopAsync(token));
        }

        private static string MakeAuth(string password, string salt, string challenge)
        {
            using (var sha = SHA256.Create())
            {
                var secret = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(password + salt)));
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(secret + challenge)));
            }
        }

        public async Task<Dictionary<string, object>> RequestAsync(string requestType, Dictionary<string, object> data = null, int timeoutMs = 5000)
        {
            if (!IsConnected) throw new InvalidOperationException("OBS に接続していません");
            var id = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<Dictionary<string, object>>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
            var d = new Dictionary<string, object> { ["requestType"] = requestType, ["requestId"] = id };
            if (data != null) d["requestData"] = data;
            await SendAsync(new Dictionary<string, object> { ["op"] = 6, ["d"] = d }).ConfigureAwait(false);

            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
            if (done != tcs.Task)
            {
                _pending.TryRemove(id, out _);
                throw new TimeoutException($"OBS からの応答がありません({requestType})");
            }
            return await tcs.Task.ConfigureAwait(false);
        }

        private async Task SendAsync(Dictionary<string, object> message)
        {
            var bytes = Encoding.UTF8.GetBytes(_json.Serialize(message));
            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
            }
            finally { _sendLock.Release(); }
        }

        private async Task<Dictionary<string, object>> ReceiveAsync(CancellationToken ct)
        {
            var buffer = new byte[16 * 1024];
            using (var ms = new MemoryStream())
            {
                while (true)
                {
                    var r = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        var reason = _ws.CloseStatusDescription;
                        if ((int?)_ws.CloseStatus == 4009) reason = "パスワードが違います";
                        throw new IOException(string.IsNullOrEmpty(reason) ? "OBS が接続を閉じました" : reason);
                    }
                    ms.Write(buffer, 0, r.Count);
                    if (r.EndOfMessage) break;
                }
                return J.D(_json.DeserializeObject(Encoding.UTF8.GetString(ms.ToArray())));
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            string reason = "接続が切れました";
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var msg = await ReceiveAsync(ct).ConfigureAwait(false);
                    var op = (int)J.N(msg, "op", -1);
                    var d = J.D(J.Get(msg, "d"));
                    if (op == 5)
                    {
                        try { EventReceived?.Invoke(J.S(d, "eventType"), J.D(J.Get(d, "eventData"))); } catch { }
                    }
                    else if (op == 7 && _pending.TryRemove(J.S(d, "requestId"), out var tcs))
                    {
                        var status = J.D(J.Get(d, "requestStatus"));
                        if (J.B(status, "result")) tcs.TrySetResult(J.D(J.Get(d, "responseData")));
                        else tcs.TrySetException(new ObsRequestException(J.S(status, "comment") is string c && c.Length > 0 ? c : $"OBS エラー {J.N(status, "code")}"));
                    }
                }
            }
            catch (Exception ex) { reason = ex.Message; }
            finally
            {
                _identified = false;
                foreach (var kv in _pending)
                    if (_pending.TryRemove(kv.Key, out var t)) t.TrySetException(new IOException(reason));
                if (!ct.IsCancellationRequested) Disconnected?.Invoke(reason);
            }
        }

        public void Close()
        {
            _identified = false;
            try { _cts?.Cancel(); } catch { }
            try { _ws?.Abort(); } catch { }
            try { _ws?.Dispose(); } catch { }
            _ws = null;
        }

        public void Dispose() => Close();
    }
}
