using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;

namespace StreamLauncher
{
    /// <summary>起動リストの1アプリ分。</summary>
    [DataContract]
    public class AppEntry
    {
        [DataMember] public bool Enabled;
        [DataMember] public string Name;
        /// <summary>exe のパス、またはフォルダ(ExeName を中から探す)。</summary>
        [DataMember(Name = "Path")] public string AppPath;
        [DataMember] public string ExeName;
        [DataMember] public string Args;
        [DataMember] public int WaitAfterSec;
        /// <summary>起動後に上から順に押すボタン名。</summary>
        [DataMember] public string[] ClickButtons;
        [DataMember] public int ClickTimeoutSec;
        /// <summary>ボタン操作で立ち上がるプロセス(起動確認・全部終了で一緒に閉じる)。</summary>
        [DataMember] public string RelatedProcess;
        [DataMember] public bool UseMouseClick;
        [DataMember] public bool CloseOnExit;
        [DataMember] public bool ConfirmBeforeKill;

        public AppEntry() { SetDefaults(); }

        [OnDeserializing]
        private void OnDeserializing(StreamingContext _) { SetDefaults(); }

        private void SetDefaults()
        {
            Enabled = true;
            Name = "新しいアプリ";
            AppPath = "";
            ExeName = "";
            Args = "";
            WaitAfterSec = 0;
            ClickButtons = new string[0];
            ClickTimeoutSec = 60;
            RelatedProcess = "";
            UseMouseClick = false;
            CloseOnExit = true;
            ConfirmBeforeKill = false;
        }

        public string ResolveExe() =>
            AppLocator.ResolveExe(AppPath, string.IsNullOrWhiteSpace(ExeName) ? new string[0] : new[] { ExeName.Trim() });

        /// <summary>プロセス名(exe が見つからなくても ExeName から推定)。</summary>
        public string ProcessName
        {
            get
            {
                var exe = ResolveExe() ?? ExeName;
                return string.IsNullOrWhiteSpace(exe) ? null : System.IO.Path.GetFileNameWithoutExtension(exe.Trim());
            }
        }

        public override string ToString() => string.IsNullOrWhiteSpace(Name) ? "(名前なし)" : Name;
    }

    /// <summary>よく使うアプリの初期設定。</summary>
    internal static class Presets
    {
        private static string PF(string sub) =>
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), sub);
        private static string PF86(string sub) =>
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), sub);

        public static AppEntry Obs(bool detect = true)
        {
            var a = new AppEntry
            {
                Name = "OBS",
                ExeName = "obs64.exe",
                WaitAfterSec = 5,
                ConfirmBeforeKill = true, // 強制終了すると録画破損・セーフモード確認が出る
            };
            if (detect)
                a.AppPath = AppLocator.FindAppFolder(new[] { PF("obs-studio"), PF86("obs-studio") },
                    "OBS Studio", "OBS Studio", new[] { a.ExeName });
            return a;
        }

        public static AppEntry CastCraft(bool detect = true)
        {
            var a = new AppEntry
            {
                Name = "CastCraft",
                ExeName = "CastCraft.exe",
                Args = "--force-renderer-accessibility", // ボタンを UI Automation で読めるようにする
                ClickButtons = new[] { "Screen", "起動する" },
                RelatedProcess = "CastCraftScreen",
            };
            if (detect)
                a.AppPath = AppLocator.FindAppFolder(
                    new[]
                    {
                        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\CastCraft"),
                        PF("CastCraft"),
                    },
                    "CastCraft", "CastCraft", new[] { a.ExeName });
            return a;
        }

        public static AppEntry VSeeFace(bool detect = true)
        {
            var a = new AppEntry { Name = "VSeeFace", ExeName = "VSeeFace.exe" };
            // インストーラーが無く zip 展開のことが多いので、ユーザーフォルダ内も探す
            if (detect)
                a.AppPath = AppLocator.FindAppFolder(new string[0], "VSeeFace", "VSeeFace",
                    new[] { a.ExeName }, AppLocator.UserFolders);
            return a;
        }

        /// <summary>初回のリスト。見つからなかったアプリはチェックを外しておく。</summary>
        public static List<AppEntry> Defaults()
        {
            var list = new List<AppEntry> { VSeeFace(), Obs(), CastCraft() };
            foreach (var a in list) a.Enabled = a.ResolveExe() != null;
            return list;
        }
    }
}
