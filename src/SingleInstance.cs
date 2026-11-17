using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace StreamLauncher
{
    /// <summary>
    /// ランチャーは1つだけ起動する。2つ目(Stream Deck のボタンなどから起動されたもの)は
    /// 起動中のランチャーに名前付きパイプで命令("start" / "closeall" / "show")を渡して終わる。
    /// </summary>
    internal static class SingleInstance
    {
        private static readonly string Id =
            "StreamLauncher_" + (WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName);

        private static Mutex _mutex;

        /// <summary>自分が唯一のランチャーになれたら true。更新直後は旧版の終了を待つ。</summary>
        public static bool TryAcquire(int waitMs)
        {
            _mutex = new Mutex(false, @"Local\" + Id);
            try { return _mutex.WaitOne(waitMs); }
            catch (AbandonedMutexException) { return true; } // 前のランチャーが終了済み
        }

        /// <summary>起動中のランチャーに命令を送る。</summary>
        public static bool Send(string command)
        {
            try
            {
                // 起動中のランチャーが前面に出られるよう許可する(Stream Deck から起動された自分は前面化の権利を持つ)
                using (var self = Process.GetCurrentProcess())
                {
                    foreach (var p in Process.GetProcessesByName(self.ProcessName))
                        using (p) if (p.Id != self.Id) AllowSetForegroundWindow(p.Id);
                }
                using (var c = new NamedPipeClientStream(".", Id, PipeDirection.Out))
                {
                    c.Connect(3000);
                    var bytes = Encoding.UTF8.GetBytes(command);
                    c.Write(bytes, 0, bytes.Length);
                    c.Flush();
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>命令の受信を始める(バックグラウンドスレッドで onCommand が呼ばれる)。</summary>
        public static void Listen(Action<string> onCommand)
        {
            var t = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using (var s = new NamedPipeServerStream(Id, PipeDirection.In, 1))
                        {
                            s.WaitForConnection();
                            using (var r = new StreamReader(s, Encoding.UTF8))
                            {
                                var cmd = r.ReadToEnd().Trim();
                                if (cmd.Length > 0) onCommand(cmd);
                            }
                        }
                    }
                    catch
                    {
                        Thread.Sleep(500);
                    }
                }
            })
            { IsBackground = true, Name = "StreamLauncher command pipe" };
            t.Start();
        }

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(int processId);
    }
}
