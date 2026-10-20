using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ParsecDraw.Win32;

/// <summary>
/// 系统托盘图标：Shell_NotifyIconW + 隐藏消息窗口收回调，零第三方依赖。
/// 左键 = 恢复主面板；右键 = TrackPopupMenu 原生菜单（打开/退出）。
/// 图标直接复用 exe 内嵌的 ApplicationIcon（Win32 资源 ID 1），无需额外文件。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint CallbackMsg = 0x8000 + 1; // WM_APP+1
    private const int EventLButtonUp = 0x0202;
    private const int EventLButtonDblClk = 0x0203;
    private const int EventRButtonUp = 0x0205;

    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2;
    private const uint NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4, NifInfo = 0x10;
    private const uint NiifInfo = 0x1;

    private const uint CmdOpen = 1, CmdExit = 2;
    private const uint TpmRightButton = 0x0002, TpmNonotify = 0x0080, TpmReturnCmd = 0x0100;

    private HwndSource? _source;
    private IntPtr _hwnd;
    private IntPtr _hIcon;
    private bool _added;

    /// <summary>左键 / 菜单"打开"。UI 线程回调。</summary>
    public event Action? OpenRequested;

    /// <summary>菜单"退出"。UI 线程回调。</summary>
    public event Action? ExitRequested;

    public TrayIcon(string tooltip)
    {
        // 隐藏消息窗口：托盘回调与菜单命令都落在它上面
        var parameters = new HwndSourceParameters("ParsecDrawTrayIcon")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP
        };
        _source = new HwndSource(parameters);
        _hwnd = _source.Handle;
        _source.AddHook(WndProc);

        // 注意：cx=cy=0 + LR_DEFAULTSIZE 的组合在 Win11 上会原生 AV（实测），务必传显式尺寸。
        // 托盘用小图标规格（SM_CXSMICON/SM_CYSMICON）加载，失败则退回 16px；
        // 再失败（exe 资源异常）用系统默认应用图标兜底——宁可图标丑也不能没有。
        var cx = GetSystemMetrics(49 /*SM_CXSMICON*/);
        var cy = GetSystemMetrics(50 /*SM_CYSMICON*/);
        if (cx <= 0 || cy <= 0) { cx = 16; cy = 16; }
        _hIcon = LoadImageW(GetModuleHandleW(null), (IntPtr)1, 1 /*IMAGE_ICON*/, cx, cy, 0);
        if (_hIcon == IntPtr.Zero)
            _hIcon = LoadImageW(GetModuleHandleW(null), (IntPtr)1, 1, 16, 16, 0);
        if (_hIcon == IntPtr.Zero)
            _hIcon = LoadIcon(IntPtr.Zero, (IntPtr)32512 /*IDI_APPLICATION*/);

        var n = NewData();
        n.uFlags = NifMessage | NifIcon | NifTip;
        n.uCallbackMessage = CallbackMsg;
        n.hIcon = _hIcon;
        n.szTip = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        _added = Shell_NotifyIconW(NimAdd, ref n);
    }

    /// <summary>气泡通知（首次最小化到托盘时提示怎么恢复）。</summary>
    public void ShowInfo(string title, string text)
    {
        if (!_added) return;
        var n = NewData();
        n.uFlags = NifInfo;
        n.szInfoTitle = title.Length > 63 ? title[..63] : title;
        n.szInfo = text.Length > 255 ? text[..255] : text;
        n.dwInfoFlags = NiifInfo;
        Shell_NotifyIconW(NimModify, ref n);
    }

    public void Dispose()
    {
        if (_added)
        {
            var n = NewData();
            _added = false;
            _ = Shell_NotifyIconW(NimDelete, ref n);
        }
        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
        }
    }

    private NOTIFYICONDATAW NewData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = 1,
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != CallbackMsg)
            return IntPtr.Zero;
        var evt = (int)(lParam.ToInt64() & 0xFFFF);
        switch (evt)
        {
            case EventLButtonUp:
            case EventLButtonDblClk:
                OpenRequested?.Invoke();
                break;
            case EventRButtonUp:
                ShowContextMenu();
                break;
        }
        handled = true;
        return IntPtr.Zero;
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            _ = AppendMenuW(menu, 0 /*MF_STRING*/, CmdOpen, "打开主面板");
            _ = AppendMenuW(menu, 0, CmdExit, "退出");

            GetCursorPos(out var pt);
            SetForegroundWindow(_hwnd); // 经典套路：否则菜单无法失焦关闭
            var cmd = TrackPopupMenu(menu, TpmRightButton | TpmNonotify | TpmReturnCmd, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
            _ = PostMessageW(_hwnd, 0 /*WM_NULL*/, IntPtr.Zero, IntPtr.Zero);

            if (cmd == CmdOpen) OpenRequested?.Invoke();
            else if (cmd == CmdExit) ExitRequested?.Invoke();
        }
        finally
        {
            _ = DestroyMenu(menu);
        }
    }

    // ---- 互操作 ----

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeout; // 与 uVersion 共用
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadImageW(IntPtr hInst, IntPtr name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadIcon(IntPtr hInst, IntPtr name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);
}
