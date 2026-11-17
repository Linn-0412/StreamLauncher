using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace StreamLauncher
{
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);

        public const uint GA_ROOT = 2;

        public static void Click(int x, int y)
        {
            SetCursorPos(x, y);
            mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); // LEFTDOWN
            mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); // LEFTUP
        }

        public static void Activate(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return;
            if (IsIconic(hWnd)) ShowWindow(hWnd, 9); // SW_RESTORE
            SetForegroundWindow(hWnd);
        }

        /// <summary>ウィンドウのクライアント領域(枠・タイトルバーを除く)の画面座標。</summary>
        public static Rectangle ClientRectOnScreen(IntPtr hWnd)
        {
            if (!GetClientRect(hWnd, out var r)) return Rectangle.Empty;
            var p = new POINT();
            if (!ClientToScreen(hWnd, ref p)) return Rectangle.Empty;
            return new Rectangle(p.X, p.Y, r.Right - r.Left, r.Bottom - r.Top);
        }
    }
}
