using System.Runtime.InteropServices;

namespace ParsecDraw.Win32;

/// <summary>
/// Win32 互操作：点击穿透、全局热键、窗口 z 序、本机 IPv4 枚举。
/// </summary>
internal static partial class WinApi
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    public const int WM_HOTKEY = 0x0312; // 仅剩冒烟测试投递用；真实热键走 KeyboardHook

    public static readonly IntPtr HwndTopmost = new(-1);

    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoActivate = 0x0010;

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static partial int GetWindowLong(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static partial int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPosNative(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    // ---- 窗口枚举 / 客户区 / DPI（观众端对齐补偿用）----

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public double Width => Right - Left;
        public double Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    [LibraryImport("user32.dll", EntryPoint = "EnumWindows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumWindowsNative(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "IsWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowNative(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "IsWindowVisible")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisibleNative(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "IsIconic")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconicNative(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [LibraryImport("user32.dll", EntryPoint = "GetClientRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRectNative(IntPtr hWnd, out RECT lpRect);

    [LibraryImport("user32.dll", EntryPoint = "ClientToScreen")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreenNative(IntPtr hWnd, ref POINT lpPoint);

    [LibraryImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    private static partial uint GetDpiForWindowNative(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "GetSystemMetrics")]
    private static partial int GetSystemMetricsNative(int nIndex);

    /// <summary>在调用方线程上枚举所有顶层窗口（回调返回 false 时停止）。</summary>
    public static List<IntPtr> EnumTopLevelWindows()
    {
        var list = new List<IntPtr>(512);
        EnumWindowsNative((h, _) => { list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }

    public static bool IsWindowAlive(IntPtr h) => h != IntPtr.Zero && IsWindowNative(h);
    public static bool IsShown(IntPtr h) => IsWindowVisibleNative(h) && !IsIconicNative(h);

    /// <summary>窗口所属进程 pid（0 = 失败）。</summary>
    public static uint WindowPid(IntPtr h)
    {
        _ = GetWindowThreadProcessId(h, out var pid);
        return pid;
    }
    public static bool TryGetClientRect(IntPtr h, out RECT r) => GetClientRectNative(h, out r);
    public static bool TryClientToScreen(IntPtr h, ref POINT p) => ClientToScreenNative(h, ref p);
    public static uint GetWindowDpi(IntPtr h) => GetDpiForWindowNative(h);

    /// <summary>主显示器分辨率（物理 px）。</summary>
    public static (int W, int H) PrimaryScreenSize
    {
        get
        {
            var w = GetSystemMetricsNative(0); // SM_CXSCREEN
            var h = GetSystemMetricsNative(1); // SM_CYSCREEN
            return (w, h);
        }
    }

    // ---- 合成输入（冒烟测试用：向指定窗口投递鼠标/热键消息）----

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageWNative(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "WindowFromPoint")]
    private static partial IntPtr WindowFromPointNative(int x, int y);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
    private static partial int GetWindowTextW(IntPtr hWnd, IntPtr lpString, int nMaxCount);

    /// <summary>OS 级命中测试：物理屏幕坐标处最顶层的可点击窗口（验证真实鼠标能否到达覆盖层）。</summary>
    public static IntPtr WindowFromScreenPoint(int x, int y)
        => WindowFromPointNative(x, y);

    /// <summary>窗口标题（冒烟判定用）。LibraryImport 不支持 StringBuilder，走 IntPtr 缓冲。</summary>
    public static string WindowTitle(IntPtr hWnd)
    {
        var p = System.Runtime.InteropServices.Marshal.AllocHGlobal(512);
        try
        {
            _ = GetWindowTextW(hWnd, p, 256);
            return System.Runtime.InteropServices.Marshal.PtrToStringUni(p) ?? "";
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(p);
        }
    }

    /// <summary>向窗口客户区坐标 (x,y) 投递鼠标消息（msg=0x200 移动 / 0x201 左键按下 / 0x202 左键抬起）。</summary>
    public static void PostMouse(IntPtr hWnd, uint msg, int x, int y)
        => PostMessageWNative(hWnd, msg, (IntPtr)1, (IntPtr)(((y & 0xFFFF) << 16) | (x & 0xFFFF)));

    /// <summary>向窗口投递 WM_HOTKEY（id=注册时的热键 id），走应用自己的 WndProc 路径。</summary>
    public static void PostHotkey(IntPtr hWnd, int id)
        => PostMessageWNative(hWnd, 0x0312, (IntPtr)id, IntPtr.Zero);

    /// <summary>切换窗口的点击穿透 / 不抢焦点扩展样式。AllowsTransparency 的窗口已带 WS_EX_LAYERED。</summary>
    public static void SetClickThrough(IntPtr hwnd, bool clickThrough, bool noActivate)
    {
        var style = GetWindowLong(hwnd, GWL_EXSTYLE);
        style = clickThrough ? style | WS_EX_TRANSPARENT : style & ~WS_EX_TRANSPARENT;
        style = noActivate ? style | WS_EX_NOACTIVATE : style & ~WS_EX_NOACTIVATE;
        SetWindowLong(hwnd, GWL_EXSTYLE, style);
    }

    /// <summary>把 hwnd 放到 aboveHwnd 的正上方（两者都已 TOPMOST 时用于工具栏压住覆盖层）。</summary>
    public static void PlaceAbove(IntPtr hwnd, IntPtr aboveHwnd)
        => SetWindowPosNative(hwnd, aboveHwnd, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);

    public static void AssertTopmost(IntPtr hwnd)
        => SetWindowPosNative(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);

    /// <summary>枚举本机所有活动网卡的 IPv4 地址（供主机端展示共享地址）。</summary>
    public static List<string> GetLocalIPv4Addresses()
        => System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                        && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            .Select(a => a.Address.ToString())
            .Distinct()
            .ToList();
}
