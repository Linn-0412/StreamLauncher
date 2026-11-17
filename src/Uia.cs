using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;

namespace StreamLauncher
{
    /// <summary>UI Automation でボタンを探して押す。</summary>
    internal static class Uia
    {
        public sealed class Hit
        {
            public AutomationElement Element;
            public IntPtr Hwnd;
            public string Name;
            public string TypeName;
        }

        private static readonly string[] PreferredTypes = { "Button", "Hyperlink", "MenuItem", "TabItem", "ListItem" };

        /// <summary>指定プロセスのトップレベルウィンドウ全部(確認ダイアログが別ウィンドウでも拾えるように)。</summary>
        public static List<AutomationElement> GetAppRoots(string processName)
        {
            var ps = Process.GetProcessesByName(processName);
            var ids = new HashSet<int>(ps.Select(p => p.Id));
            foreach (var p in ps) p.Dispose();

            var roots = new List<AutomationElement>();
            if (ids.Count == 0) return roots;
            foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition))
            {
                try { if (ids.Contains(w.Current.ProcessId)) roots.Add(w); } catch { }
            }
            return roots;
        }

        public static string TypeName(AutomationElement el) =>
            el.Current.ControlType.ProgrammaticName.Replace("ControlType.", "");

        /// <summary>名前が完全一致する要素を優先し、無ければ部分一致。見つからなければ null。</summary>
        public static Hit Find(string processName, string name)
        {
            foreach (var root in GetAppRoots(processName))
            {
                try
                {
                    var el = FindIn(root, name);
                    if (el != null)
                        return new Hit
                        {
                            Element = el,
                            Hwnd = new IntPtr(root.Current.NativeWindowHandle),
                            Name = el.Current.Name,
                            TypeName = TypeName(el),
                        };
                }
                catch { }
            }
            return null;
        }

        private static AutomationElement FindIn(AutomationElement root, string name)
        {
            string target = name.Trim();
            var exact = new List<AutomationElement>();
            var partial = new List<AutomationElement>();
            foreach (AutomationElement el in root.FindAll(TreeScope.Descendants, Condition.TrueCondition))
            {
                try
                {
                    var c = el.Current;
                    var n = (c.Name ?? "").Trim();
                    if (n.Length == 0 || c.IsOffscreen) continue;
                    if (string.Equals(n, target, StringComparison.OrdinalIgnoreCase)) exact.Add(el);
                    else if (n.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0) partial.Add(el);
                }
                catch { }
            }
            return SelectBest(exact) ?? SelectBest(partial);
        }

        private static AutomationElement SelectBest(List<AutomationElement> list)
        {
            if (list.Count == 0) return null;
            foreach (var el in list)
            {
                try { if (PreferredTypes.Contains(TypeName(el))) return el; } catch { }
            }
            return list[0];
        }

        /// <summary>押せる要素(自身か親)を Invoke。無理ならマウスで中心をクリック。押し方を返す。</summary>
        public static string Click(Hit hit, bool useMouse)
        {
            if (!useMouse)
            {
                var walker = TreeWalker.ControlViewWalker;
                var cur = hit.Element;
                for (int i = 0; i < 4 && cur != null; i++)
                {
                    var type = cur.Current.ControlType;
                    if (type == ControlType.Window || type == ControlType.Document) break;
                    if (cur.TryGetCurrentPattern(InvokePattern.Pattern, out object p))
                    {
                        ((InvokePattern)p).Invoke();
                        return "InvokePattern";
                    }
                    if (cur.TryGetCurrentPattern(SelectionItemPattern.Pattern, out p))
                    {
                        ((SelectionItemPattern)p).Select();
                        return "SelectionItemPattern";
                    }
                    cur = walker.GetParent(cur);
                }
            }

            if (hit.Hwnd != IntPtr.Zero)
            {
                if (Native.IsIconic(hit.Hwnd)) Native.ShowWindow(hit.Hwnd, 9); // SW_RESTORE
                Native.SetForegroundWindow(hit.Hwnd);
                Thread.Sleep(400);
            }
            var r = hit.Element.Current.BoundingRectangle;
            if (r.IsEmpty || r.Width <= 0) throw new InvalidOperationException("ボタンの位置を取得できませんでした");
            Native.Click((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2));
            return "マウスクリック";
        }

        /// <summary>名前付きの UI 要素一覧(ボタン名の確認用)。</summary>
        public static List<string> Dump(string processName)
        {
            var lines = new List<string>();
            foreach (var root in GetAppRoots(processName))
            {
                foreach (AutomationElement el in root.FindAll(TreeScope.Descendants, Condition.TrueCondition))
                {
                    try
                    {
                        var c = el.Current;
                        if (string.IsNullOrWhiteSpace(c.Name)) continue;
                        lines.Add($"    {TypeName(el),-10} '{c.Name}'{(c.IsOffscreen ? " (画面外)" : "")}");
                    }
                    catch { }
                }
            }
            return lines;
        }

        private static class Native
        {
            [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
            [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
            [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
            [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
            [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);

            public static void Click(int x, int y)
            {
                SetCursorPos(x, y);
                mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); // LEFTDOWN
                mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); // LEFTUP
            }
        }
    }
}
