using System;
using System.Linq;
using System.Windows.Forms;

namespace StreamLauncher
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool Has(string flag) => args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

            _ = Updater.CleanupOldAsync(Application.ExecutablePath);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(autoRun: Has("--autorun"), justUpdated: Has("--updated")));
        }
    }
}
