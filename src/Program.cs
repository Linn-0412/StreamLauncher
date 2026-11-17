using System;
using System.Linq;
using System.Windows.Forms;

namespace StreamLauncher
{
    internal static class Program
    {
        // コマンドライン(Stream Deck / VSD Craft のボタンから使う)
        //   --start (--autorun)  起動リストを起動
        //   --closeall           全部終了
        //   (なし)               画面を開く
        [STAThread]
        private static void Main(string[] args)
        {
            bool Has(string flag) => args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

            string command = Has("--closeall") ? "closeall"
                           : Has("--start") || Has("--autorun") ? "start"
                           : "show";
            bool justUpdated = Has("--updated");

            // すでにランチャーが開いていれば、そちらに命令を渡して終わる
            if (!SingleInstance.TryAcquire(justUpdated ? 15000 : 0))
            {
                SingleInstance.Send(command);
                return;
            }

            _ = Updater.CleanupOldAsync(Application.ExecutablePath);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(command, justUpdated));
        }
    }
}
