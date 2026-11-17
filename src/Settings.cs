using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace StreamLauncher
{
    /// <summary>
    /// 設定は配布フォルダではなく各ユーザーの %APPDATA%\StreamLauncher に保存する
    /// (配布物に個人のパスが混ざらないように)。
    /// </summary>
    [DataContract]
    public class Settings
    {
        [DataMember] public List<AppEntry> Apps;
        [DataMember] public bool SkipIfRunning;
        [DataMember] public bool AutoRunOnOpen;
        [DataMember] public bool ConfirmCloseAll;
        [DataMember] public bool ExitAfterCloseAll;

        public Settings()
        {
            SetDefaults();
            Apps = new List<AppEntry>();
        }

        // DataContract の復元ではコンストラクタが呼ばれないので、ここで既定値を入れる
        // (Apps は null のままにして、旧形式の設定ファイルかどうかの判定に使う)
        [OnDeserializing]
        private void OnDeserializing(StreamingContext _) { SetDefaults(); }

        private void SetDefaults()
        {
            SkipIfRunning = true;
            AutoRunOnOpen = false;
            ConfirmCloseAll = true;
            ExitAfterCloseAll = true;
        }

        public static string FolderPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StreamLauncher");

        public static string FilePath => Path.Combine(FolderPath, "settings.json");

        /// <summary>保存済みの設定。無ければ自動検出した初期設定。</summary>
        public static Settings LoadOrDefault()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var bytes = Encoding.UTF8.GetBytes(File.ReadAllText(FilePath, Encoding.UTF8));
                    var s = Read<Settings>(bytes);
                    if (s.Apps == null) s.Apps = Read<LegacySettings>(bytes).ToApps(); // 旧形式から移行
                    return s;
                }
            }
            catch { }
            return new Settings { Apps = Presets.Defaults() };
        }

        private static T Read<T>(byte[] bytes)
        {
            using (var ms = new MemoryStream(bytes))
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(ms);
        }

        public void Save()
        {
            Directory.CreateDirectory(FolderPath);
            using (var fs = File.Create(FilePath))
            using (var w = JsonReaderWriterFactory.CreateJsonWriter(fs, new UTF8Encoding(false), false, true))
            {
                new DataContractJsonSerializer(typeof(Settings)).WriteObject(w, this);
            }
        }
    }

#pragma warning disable CS0649 // JSON から復元されるフィールド
    /// <summary>OBS / CastCraft / VSeeFace 固定だった頃の設定ファイル。</summary>
    [DataContract]
    internal class LegacySettings
    {
        [DataMember] public string ObsPath;
        [DataMember] public string ObsArgs;
        [DataMember] public string CastCraftPath;
        [DataMember] public string CastCraftArgs;
        [DataMember] public bool LaunchVSeeFace;
        [DataMember] public string VSeeFacePath;
        [DataMember] public string VSeeFaceArgs;
        [DataMember] public string ButtonName;
        [DataMember] public string ConfirmButtonName;
        [DataMember] public string ScreenProcessName;
        [DataMember] public int DelayAfterObsSec = 5;
        [DataMember] public int UiTimeoutSec = 60;
        [DataMember] public bool UseMouseClick;

        [OnDeserializing]
        private void OnDeserializing(StreamingContext _)
        {
            DelayAfterObsSec = 5;
            UiTimeoutSec = 60;
            ButtonName = "Screen";
            ConfirmButtonName = "起動する";
            ScreenProcessName = "CastCraftScreen";
        }

        public List<AppEntry> ToApps()
        {
            var vsf = Presets.VSeeFace(detect: string.IsNullOrWhiteSpace(VSeeFacePath));
            if (!string.IsNullOrWhiteSpace(VSeeFacePath)) vsf.AppPath = VSeeFacePath;
            vsf.Args = VSeeFaceArgs ?? "";
            vsf.Enabled = LaunchVSeeFace;

            var obs = Presets.Obs(detect: false);
            obs.AppPath = ObsPath ?? "";
            obs.Args = ObsArgs ?? "";
            obs.WaitAfterSec = DelayAfterObsSec;

            var cc = Presets.CastCraft(detect: false);
            cc.AppPath = CastCraftPath ?? "";
            if (CastCraftArgs != null) cc.Args = CastCraftArgs;
            var clicks = new List<string>();
            if (!string.IsNullOrWhiteSpace(ButtonName)) clicks.Add(ButtonName.Trim());
            if (!string.IsNullOrWhiteSpace(ConfirmButtonName)) clicks.Add(ConfirmButtonName.Trim());
            cc.ClickButtons = clicks.ToArray();
            cc.ClickTimeoutSec = UiTimeoutSec;
            cc.RelatedProcess = ScreenProcessName ?? "";
            cc.UseMouseClick = UseMouseClick;

            return new List<AppEntry> { vsf, obs, cc };
        }
    }
}
