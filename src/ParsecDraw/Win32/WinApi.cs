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

    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint VK_D = 0x44; // Ctrl+Alt+D 切换绘画/观看
    public const uint VK_C = 0x43; // Ctrl+Alt+C 清空
    public const uint VK_P = 0x50; // Ctrl+Alt+P 钉住

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

    [LibraryImport("user32.dll", EntryPoint = "RegisterHotKey", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKeyNative(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", EntryPoint = "UnregisterHotKey")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKeyNative(IntPtr hWnd, int id);

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

    public static bool TryRegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk)
        => RegisterHotKeyNative(hwnd, id, modifiers, vk);

    public static void UnregisterHotKeyId(IntPtr hwnd, int id)
        => UnregisterHotKeyNative(hwnd, id);

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
