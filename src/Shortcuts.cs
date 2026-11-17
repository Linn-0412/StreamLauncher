using System;
using System.IO;
using System.Reflection;

namespace StreamLauncher
{
    /// <summary>
    /// スタートメニューへの登録。VSD Craft / Stream Deck の「アプリを開く」一覧は
    /// スタートメニューから作られるので、ここに置くとボタンに割り当てられる。
    /// </summary>
    internal static class Shortcuts
    {
        public static string Folder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs), "StreamLauncher");

        private static readonly (string File, string Args, string Description)[] Items =
        {
            ("StreamLauncher.lnk", "", "配信スタートランチャーを開く"),
            ("StreamLauncher 配信開始.lnk", "--start", "起動リストのアプリをまとめて起動"),
            ("StreamLauncher 全部終了.lnk", "--closeall", "起動したアプリをまとめて終了"),
        };

        public static bool IsRegistered => File.Exists(Path.Combine(Folder, Items[0].File));

        public static void Register(string exePath)
        {
            Directory.CreateDirectory(Folder);
            foreach (var (file, args, desc) in Items)
                Create(Path.Combine(Folder, file), exePath, args, desc);
        }

        public static void Unregister()
        {
            if (Directory.Exists(Folder)) Directory.Delete(Folder, true);
        }

        /// <summary>登録済みで、exe の場所が変わっていたら登録し直す。</summary>
        public static void RefreshIfMoved(string exePath)
        {
            try
            {
                if (!IsRegistered) return;
                var target = GetTarget(Path.Combine(Folder, Items[0].File));
                if (!string.Equals(target, exePath, StringComparison.OrdinalIgnoreCase)) Register(exePath);
            }
            catch { }
        }

        private static object Shell()
        {
            var t = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell を使えません");
            return Activator.CreateInstance(t);
        }

        private static object Open(string lnkPath)
        {
            var shell = Shell();
            return shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
        }

        private static void Set(object sc, string name, object value) =>
            sc.GetType().InvokeMember(name, BindingFlags.SetProperty, null, sc, new[] { value });

        private static void Create(string lnkPath, string exePath, string args, string description)
        {
            var sc = Open(lnkPath);
            Set(sc, "TargetPath", exePath);
            Set(sc, "Arguments", args);
            Set(sc, "WorkingDirectory", Path.GetDirectoryName(exePath));
            Set(sc, "IconLocation", exePath + ",0");
            Set(sc, "Description", description);
            sc.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
        }

        private static string GetTarget(string lnkPath)
        {
            var sc = Open(lnkPath);
            return sc.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, sc, null) as string;
        }
    }
}
