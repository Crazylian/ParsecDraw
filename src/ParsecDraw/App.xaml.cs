using System.IO;
using System.Windows;
using System.Windows.Threading;
using ParsecDraw.Ui;
using ParsecDraw.Win32;

namespace ParsecDraw;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.ToString(), "ParsecDraw 出错了");
            args.Handled = true;
        };

        var args = e.Args;

        // 无头自测：--selftest <日志路径> → 退出码 0/1
        if (args.Contains("--selftest"))
        {
            var logPath = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "parsec-draw-selftest.log");
            var code = SelfTest.RunAsync(logPath).GetAwaiter().GetResult();
            Environment.ExitCode = code;
            Shutdown(code);
            return;
        }

        // --multi / --smoke：跳过单实例限制（双开联测 / 自动化冒烟不被人肉实例挡住）
        if (!args.Contains("--multi") && !args.Contains("--smoke"))
        {
            _singleInstanceMutex = new Mutex(true, @"Local\ParsecDraw.SingleInstance", out var createdNew);
            if (!createdNew)
            {
                MessageBox.Show("ParsecDraw 已经在运行了。（同机双开联测请用 --multi 参数）", "ParsecDraw");
                Shutdown(0);
                return;
            }
        }

        // 冒烟测试：主机 UI + 同进程观众会话/覆盖层，6 秒后自动退出并写结果日志
        // （不进托盘，避免 Close 被拦）
        if (args.Contains("--smoke"))
        {
            var result = new List<string>();
            // 冒烟也过一遍真实托盘路径（只测加载，不接管 Close 行为）
            var smokeTray = new Win32.TrayIcon("ParsecDraw 冒烟");
            result.Add($"tray-icon={smokeTray.IconLoaded} added={smokeTray.Added}");
            var trayOk = smokeTray.IconLoaded && smokeTray.Added;
            smokeTray.Dispose();

            var w = new ControlWindow(minimizeToTray: false);
            w.Show();
            w.Dispatcher.BeginInvoke(w.SmokeStartHost);
            result.Add("host-window-up");

            Session? viewer = null;
            OverlayWindow? viewerOverlay = null;
            var hostInfoSeen = false;
            var t2 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            t2.Tick += (_, _) =>
            {
                t2.Stop();
                viewer = new Session { DisplayName = "冒烟" };
                viewer.HostInfo += (hw, hh) =>
                {
                    if (!hostInfoSeen)
                    {
                        hostInfoSeen = true;
                        result.Add($"host-info {hw:0}x{hh:0}");
                    }
                };
                viewer.StartViewer(Dispatcher, $"ws://127.0.0.1:{Session.DefaultPort}");
                viewerOverlay = new OverlayWindow(viewer, isHost: false);
                viewerOverlay.Show();
                result.Add("viewer-overlay-up");
            };
            t2.Start();

            // ~4s：对观众覆盖层投递真实路径的合成输入——
            // 输入走 WndProc 原始消息（PostMessage 直达，与真实鼠标同路），热键走 WM_HOTKEY。
            Session? viewerForInput = null;
            OverlayWindow? overlayForInput = null;
            var t3 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            t3.Tick += (_, _) =>
            {
                t3.Stop();
                if (viewerOverlay is null || viewer is null) return;
                viewerForInput = viewer;
                overlayForInput = viewerOverlay;
                var h = viewerOverlay.HwndForSmoke;
                result.Add($"pre-input draw-mode={viewerOverlay.DrawMode}");
                if (h != IntPtr.Zero)
                {
                    if (viewerOverlay.DrawMode)
                    {
                        WinApi.PostMouse(h, 0x0201, 600, 400); // WM_LBUTTONDOWN
                        for (var i = 1; i <= 8; i++)
                            WinApi.PostMouse(h, 0x0200, 600 + i * 12, 400 + i * 6); // WM_MOUSEMOVE
                        WinApi.PostMouse(h, 0x0202, 696, 448); // WM_LBUTTONUP
                    }
                    WinApi.PostHotkey(h, 1); // Ctrl+Alt+D → 观众切换绘画/观看态
                }
                result.Add("synth-input-posted");
            };
            t3.Start();

            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                result.Add($"host-info-received={hostInfoSeen}");
                if (overlayForInput is not null)
                    result.Add($"post-input draw-mode={overlayForInput.DrawMode} strokes={viewerForInput!.Store.Strokes.Count}");
                if (viewerOverlay is not null)
                {
                    var m = viewerOverlay.MapRectForSmoke;
                    result.Add($"viewer-map={m.X:0.0},{m.Y:0.0},{m.Width:0.0},{m.Height:0.0}");
                    result.Add($"alignment-rescans={viewerOverlay.AlignmentRescansForSmoke}");
                }
                try { File.WriteAllLines(Path.Combine(Path.GetTempPath(), "parsec-draw-smoke.log"), result); } catch { /* 日志尽力 */ }
                var mapOk = viewerOverlay is not null
                            && !viewerOverlay.MapRectForSmoke.IsEmpty
                            && viewerOverlay.MapRectForSmoke.Width > 0
                            && viewerOverlay.AlignmentRescansForSmoke > 0; // 窗口枚举真实跑过
                // 合成输入必须真的画出笔迹、热键必须真的切了模式
                var inputOk = overlayForInput is not null
                              && viewerForInput is not null
                              && viewerForInput.Store.Strokes.Count > 0
                              && overlayForInput.DrawMode == false; // 热键后应处于观看态
                Shutdown(hostInfoSeen && mapOk && trayOk && inputOk ? 0 : 1);
            };
            t.Start();
            return;
        }

        new ControlWindow().Show();
    }
}
