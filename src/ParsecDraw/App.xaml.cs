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

        // 冒烟测试：起 UI 3 秒后自动退出
        if (args.Contains("--smoke"))
        {
            new ControlWindow().Show();
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                Shutdown(0);
            };
            t.Start();
            return;
        }

        new ControlWindow().Show();
    }
}
