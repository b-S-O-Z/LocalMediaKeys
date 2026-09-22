// LocalMediaKeys.cs
// リモートデスクトップ(mstsc)を全画面で使っていても、メディアキー
// （次の曲 / 前の曲 / 再生・一時停止 / 停止 / 音量アップ / 音量ダウン / ミュート）
// をローカル側で処理する常駐ツール。
//
// 仕組み:
//   低レベルキーボードフック(WH_KEYBOARD_LL)でメディアキーを先に受け取り、
//   リモートへ転送される前に握りつぶし、代わりにローカルのシェル(タスクバー)へ
//   WM_APPCOMMAND を投げる。シェルが再生中のプレーヤーへ振り分ける。
//
// ビルド: build.cmd（.NET Framework 同梱の csc.exe を使用。追加インストール不要）

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("LocalMediaKeys")]
[assembly: AssemblyProduct("LocalMediaKeys")]
[assembly: AssemblyDescription("Keep media keys local while Remote Desktop is fullscreen")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace LocalMediaKeys
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool created;
            using (new Mutex(true, "LocalMediaKeys_SingleInstance", out created))
            {
                if (!created) return;   // 二重起動しない
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayContext());
            }
        }
    }

    sealed class TrayContext : ApplicationContext
    {
        // ---- Win32 定数 ----
        const int WH_KEYBOARD_LL = 13;
        const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
        const uint WM_APPCOMMAND = 0x0319;
        const int VK_VOLUME_MUTE = 0xAD, VK_VOLUME_DOWN = 0xAE, VK_VOLUME_UP = 0xAF;
        const int VK_MEDIA_NEXT_TRACK = 0xB0, VK_MEDIA_PREV_TRACK = 0xB1, VK_MEDIA_STOP = 0xB2, VK_MEDIA_PLAY_PAUSE = 0xB3;
        const int APPCOMMAND_VOLUME_MUTE = 8, APPCOMMAND_VOLUME_DOWN = 9, APPCOMMAND_VOLUME_UP = 10;
        const int APPCOMMAND_MEDIA_NEXTTRACK = 11, APPCOMMAND_MEDIA_PREVIOUSTRACK = 12, APPCOMMAND_MEDIA_STOP = 13, APPCOMMAND_MEDIA_PLAY_PAUSE = 14;
        const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        const uint WINEVENT_OUTOFCONTEXT = 0x0000;

        delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [StructLayout(LayoutKind.Sequential)]
        struct KBDLLHOOKSTRUCT { public uint vkCode; public uint scanCode; public uint flags; public uint time; public IntPtr dwExtraInfo; }

        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)] static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto)] static extern IntPtr GetModuleHandle(string lpModuleName);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        // リモートデスクトップクライアントと見なすプロセス名（小文字）
        //   mstsc  : Windows 標準クライアント
        //   msrdc  : Windows App / Azure Virtual Desktop クライアント
        static readonly string[] RdpProcessNames = { "mstsc", "msrdc", "msrdcw" };
        // mstsc のトップレベルウィンドウクラス（プロセス名で判定できないときの保険）
        const string RdpWindowClass = "TscShellContainerClass";

        const string SettingsKey = @"Software\LocalMediaKeys";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string AppName = "LocalMediaKeys";

        // 診断ログ: %LOCALAPPDATA%\LocalMediaKeys\LocalMediaKeys.log（起動ごとに作り直す）
        static readonly string LogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName, AppName + ".log");
        static readonly object LogLock = new object();

        readonly NotifyIcon _tray;
        readonly ToolStripMenuItem _miEnabled, _miOnlyRdp, _miStartup;
        readonly LowLevelKeyboardProc _hookProc;     // GC に回収されないよう保持
        readonly WinEventDelegate _winEventProc;     // 同上
        readonly bool[] _held = new bool[16];        // キーリピートで連打にならないように
        IntPtr _hook = IntPtr.Zero;
        IntPtr _winEventHook = IntPtr.Zero;
        volatile bool _enabled = true;
        volatile bool _onlyWhenRdpForeground = true;
        volatile bool _rdpForeground = false;

        public TrayContext()
        {
            _hookProc = HookCallback;
            _winEventProc = WinEventCallback;
            LoadSettings();
            try { Directory.CreateDirectory(Path.GetDirectoryName(LogPath)); File.WriteAllText(LogPath, "", Encoding.UTF8); } catch { }
            Log("start enabled=" + _enabled + " onlyRdp=" + _onlyWhenRdpForeground + " exe=" + Application.ExecutablePath);

            _miEnabled = new ToolStripMenuItem("有効") { Checked = _enabled, CheckOnClick = true };
            _miEnabled.CheckedChanged += delegate { _enabled = _miEnabled.Checked; SaveSettings(); UpdateTray(); };

            _miOnlyRdp = new ToolStripMenuItem("リモートデスクトップが前面のときだけ横取り") { Checked = _onlyWhenRdpForeground, CheckOnClick = true };
            _miOnlyRdp.CheckedChanged += delegate { _onlyWhenRdpForeground = _miOnlyRdp.Checked; SaveSettings(); UpdateTray(); };

            _miStartup = new ToolStripMenuItem("Windows 起動時に自動起動") { Checked = IsStartupRegistered(), CheckOnClick = true };
            _miStartup.CheckedChanged += delegate { SetStartup(_miStartup.Checked); };

            var miExit = new ToolStripMenuItem("終了");
            miExit.Click += delegate { ExitThread(); };

            var menu = new ContextMenuStrip();
            menu.Items.AddRange(new ToolStripItem[] { _miEnabled, _miOnlyRdp, new ToolStripSeparator(), _miStartup, new ToolStripSeparator(), miExit });

            _tray = new NotifyIcon { Icon = CreateIcon(), ContextMenuStrip = menu, Visible = true };
            _tray.DoubleClick += delegate { _miEnabled.Checked = !_miEnabled.Checked; };

            _rdpForeground = IsRdpWindow(GetForegroundWindow());
            Log("foreground at start: rdp=" + _rdpForeground + " " + DescribeWindow(GetForegroundWindow()));
            InstallHook();
            _winEventHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);
            UpdateTray();
            _tray.ShowBalloonTip(2000, AppName, "メディアキーをローカルで処理します。右クリックで設定。", ToolTipIcon.Info);
        }

        protected override void ExitThreadCore()
        {
            if (_winEventHook != IntPtr.Zero) { UnhookWinEvent(_winEventHook); _winEventHook = IntPtr.Zero; }
            UninstallHook();
            _tray.Visible = false;
            _tray.Dispose();
            base.ExitThreadCore();
        }

        // ---- フック ----
        void InstallHook()
        {
            if (_hook != IntPtr.Zero) return;
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(null), 0);
            Log("hook installed: " + _hook + (_hook == IntPtr.Zero ? " ERROR=" + Marshal.GetLastWin32Error() : ""));
        }

        void UninstallHook()
        {
            if (_hook == IntPtr.Zero) return;
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        // 後から入れたフックほど先に呼ばれる。RDP クライアントが前面に来たら
        // 掛け直して、mstsc 自身のフックより先に受け取れるようにする。
        void ReinstallHook() { UninstallHook(); InstallHook(); }

        IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    var info = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                    int vk = (int)info.vkCode;
                    int msg = (int)wParam.ToInt64();
                    int cmd = ToAppCommand(vk);
                    bool active = _enabled && (!_onlyWhenRdpForeground || _rdpForeground);
                    if (vk >= 0xA6 && vk <= 0xB7)   // ブラウザ/メディア/ランチャ系のキーだけ記録する
                        Log(string.Format("key vk=0x{0:X2} msg=0x{1:X3} sc=0x{2:X} flags=0x{3:X} injected={4} rdpFg={5} enabled={6} onlyRdp={7} -> {8}",
                            vk, msg, info.scanCode, info.flags, (info.flags & 0x10) != 0, _rdpForeground, _enabled, _onlyWhenRdpForeground,
                            (cmd != 0 && active) ? "SWALLOW" : "pass"));
                    if (cmd != 0 && active)
                    {
                        if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                        {
                            // 音量アップ/ダウンは押し続けで連続変化させたいのでリピートを通す。
                            // 曲送りなどは押し続けても1回だけ。
                            if (IsRepeatable(cmd) || !_held[cmd]) { _held[cmd] = true; SendToLocalShell(cmd); }
                            return (IntPtr)1;   // リモートへ渡さない
                        }
                        if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
                        {
                            _held[cmd] = false;
                            return (IntPtr)1;
                        }
                    }
                }
            }
            catch { /* フック内では例外を外へ出さない */ }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        static bool IsRepeatable(int cmd)
        {
            return cmd == APPCOMMAND_VOLUME_UP || cmd == APPCOMMAND_VOLUME_DOWN;
        }

        static int ToAppCommand(int vk)
        {
            switch (vk)
            {
                case VK_VOLUME_MUTE:      return APPCOMMAND_VOLUME_MUTE;
                case VK_VOLUME_DOWN:      return APPCOMMAND_VOLUME_DOWN;
                case VK_VOLUME_UP:        return APPCOMMAND_VOLUME_UP;
                case VK_MEDIA_NEXT_TRACK: return APPCOMMAND_MEDIA_NEXTTRACK;
                case VK_MEDIA_PREV_TRACK: return APPCOMMAND_MEDIA_PREVIOUSTRACK;
                case VK_MEDIA_STOP:       return APPCOMMAND_MEDIA_STOP;
                case VK_MEDIA_PLAY_PAUSE: return APPCOMMAND_MEDIA_PLAY_PAUSE;
                default: return 0;
            }
        }

        // ローカルのシェル(タスクバー)に WM_APPCOMMAND を投げる。
        // キーボードのメディアキーを押したときと同じ経路で、再生中のプレーヤーに届く。
        static void SendToLocalShell(int cmd)
        {
            IntPtr shell = FindWindow("Shell_TrayWnd", null);
            if (shell == IntPtr.Zero) { Log("Shell_TrayWnd not found"); return; }
            bool ok = PostMessage(shell, WM_APPCOMMAND, IntPtr.Zero, (IntPtr)(cmd << 16));
            Log("PostMessage WM_APPCOMMAND cmd=" + cmd + " to " + shell + " ok=" + ok);
        }

        // ---- 前面ウィンドウの監視 ----
        void WinEventCallback(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            try
            {
                bool was = _rdpForeground;
                _rdpForeground = IsRdpWindow(hwnd);
                if (_rdpForeground != was) Log("foreground: rdp=" + _rdpForeground + " " + DescribeWindow(hwnd));
                if (_rdpForeground && !was) ReinstallHook();
                if (_rdpForeground != was) UpdateTray();
            }
            catch { }
        }

        static bool IsRdpWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            var sb = new StringBuilder(256);
            if (GetClassName(hwnd, sb, sb.Capacity) > 0 && sb.ToString() == RdpWindowClass) return true;
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if (pid == 0) return false;
            try
            {
                using (var p = Process.GetProcessById((int)pid))
                {
                    string name = p.ProcessName.ToLowerInvariant();
                    foreach (var n in RdpProcessNames) if (name == n) return true;
                }
            }
            catch { }
            return false;
        }

        // ---- トレイ ----
        void UpdateTray()
        {
            string s;
            if (!_enabled) s = "無効";
            else if (!_onlyWhenRdpForeground) s = "有効（常時ローカル処理）";
            else s = _rdpForeground ? "有効（RDP前面: 横取り中）" : "有効（RDP前面時のみ: 待機）";
            _tray.Text = AppName + ": " + s;
        }

        static Icon CreateIcon()
        {
            using (var bmp = new Bitmap(16, 16))
            using (var g = Graphics.FromImage(bmp))
            using (var b = new SolidBrush(Color.FromArgb(30, 144, 255)))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                g.FillPolygon(b, new[] { new Point(1, 3), new Point(8, 8), new Point(1, 13) });
                g.FillPolygon(b, new[] { new Point(7, 3), new Point(14, 8), new Point(7, 13) });
                g.FillRectangle(b, 13, 3, 2, 10);
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        // ---- 診断ログ ----
        static void Log(string s)
        {
            try
            {
                lock (LogLock)
                    File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + s + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        static string DescribeWindow(IntPtr hwnd)
        {
            try
            {
                var sb = new StringBuilder(256);
                GetClassName(hwnd, sb, sb.Capacity);
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                string pname = "?";
                try { using (var p = Process.GetProcessById((int)pid)) pname = p.ProcessName; } catch { }
                return "hwnd=" + hwnd + " class=" + sb + " proc=" + pname;
            }
            catch { return "hwnd=" + hwnd; }
        }

        // ---- 設定（HKCU\Software\LocalMediaKeys） ----
        void LoadSettings()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(SettingsKey))
                {
                    if (k == null) return;
                    _enabled = Convert.ToInt32(k.GetValue("Enabled", 1)) != 0;
                    _onlyWhenRdpForeground = Convert.ToInt32(k.GetValue("OnlyRdp", 1)) != 0;
                }
            }
            catch { }
        }

        void SaveSettings()
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(SettingsKey))
                {
                    k.SetValue("Enabled", _enabled ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("OnlyRdp", _onlyWhenRdpForeground ? 1 : 0, RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        static bool IsStartupRegistered()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(AppName) != null;
            }
            catch { return false; }
        }

        static void SetStartup(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (on) k.SetValue(AppName, "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue(AppName, false);
                }
            }
            catch { }
        }
    }
}
