using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace StreamLauncher
{
    /// <summary>配信モニターの設定(%APPDATA%\StreamLauncher\monitor.json)。</summary>
    [DataContract]
    public class MonitorSettings
    {
        [DataMember] public string Host;
        /// <summary>0 なら OBS の設定ファイルのポートを使う。</summary>
        [DataMember] public int Port;
        /// <summary>DPAPI で暗号化したパスワード。空なら OBS の設定ファイルから読む。</summary>
        [DataMember] public string ProtectedPassword;
        /// <summary>見張るマイクの入力名。空なら最初のマイク入力。</summary>
        [DataMember] public string MicInput;

        [DataMember] public double DropWarnPercent;
        /// <summary>設定ビットレートの何 % を下回ったら警告するか。</summary>
        [DataMember] public int BitrateWarnPercent;
        /// <summary>基準のビットレート(kbps)。0 なら OBS の出力設定から読む。</summary>
        [DataMember] public int TargetBitrateKbps;
        [DataMember] public int MicSilentSec;
        /// <summary>必ず有効にしておくフィルター(1 行に「ソース名:フィルター名」)。</summary>
        [DataMember] public string RequiredFilters;
        [DataMember] public bool CheckReplayBuffer;
        /// <summary>配信先の接続方法。true = アカウント接続(ストリームキーを確認しない)。</summary>
        [DataMember] public bool AccountConnected;
        [DataMember] public int CpuWarnPercent;
        [DataMember] public int DiskWarnGb;

        [DataMember] public bool PlaySound;
        [DataMember] public bool WindowsNotify;
        [DataMember] public string DiscordWebhook;
        [DataMember] public bool AlwaysOnTop;
        [DataMember] public bool OpenAfterLaunch;

        public MonitorSettings() { SetDefaults(); }

        [OnDeserializing]
        private void OnDeserializing(StreamingContext _) { SetDefaults(); }

        private void SetDefaults()
        {
            Host = "localhost";
            Port = 0;
            ProtectedPassword = "";
            MicInput = "";
            DropWarnPercent = 1.0;
            BitrateWarnPercent = 70;
            TargetBitrateKbps = 0;
            MicSilentSec = 30;
            RequiredFilters = "";
            CheckReplayBuffer = false;
            CpuWarnPercent = 90;
            DiskWarnGb = 5;
            PlaySound = true;
            WindowsNotify = true;
            DiscordWebhook = "";
            AlwaysOnTop = true;
            OpenAfterLaunch = true;
        }

        public bool IsLocal =>
            string.IsNullOrWhiteSpace(Host) || Host.Trim() == "localhost" || Host.Trim() == "127.0.0.1";

        /// <summary>入力されたパスワード(平文)。保存時は Windows のユーザーに紐づけて暗号化する。</summary>
        public string Password
        {
            get
            {
                if (string.IsNullOrEmpty(ProtectedPassword)) return "";
                try
                {
                    var bytes = ProtectedData.Unprotect(Convert.FromBase64String(ProtectedPassword), null, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(bytes);
                }
                catch { return ""; }
            }
            set
            {
                ProtectedPassword = string.IsNullOrEmpty(value)
                    ? ""
                    : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
            }
        }

        private static string FilePath => Path.Combine(Settings.FolderPath, "monitor.json");

        public static MonitorSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var bytes = Encoding.UTF8.GetBytes(File.ReadAllText(FilePath, Encoding.UTF8));
                    using (var ms = new MemoryStream(bytes))
                        return (MonitorSettings)new DataContractJsonSerializer(typeof(MonitorSettings)).ReadObject(ms);
                }
            }
            catch { }
            return new MonitorSettings();
        }

        public void Save()
        {
            Directory.CreateDirectory(Settings.FolderPath);
            using (var fs = File.Create(FilePath))
            using (var w = JsonReaderWriterFactory.CreateJsonWriter(fs, new UTF8Encoding(false), false, true))
                new DataContractJsonSerializer(typeof(MonitorSettings)).WriteObject(w, this);
        }
    }
}
