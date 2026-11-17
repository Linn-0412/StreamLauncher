using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace StreamLauncher
{
    /// <summary>
    /// GitHub Releases の latest.json を見て自己更新する。
    /// exe は CI で RSA 署名され、埋め込みの公開鍵で検証できたものだけを適用する
    /// (秘密鍵は GitHub Actions の Secret UPDATE_SIGNING_KEY にのみ置く)。
    /// </summary>
    internal static class Updater
    {
        private const string LatestJsonUrl = "https://github.com/Linn-0412/StreamLauncher/releases/latest/download/latest.json";
        public const string ReleasesPage = "https://github.com/Linn-0412/StreamLauncher/releases/latest";

        private const string PublicKeyXml =
            "<RSAKeyValue><Modulus>rd+WhPnLhGQQeyyXGnBtHlBqnZ7RxggG5RT4NX/WbKLdl4zkRnq93V3n23Vagcg7mpTF/scJ3BR2VKTIbUd+W/k1xwCImsGcCYgpuV5K8Q0/V6YvZGY4FEmD9GJcwLhwq22Qw4VzyE1YSOTV+2dYs4ko3RFkEZ/oeGVKQB1QU0Wg/w/jMnCKK1wJa9LGUKx6nZHU7O9KxvFVwjgphiyG4crOMNF5U+4gO6neU+o6va/dzaS2DfkgxptaV5WGzrLkMhnFiLfvusXBIBKzyoGf+wlD3qncFARwc6iVHlQiLIKjz0JTz4usIAu0zPbm9GUtM4lnYvZ23GWIg0lXmUUgMn2SO0rXiDhd+9nv4YMkx03XsS0knQwUuFGcKfbIreTzucqSh4YKyjGxPhAJj6FHRZz0CIMcaqbWwM4vM74cweLBdc0QvCqbTILV9+8FuF0rL0iRgis5X7tz4w+9PFa1S8UsCxDHGKqPHS0w/Gunpia9UKJmXBdESttnjb3vvI5x</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";

#pragma warning disable CS0649 // JSON から復元されるフィールド
        [DataContract]
        public sealed class ReleaseInfo
        {
            [DataMember(Name = "version")] public string Version;
            [DataMember(Name = "url")] public string Url;
            [DataMember(Name = "sha256")] public string Sha256;
            [DataMember(Name = "signature")] public string Signature;
        }
#pragma warning restore CS0649

        public static Version CurrentVersion => Normalize(Assembly.GetExecutingAssembly().GetName().Version);

        public static string CurrentVersionText => ToText(CurrentVersion);

        public static string ToText(Version v) => $"{v.Major}.{v.Minor}.{v.Build}";

        private static Version Normalize(Version v) =>
            new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

        private static HttpClient CreateClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("StreamLauncher-Updater");
            return c;
        }

        /// <summary>新しいバージョンがあればその情報、最新なら null。</summary>
        public static async Task<ReleaseInfo> CheckAsync()
        {
            byte[] json;
            using (var c = CreateClient())
                json = await c.GetByteArrayAsync(LatestJsonUrl).ConfigureAwait(false);

            ReleaseInfo info;
            using (var ms = new MemoryStream(json))
                info = (ReleaseInfo)new DataContractJsonSerializer(typeof(ReleaseInfo)).ReadObject(ms);

            if (info == null || !System.Version.TryParse(info.Version, out var latest))
                throw new InvalidDataException("更新情報を読み取れませんでした");
            return Normalize(latest) > CurrentVersion ? info : null;
        }

        /// <summary>
        /// 新しい exe をダウンロード・検証して差し替える。実行中の exe は削除できないが名前は変えられるので、
        /// 現在の exe を .old に退避して新しい exe を置く(.old は次回起動時に消す)。
        /// </summary>
        public static async Task InstallAsync(ReleaseInfo info, string exePath)
        {
            byte[] data;
            using (var c = CreateClient())
                data = await c.GetByteArrayAsync(info.Url).ConfigureAwait(false);
            Verify(data, info);

            string newPath = exePath + ".new";
            string oldPath = exePath + ".old";
            File.WriteAllBytes(newPath, data);
            try
            {
                if (File.Exists(oldPath)) File.Delete(oldPath);
                File.Move(exePath, oldPath);
                try { File.Move(newPath, exePath); }
                catch { File.Move(oldPath, exePath); throw; }
            }
            finally
            {
                try { if (File.Exists(newPath)) File.Delete(newPath); } catch { }
            }
        }

        private static void Verify(byte[] data, ReleaseInfo info)
        {
            byte[] hash;
            using (var sha = SHA256.Create()) hash = sha.ComputeHash(data);

            if (!string.Equals(BitConverter.ToString(hash).Replace("-", ""), (info.Sha256 ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("ダウンロードしたファイルが壊れています");

            byte[] sig;
            try { sig = Convert.FromBase64String(info.Signature ?? ""); }
            catch { throw new InvalidDataException("署名を読み取れませんでした"); }

            using (var rsa = new RSACng())
            {
                rsa.FromXmlString(PublicKeyXml);
                if (!rsa.VerifyHash(hash, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                    throw new InvalidDataException("署名が一致しません(公式のファイルではない可能性があります)");
            }
        }

        /// <summary>前回の更新で退避した古い exe を消す(終了待ちのため数回試す)。</summary>
        public static async Task CleanupOldAsync(string exePath)
        {
            string oldPath = exePath + ".old";
            for (int i = 0; i < 20 && File.Exists(oldPath); i++)
            {
                try { File.Delete(oldPath); return; }
                catch { await Task.Delay(500).ConfigureAwait(false); }
            }
        }
    }
}
