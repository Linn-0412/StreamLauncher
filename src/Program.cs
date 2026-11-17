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
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool autoRun = args.Any(a => string.Equals(a, "--autorun", StringComparison.OrdinalIgnoreCase));
            Application.Run(new MainForm(autoRun));
        }
    }
}
