using System.Runtime.InteropServices;

namespace ParsecDraw.Win32;

/// <summary>
/// 低级键盘钩子（WH_KEYBOARD_LL）热键引擎，替代 RegisterHotKey。
/// 原因：Parsec 客户端获得焦点时会捕获键盘（转发给远端主机），
/// RegisterHotKey 的系统热键分发收不到按键——而"看着 Parsec 按热键"
/// 正是本工具的核心场景。LL 钩子在输入链最前端，先于 Parsec 处理。
/// 命中组合键时吞掉（返回 1），按键不会再被转发到主机。
/// </summary>
internal sealed partial class KeyboardHook : IDisposable
{
    private const int WhKeyboardLL = 13;
    private const uint WmKeydown = 0x0100, WmSyskeydown = 0x0104;

    private readonly nint _hook;
    private readonly Native.HookProc _proc; // 防 GC 回收委托
    private bool _disposed;

    /// <summary>命中热键（vk），回调在 UI 线程（钩子安装线程的消息循环）。</summary>
    public event Action<uint>? Hotkey;

    public KeyboardHook()
    {
        _proc = HookProc;
        _hook = Native.SetWindowsHookExW(WhKeyboardLL, _proc, Native.GetModuleHandleW(0), 0);
    }

    public bool Active => _hook != 0;

    private nint HookProc(int code, nuint wParam, nint lParam)
    {
        if (code == 0 && (wParam == WmKeydown || wParam == WmSyskeydown))
        {
            var vk = (uint)Marshal.ReadInt32(lParam); // KBDLLHOOKSTRUCT.vkOffset=0
            if (IsHotkey(vk))
            {
                Hotkey?.Invoke(vk);
                return 1; // 吞掉：既触发本端动作，也不让按键流入 Parsec/主机
            }
        }
        return Native.CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static bool IsHotkey(uint vk)
        => vk is 0x44 or 0x43 or 0x50 or 0x5A // D / C / P / Z
           && (Native.GetAsyncKeyState(0x11) & 0x8000) != 0  // Ctrl
           && (Native.GetAsyncKeyState(0x12) & 0x8000) != 0; // Alt

    public void Dispose()
    {
        if (_disposed || _hook == 0) return;
        _disposed = true;
        _ = Native.UnhookWindowsHookEx(_hook);
    }

    private static partial class Native
    {
        internal delegate nint HookProc(int code, nuint wParam, nint lParam);

        [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
        internal static partial nint SetWindowsHookExW(int idHook, HookProc lpfn, nint hMod, uint dwThreadId);

        [LibraryImport("user32.dll", EntryPoint = "CallNextHookEx")]
        internal static partial nint CallNextHookEx(nint hhk, int nCode, nuint wParam, nint lParam);

        [LibraryImport("user32.dll", EntryPoint = "UnhookWindowsHookEx")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool UnhookWindowsHookEx(nint hhk);

        [LibraryImport("user32.dll", EntryPoint = "GetAsyncKeyState")]
        internal static partial short GetAsyncKeyState(int vKey);

        [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true)]
        internal static partial nint GetModuleHandleW(nint lpModuleName);
    }
}
