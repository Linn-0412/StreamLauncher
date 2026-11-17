using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Threading;
using System.Windows.Forms;

namespace StreamLauncher
{
    /// <summary>位置で押すボタン(Unity 製など UI Automation で見えないアプリ用)。</summary>
    [DataContract]
    public class ClickPoint
    {
        /// <summary>クライアント領域に対する割合(0〜1)。</summary>
        [DataMember] public double X;
        [DataMember] public double Y;
        /// <summary>ボタン周辺の画像(PNG を base64)。現れるまで待つのと位置合わせに使う。</summary>
        [DataMember] public string Patch;
    }

    internal static class PositionClicker
    {
        private const int PatchW = 48, PatchH = 24;
        private const int SearchMargin = 80;   // 記録位置から上下左右この範囲で画像を探す
        private const double MatchThreshold = 14; // 1画素あたりの平均輝度差(0〜255)

        /// <summary>今のマウス位置を、指定アプリのウィンドウ上の位置として記録する。</summary>
        public static ClickPoint Record(string processName)
        {
            var pos = Cursor.Position;
            var hwnd = Native.GetAncestor(Native.WindowFromPoint(new Native.POINT { X = pos.X, Y = pos.Y }), Native.GA_ROOT);
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            string name;
            using (var p = Process.GetProcessById((int)pid)) name = p.ProcessName;
            if (!string.Equals(name, processName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"マウスの下にあるのは {name} のウィンドウです({processName} の上に置いてください)");

            var rc = Native.ClientRectOnScreen(hwnd);
            if (rc.Width <= 0 || !rc.Contains(pos))
                throw new InvalidOperationException("ウィンドウの中(タイトルバー以外)にマウスを置いてください");

            // マウスが乗った見た目(ホバー)で記録しないよう、いったんカーソルをどかしてから撮る
            Native.SetCursorPos(rc.Right + 200, rc.Bottom + 200);
            Thread.Sleep(350);
            var patch = new Rectangle(pos.X - PatchW / 2, pos.Y - PatchH / 2, PatchW, PatchH);
            string png;
            using (var bmp = Capture(patch)) png = ToBase64(bmp);
            Native.SetCursorPos(pos.X, pos.Y);

            return new ClickPoint
            {
                X = (pos.X - rc.X) / (double)rc.Width,
                Y = (pos.Y - rc.Y) / (double)rc.Height,
                Patch = png,
            };
        }

        /// <summary>記録位置(Expected)と、画像が見つかればその中心(Matched)。</summary>
        public static (Point Expected, Point? Matched) Locate(IntPtr hwnd, ClickPoint cp)
        {
            var rc = Native.ClientRectOnScreen(hwnd);
            if (rc.Width <= 0 || rc.Height <= 0) return (Point.Empty, null);
            var expected = new Point(rc.X + (int)Math.Round(cp.X * rc.Width), rc.Y + (int)Math.Round(cp.Y * rc.Height));
            if (string.IsNullOrEmpty(cp.Patch)) return (expected, expected);

            using (var patch = FromBase64(cp.Patch))
            {
                var region = Rectangle.Intersect(rc, new Rectangle(
                    expected.X - patch.Width / 2 - SearchMargin, expected.Y - patch.Height / 2 - SearchMargin,
                    patch.Width + 2 * SearchMargin, patch.Height + 2 * SearchMargin));
                if (region.Width < patch.Width || region.Height < patch.Height) return (expected, null);

                using (var shot = Capture(region))
                {
                    var hay = ToRgb(shot);
                    var needle = ToRgb(patch);
                    var best = FindBest(hay, shot.Width, shot.Height, needle, patch.Width, patch.Height, out int bx, out int by);
                    if (best > MatchThreshold) return (expected, null);
                    return (expected, new Point(region.X + bx + patch.Width / 2, region.Y + by + patch.Height / 2));
                }
            }
        }

        // RGB の平均絶対差が最小の位置(途中で打ち切りながら総当たり)。
        // 明るさだけだと「色は違うが明るさが近い」場所を誤検出するので色も比べる。
        private static double FindBest(byte[] hay, int hw, int hh, byte[] needle, int nw, int nh, out int bestX, out int bestY)
        {
            long bestSum = long.MaxValue;
            bestX = bestY = 0;
            int rowLen = nw * 3;
            for (int y = 0; y <= hh - nh; y++)
            {
                for (int x = 0; x <= hw - nw; x++)
                {
                    long sum = 0;
                    for (int j = 0; j < nh && sum < bestSum; j++)
                    {
                        int hi = ((y + j) * hw + x) * 3, ni = j * rowLen;
                        for (int i = 0; i < rowLen; i++) sum += Math.Abs(hay[hi + i] - needle[ni + i]);
                    }
                    if (sum < bestSum) { bestSum = sum; bestX = x; bestY = y; }
                }
            }
            return bestSum / (double)(nw * nh * 3);
        }

        private static Bitmap Capture(Rectangle r)
        {
            var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
            return bmp;
        }

        // 1画素3バイト(B, G, R)の配列にする
        private static byte[] ToRgb(Bitmap bmp)
        {
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var raw = new byte[data.Stride * bmp.Height];
                Marshal.Copy(data.Scan0, raw, 0, raw.Length);
                var rgb = new byte[bmp.Width * bmp.Height * 3];
                for (int y = 0; y < bmp.Height; y++)
                    for (int x = 0; x < bmp.Width; x++)
                    {
                        int o = y * data.Stride + x * 4, d = (y * bmp.Width + x) * 3; // BGRA → BGR
                        rgb[d] = raw[o];
                        rgb[d + 1] = raw[o + 1];
                        rgb[d + 2] = raw[o + 2];
                    }
                return rgb;
            }
            finally { bmp.UnlockBits(data); }
        }

        private static string ToBase64(Bitmap bmp)
        {
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                return Convert.ToBase64String(ms.ToArray());
            }
        }

        private static Bitmap FromBase64(string b64)
        {
            using (var ms = new MemoryStream(Convert.FromBase64String(b64)))
            using (var img = Image.FromStream(ms))
                return new Bitmap(img); // ストリームから切り離す
        }
    }
}
