using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace StreamLauncher
{
    /// <summary>OBS / CastCraft の場所探しとプロセス操作。インストール先は人によって違う前提。</summary>
    internal static class AppLocator
    {
        public static readonly string[] ObsExeNames = { "obs64.exe", "obs32.exe", "obs.exe" };
        public static readonly string[] CastCraftExeNames = { "CastCraft.exe" };
        public static readonly string[] VSeeFaceExeNames = { "VSeeFace.exe" };

        /// <summary>ポータブル版アプリを探す場所(デスクトップ・ダウンロード・ドキュメント)。</summary>
        public static string[] UserFolders => new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };

        /// <summary>exe のパス、またはフォルダ(中を最大4階層まで探す)から exe を返す。</summary>
        public static string ResolveExe(string path, string[] names)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            path = path.Trim().Trim('"');
            try
            {
                if (File.Exists(path)) return path;
                if (!Directory.Exists(path)) return null;
                foreach (var n in names)
                {
                    var found = SearchFile(path, n, 4);
                    if (found != null) return found;
                }
            }
            catch { }
            return null;
        }

        // 浅い階層から順に探す
        private static string SearchFile(string root, string name, int maxDepth)
        {
            var level = new List<string> { root };
            for (int depth = 0; depth <= maxDepth && level.Count > 0; depth++)
            {
                var next = new List<string>();
                foreach (var dir in level)
                {
                    try
                    {
                        var p = Path.Combine(dir, name);
                        if (File.Exists(p)) return p;
                        if (depth < maxDepth) next.AddRange(Directory.GetDirectories(dir));
                    }
                    catch { }
                }
                level = next;
            }
            return null;
        }

        /// <summary>
        /// 起動中のプロセス → 候補フォルダ → レジストリのインストール情報 → ショートカット
        /// → ユーザーフォルダ内(ポータブル版用) の順で探す。
        /// </summary>
        public static string FindAppFolder(string[] candidates, string registryNamePrefix, string shortcutNamePrefix,
                                           string[] exeNames, string[] searchRoots = null)
        {
            var sources = new List<Func<string>> { () => FindRunningFolder(exeNames) };
            foreach (var c in candidates) { var cc = c; sources.Add(() => cc); }
            if (registryNamePrefix != null) sources.Add(() => FindInstallLocation(registryNamePrefix));
            if (shortcutNamePrefix != null) sources.Add(() => FindShortcutTarget(shortcutNamePrefix));
            foreach (var root in searchRoots ?? new string[0])
            {
                var r = root;
                foreach (var n in exeNames)
                {
                    var nn = n;
                    sources.Add(() => DirOf(SearchFile(r, nn, 3)));
                }
            }
            foreach (var src in sources)
            {
                string dir = null;
                try { dir = src(); } catch { }
                if (!string.IsNullOrEmpty(dir) && ResolveExe(dir, exeNames) != null) return dir;
            }
            return "";
        }

        /// <summary>HKCU\...\Run に登録された exe のフォルダ(「Windows 起動時に開始」を使っているアプリ用)。</summary>
        public static string FindRunEntryFolder(string valueName)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    if (!(k?.GetValue(valueName) is string v) || v.Length == 0) return null;
                    v = v.Trim();
                    var exe = v.StartsWith("\"") ? v.Substring(1, Math.Max(0, v.IndexOf('"', 1) - 1)) : v.Split(' ')[0];
                    return Path.GetDirectoryName(exe);
                }
            }
            catch { return null; }
        }

        private static string FindRunningFolder(string[] exeNames)
        {
            foreach (var n in exeNames)
            {
                var ps = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(n));
                try
                {
                    foreach (var p in ps)
                    {
                        try
                        {
                            var f = p.MainModule?.FileName;
                            if (!string.IsNullOrEmpty(f)) return Path.GetDirectoryName(f);
                        }
                        catch { }
                    }
                }
                finally { foreach (var p in ps) p.Dispose(); }
            }
            return null;
        }

        private static string FindInstallLocation(string displayNamePrefix)
        {
            var hives = new[]
            {
                (RegistryHive.LocalMachine, RegistryView.Registry64),
                (RegistryHive.LocalMachine, RegistryView.Registry32),
                (RegistryHive.CurrentUser, RegistryView.Registry64),
            };
            foreach (var (hive, view) in hives)
            {
                try
                {
                    using (var baseKey = RegistryKey.OpenBaseKey(hive, view))
                    using (var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                    {
                        if (uninstall == null) continue;
                        foreach (var sub in uninstall.GetSubKeyNames())
                        {
                            using (var k = uninstall.OpenSubKey(sub))
                            {
                                var name = k?.GetValue("DisplayName") as string;
                                if (name == null || !name.StartsWith(displayNamePrefix, StringComparison.OrdinalIgnoreCase)) continue;

                                var loc = Unquote(k.GetValue("InstallLocation") as string);
                                if (string.IsNullOrEmpty(loc) && k.GetValue("DisplayIcon") is string icon)
                                    loc = DirOf(Unquote(icon.Split(',')[0]));
                                if (string.IsNullOrEmpty(loc) && k.GetValue("UninstallString") is string un)
                                    loc = DirOf(FirstToken(un));
                                if (!string.IsNullOrEmpty(loc) && Directory.Exists(loc)) return loc;
                            }
                        }
                    }
                }
                catch { }
            }
            return null;
        }

        private static string FindShortcutTarget(string namePrefix)
        {
            var dirs = new[]
            {
                (Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), true),
                (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), true),
                (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), false),
                (Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), false),
            };
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return null;
            object shell = Activator.CreateInstance(shellType);
            foreach (var (dir, recurse) in dirs)
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                IEnumerable<string> links;
                try
                {
                    links = Directory.GetFiles(dir, namePrefix + "*.lnk",
                        recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
                }
                catch { continue; }
                foreach (var lnk in links)
                {
                    try
                    {
                        object sc = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                        var target = sc.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, sc, null) as string;
                        if (!string.IsNullOrEmpty(target) && File.Exists(target)) return Path.GetDirectoryName(target);
                    }
                    catch { }
                }
            }
            return null;
        }

        private static string Unquote(string s) => s?.Trim().Trim('"');

        private static string FirstToken(string s)
        {
            s = s.Trim();
            if (s.StartsWith("\""))
            {
                int end = s.IndexOf('"', 1);
                return end > 1 ? s.Substring(1, end - 1) : s.Trim('"');
            }
            return s;
        }

        private static string DirOf(string file)
        {
            try { return string.IsNullOrEmpty(file) ? null : Path.GetDirectoryName(file); }
            catch { return null; }
        }

        public static bool IsRunning(string processName)
        {
            if (string.IsNullOrWhiteSpace(processName)) return false;
            var ps = Process.GetProcessesByName(processName.Trim());
            foreach (var p in ps) p.Dispose();
            return ps.Length > 0;
        }

        /// <summary>ウィンドウを持つプロセスに「閉じる」を送る(通常終了)。送れた数を返す。</summary>
        public static int RequestClose(string processName)
        {
            int sent = 0;
            foreach (var p in Process.GetProcessesByName(processName))
            {
                using (p)
                {
                    try { if (p.MainWindowHandle != IntPtr.Zero && p.CloseMainWindow()) sent++; } catch { }
                }
            }
            return sent;
        }

        /// <summary>同名プロセスをすべて強制終了。</summary>
        public static void Kill(string processName)
        {
            foreach (var p in Process.GetProcessesByName(processName))
            {
                using (p)
                {
                    try { p.Kill(); } catch { }
                }
            }
        }

        public static void Start(string exe, string args)
        {
            var psi = new ProcessStartInfo(exe)
            {
                WorkingDirectory = Path.GetDirectoryName(exe), // OBS は作業フォルダが bin でないと起動に失敗する
                UseShellExecute = true,
            };
            if (!string.IsNullOrWhiteSpace(args)) psi.Arguments = args.Trim();
            Process.Start(psi)?.Dispose();
        }
    }
}
