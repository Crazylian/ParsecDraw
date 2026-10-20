using System.IO;
using System.Windows;
using System.Windows.Threading;
using ParsecDraw.Ui;

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

        // --multi：跳过单实例限制，供同一台机器双开做主机/观众联测
        if (!args.Contains("--multi"))
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

            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                result.Add($"host-info-received={hostInfoSeen}");
                if (viewerOverlay is not null)
                {
                    var m = viewerOverlay.MapRectForSmoke;
                    result.Add($"viewer-map={m.X:0.0},{m.Y:0.0},{m.Width:0.0},{m.Height:0.0}");
                }
                try { File.WriteAllLines(Path.Combine(Path.GetTempPath(), "parsec-draw-smoke.log"), result); } catch { /* 日志尽力 */ }
                var mapOk = viewerOverlay is not null
                            && !viewerOverlay.MapRectForSmoke.IsEmpty
                            && viewerOverlay.MapRectForSmoke.Width > 0;
                Shutdown(hostInfoSeen && mapOk ? 0 : 1);
            };
            t.Start();
            return;
        }

        new ControlWindow().Show();
    }
}
