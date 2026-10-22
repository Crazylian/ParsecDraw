using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ParsecDraw.Draw;
using ParsecDraw.Net;
using ParsecDraw.Protocol;

namespace ParsecDraw;

/// <summary>
/// 无头自测：--selftest 参数触发，覆盖协议序列化、笔迹状态机（假时钟）、网络回环三块核心逻辑。
/// 结果写日志文件，退出码 0/1；UI 冒烟测试见 --smoke。
/// </summary>
public static class SelfTest
{
    public static async Task<int> RunAsync(string logPath)
    {
        var sb = new StringBuilder();
        var ok = true;

        void Check(bool cond, string name)
        {
            ok &= cond;
            sb.Append(cond ? "PASS  " : "FAIL  ").AppendLine(name);
        }

        // ---- 1. 协议序列化往返 ----
        try
        {
            var m = new Msg
            {
                T = MsgTypes.Begin,
                Id = "ab01-1",
                Name = "小明",
                Color = "#FF4757",
                W = 6,
                K = ToolKind.Laser,
                P = new double[][] { new[] { 0.5, 0.25 } },
            };
            var json = JsonSerializer.Serialize(m, AppJsonContext.Default.Msg);
            var back = JsonSerializer.Deserialize(json, AppJsonContext.Default.Msg)!;
            Check(back.T == MsgTypes.Begin && back.Id == "ab01-1" && back.Name == "小明"
                  && back.W == 6 && back.K == ToolKind.Laser
                  && back.P![0][0] == 0.5 && back.P[0][1] == 0.25,
                $"protocol roundtrip: {json}");
        }
        catch (Exception ex)
        {
            Check(false, "protocol roundtrip threw: " + ex.Message);
        }

        // ---- 2. 笔迹状态机（画笔淡出/剪除） ----
        try
        {
            long now = 10_000;
            var store = new StrokeStore(() => now);
            store.SetMap(new System.Windows.Rect(0, 0, 1920, 1080));
            store.Apply(new Msg
            {
                T = MsgTypes.Begin, Id = "ab01-1", Name = "小明",
                Color = "#FF4757", W = 6, K = ToolKind.Pen,
                P = new double[][] { new[] { 0.1, 0.1 } },
            });
            Check(store.Strokes.Count == 1 && store.OpacityOf(store.Strokes[0]) == 1, "pen: begin visible");

            store.Apply(new Msg
            {
                T = MsgTypes.Point, Id = "ab01-1",
                P = new double[][] { new[] { 0.2, 0.2 }, new[] { 0.3, 0.25 } },
            });
            Check(store.Strokes[0].Points.Count == 3 && store.Strokes[0].Geometry is not null,
                "pen: points appended, geometry built");

            store.Apply(new Msg { T = MsgTypes.End, Id = "ab01-1" });
            now += 3_999;
            Check(store.OpacityOf(store.Strokes[0]) == 1, "pen: hold phase");
            now += 500;
            var o = store.OpacityOf(store.Strokes[0]);
            Check(o > 0 && o < 1, $"pen: fading (alpha={o:0.00})");
            now += 600;
            Check(store.OpacityOf(store.Strokes[0]) == 0, "pen: fully faded");
            Check(store.Strokes.Count == 1 && !store.HasPendingFade(), "pen: faded stroke stops render loop");
            Check(store.PruneExpired(), "pen: prune removes expired");
            Check(store.Strokes.Count == 0 && !store.HasPendingFade(), "pen: idle after prune");
        }
        catch (Exception ex)
        {
            Check(false, "pen lifecycle threw: " + ex);
        }

        // ---- 2b. 激光笔 / 钉住 / 清空 / 撤销 ----
        try
        {
            long now = 100_000;
            var store = new StrokeStore(() => now);
            store.SetMap(new System.Windows.Rect(0, 0, 1920, 1080));

            store.Apply(new Msg
            {
                T = MsgTypes.Begin, Id = "ab01-2", Name = "L",
                Color = "#FFFFFF", W = 6, K = ToolKind.Laser,
                P = new double[][] { new[] { 0.5, 0.5 } },
            });
            store.Apply(new Msg { T = MsgTypes.End, Id = "ab01-2" });
            now += 300;
            Check(Math.Abs(store.OpacityOf(store.Strokes[0]) - 0.5) < 0.01, "laser: half faded");
            now += 400;
            Check(store.OpacityOf(store.Strokes[0]) == 0, "laser: gone");

            store.Apply(new Msg
            {
                T = MsgTypes.Begin, Id = "ab01-3", Name = "P",
                Color = "#FFFFFF", W = 6, K = ToolKind.Pen,
                P = new double[][] { new[] { 0.5, 0.5 } },
            });
            store.Apply(new Msg { T = MsgTypes.End, Id = "ab01-3" });
            store.Apply(new Msg { T = MsgTypes.Pin });
            now += 10_000;
            Check(store.OpacityOf(store.Strokes[^1]) == 1, "pin: stays visible");
            store.PruneExpired(); // 顺带剪掉早已淡出的激光笔
            Check(store.Strokes.Count == 1, "pin: not pruned");
            store.Apply(new Msg { T = MsgTypes.Clear });
            Check(store.Strokes.Count == 0, "clear: removes everything");

            store.Apply(new Msg
            {
                T = MsgTypes.Begin, Id = "ab01-4", Name = "U",
                Color = "#FFFFFF", W = 6, K = ToolKind.Pen,
                P = new double[][] { new[] { 0.5, 0.5 } },
            });
            Check(store.LastEndedStrokeOf("ab01") is null, "undo: active stroke not undoable");
            store.Apply(new Msg { T = MsgTypes.End, Id = "ab01-4" });
            Check(store.LastEndedStrokeOf("ab01")?.Id == "ab01-4", "undo: lookup finds ended stroke");
            store.Apply(new Msg { T = MsgTypes.Undo, Id = "ab01-4" });
            Check(store.Strokes.Count == 0, "undo: removes stroke");
        }
        catch (Exception ex)
        {
            Check(false, "store extras threw: " + ex);
        }

        // ---- 2c. 对齐/黑边补偿（letterbox 数学 + 映射矩形重映射） ----
        try
        {
            // 16:9 塞进 16:10 屏幕 → 上下黑边
            var r = Ui.AlignmentMath.FitInto(0, 0, 1920, 1200, 16.0 / 9);
            Check(Math.Abs(r.X) < 0.01 && Math.Abs(r.Y - 60) < 0.01
                  && Math.Abs(r.Width - 1920) < 0.01 && Math.Abs(r.Height - 1080) < 0.01,
                $"letterbox: 16:9 in 1920x1200 -> ({r.X:0},{r.Y:0},{r.Width:0},{r.Height:0})");

            // 16:9 塞进 4:3 窗口 → 左右黑边
            r = Ui.AlignmentMath.FitInto(0, 0, 1024, 768, 16.0 / 9);
            Check(Math.Abs(r.X) < 0.01 && Math.Abs(r.Y - 96) < 0.01
                  && Math.Abs(r.Width - 1024) < 0.01 && Math.Abs(r.Height - 576) < 0.01,
                "letterbox: 16:9 in 1024x768");

            // 同比例 → 铺满；带偏移的区域原点保留
            r = Ui.AlignmentMath.FitInto(100, 50, 800, 450, 16.0 / 9);
            Check(Math.Abs(r.X - 100) < 0.01 && Math.Abs(r.Y - 50) < 0.01
                  && Math.Abs(r.Width - 800) < 0.01 && Math.Abs(r.Height - 450) < 0.01,
                "letterbox: same aspect fills area, origin kept");

            // 非整屏映射下几何与标签都落在矩形内
            long now2 = 200_000;
            var store2 = new StrokeStore(() => now2);
            store2.SetMap(new System.Windows.Rect(100, 50, 800, 450));
            store2.Apply(new Msg
            {
                T = MsgTypes.Begin, Id = "ee03-1", Name = "对齐",
                Color = "#FFFFFF", W = 6, K = ToolKind.Pen,
                P = new double[][] { new[] { 0.5, 0.5 } },
            });
            var p = store2.ToScreen(new System.Windows.Point(0.5, 0.5));
            Check(Math.Abs(p.X - 500) < 0.01 && Math.Abs(p.Y - 275) < 0.01,
                $"map: ToScreen(0.5,0.5) in (100,50,800,450) -> ({p.X:0},{p.Y:0})");
            var g = store2.Strokes[0].Geometry!.Bounds;
            Check(Math.Abs(g.X - 500) < 0.01 && Math.Abs(g.Y - 275) < 0.01,
                "map: geometry respects map rect");
        }
        catch (Exception ex)
        {
            Check(false, "alignment threw: " + ex);
        }

        // ---- 2d. Win32 互操作真实调用（EntryPoint 拼错会在这里直接炸） ----
        try
        {
            var wins = Win32.WinApi.EnumTopLevelWindows();
            Check(wins.Count > 0, $"win32: enum windows ({wins.Count})");

            var rectOk = false;
            var dpiOk = false;
            foreach (var h in wins.Take(30))
            {
                if (!rectOk && Win32.WinApi.TryGetClientRect(h, out var rc) && rc.Width > 0 && rc.Height > 0)
                {
                    var pt = new Win32.WinApi.POINT();
                    if (Win32.WinApi.TryClientToScreen(h, ref pt))
                    {
                        rectOk = true;
                        Check(true, $"win32: client rect + to-screen ({rc.Width:0}x{rc.Height:0} at {pt.X},{pt.Y})");
                    }
                }
                if (!dpiOk && Win32.WinApi.GetWindowDpi(h) >= 96)
                    dpiOk = true;
                if (rectOk && dpiOk) break;
            }
            Check(rectOk, "win32: GetClientRect/ClientToScreen callable");
            Check(dpiOk, "win32: GetDpiForWindow callable");

            var (sw, sh) = Win32.WinApi.PrimaryScreenSize;
            Check(sw > 0 && sh > 0, $"win32: primary screen {sw}x{sh}");
        }
        catch (Exception ex)
        {
            Check(false, "win32 interop threw: " + ex);
        }

        // ---- 3. 网络回环（真实 socket） ----
        try
        {
            await using var server = new DrawServer();
            server.Start(0);
            var serverGot = NewTcs<Msg>();
            server.Received += m => serverGot.TrySetResult(m);

            await using var client1 = new DrawClient();
            await using var client2 = new DrawClient();
            var c1Connected = NewTcs<bool>();
            var c2Connected = NewTcs<bool>();
            var c2Got = NewTcs<Msg>();
            client1.StateChanged += s => { if (s == ConnState.Connected) c1Connected.TrySetResult(true); };
            client2.StateChanged += s => { if (s == ConnState.Connected) c2Connected.TrySetResult(true); };
            client2.Received += m => c2Got.TrySetResult(m);

            var url = $"ws://127.0.0.1:{server.BoundPort}";
            client1.Start(url);
            client2.Start(url);
            Check(await Wait(c1Connected.Task, 5000).ConfigureAwait(false)
                  && await Wait(c2Connected.Task, 5000).ConfigureAwait(false),
                "net: both clients connected");

            var msg = new Msg
            {
                T = MsgTypes.Begin, Id = "cd02-1", Name = "观众乙",
                Color = "#2ED573", W = 6, K = ToolKind.Pen,
                P = new double[][] { new[] { 0.25, 0.75 } },
            };
            client1.Send(msg);
            var atServer = await Wait(serverGot.Task, 5000).ConfigureAwait(false);
            var atClient2 = await Wait(c2Got.Task, 5000).ConfigureAwait(false);
            Check(atServer && atClient2, "net: begin relayed to server + peer");
            if (atClient2)
            {
                var got = await c2Got.Task;
                Check(got.Id == "cd02-1" && got.Name == "观众乙", "net: fields intact");
            }

            // 权限：c2 撤销 c1 的笔迹必须被服务器拦截（1.2s 内不得广播）
            var c1Got = NewTcs<Msg>();
            client1.Received += m => c1Got.TrySetResult(m);
            client2.Send(new Msg { T = MsgTypes.Undo, Id = "cd02-1" });
            Check(!await Wait(c1Got.Task, 1200).ConfigureAwait(false), "net: foreign undo blocked");

            // 自己的 undo 放行（c1 发 → 广播给 c2）
            var c2Got2 = NewTcs<Msg>();
            client2.Received += m => c2Got2.TrySetResult(m);
            client1.Send(new Msg { T = MsgTypes.Undo, Id = "cd02-1" });
            Check(await Wait(c2Got2.Task, 3000).ConfigureAwait(false), "net: own undo allowed");

            // 主机分辨率通告：SetHostScreen 广播给已连接观众
            var c1Host = NewTcs<Msg>();
            client1.Received += m => { if (m.T == MsgTypes.Host) c1Host.TrySetResult(m); };
            server.SetHostScreen(2560, 1440);
            var gotHost = await Wait(c1Host.Task, 3000).ConfigureAwait(false);
            Check(gotHost, "net: host screen broadcast");
            if (gotHost)
            {
                var hm = await c1Host.Task;
                Check(hm.W == 2560 && hm.H == 1440, "net: host screen fields intact");
            }

            // 观众伪造 host 消息必须被服务器拦截
            var c1Fake = NewTcs<Msg>();
            client1.Received += m => { if (m.T == MsgTypes.Host && c1Host.Task.IsCompleted) c1Fake.TrySetResult(m); };
            client2.Send(new Msg { T = MsgTypes.Host, W = 1, H = 1 });
            Check(!await Wait(c1Fake.Task, 1200).ConfigureAwait(false), "net: forged host blocked");

            // 非握手 HTTP → 状态页
            using var http = new System.Net.Sockets.TcpClient("127.0.0.1", server.BoundPort);
            var httpStream = http.GetStream();
            var req = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");
            await httpStream.WriteAsync(req).ConfigureAwait(false);
            var respBuf = new MemoryStream();
            var chunk = new byte[4096];
            int n;
            while ((n = await httpStream.ReadAsync(chunk).ConfigureAwait(false)) > 0)
                respBuf.Write(chunk, 0, n);
            var respText = Encoding.ASCII.GetString(respBuf.ToArray());
            Check(respText.StartsWith("HTTP/1.1 200", StringComparison.Ordinal)
                  && respText.Contains("ParsecDraw", StringComparison.OrdinalIgnoreCase),
                "net: http status page");
        }
        catch (Exception ex)
        {
            Check(false, "net loopback threw: " + ex);
        }

        // ---- 3b. 局域网发现（真实 UDP 回环；responder 随机端口 + scanner 单播，不碰真实发现端口） ----
        try
        {
            // 应答负载序列化往返：中文名与端口字段完整
            var ansJson = JsonSerializer.Serialize(
                new DiscoveryAnswer { Magic = Discovery.AnswerMagic, Name = "测试主机", Port = 49999 },
                AppJsonContext.Default.DiscoveryAnswer);
            var ansBack = JsonSerializer.Deserialize(ansJson, AppJsonContext.Default.DiscoveryAnswer);
            Check(ansBack is { Name: "测试主机", Port: 49999 }, "discovery: answer roundtrip");

            var hostName = "测试主机";
            using var responder = new DiscoveryResponder();
            Check(responder.Start(0, () => new DiscoveryAnswer
                  { Magic = Discovery.AnswerMagic, Name = hostName, Port = 49999 })
                  && responder.BoundPort > 0,
                "discovery: responder on random port");

            using var scanner = new DiscoveryScanner();
            var found = NewTcs<IPEndPoint>();
            DiscoveryAnswer? foundAns = null;
            scanner.HostFound += (a, ep) => { foundAns = a; found.TrySetResult(ep); };
            var completed = NewTcs<bool>();
            scanner.Completed += () => completed.TrySetResult(true);

            var target = new IPEndPoint(IPAddress.Loopback, responder.BoundPort);
            scanner.Scan(target, TimeSpan.FromSeconds(3));
            var gotFound = await Wait(found.Task, 4000).ConfigureAwait(false);
            Check(gotFound, "discovery: probe -> answer");
            if (gotFound)
                Check(foundAns is { Name: "测试主机", Port: 49999 }
                      && (await found.Task).Port == responder.BoundPort,
                    "discovery: answer fields + source endpoint");
            Check(await Wait(completed.Task, 4500).ConfigureAwait(false), "discovery: window completes");

            // 改名即时生效：应答每次现取，无需重启响应器
            var renamed = NewTcs<bool>();
            scanner.HostFound += (a, _) => { if (a.Name == "新名字") renamed.TrySetResult(true); };
            hostName = "新名字";
            scanner.Scan(target, TimeSpan.FromSeconds(3));
            Check(await Wait(renamed.Task, 4000).ConfigureAwait(false), "discovery: rename takes effect immediately");

            // 错误 magic 的探测不得换来应答（1s 内裸 socket 收不到任何包）
            using (var raw = new UdpClient(0))
            {
                await raw.SendAsync(Encoding.ASCII.GetBytes("NOTPARSEC"), 8, target).ConfigureAwait(false);
                Check(!await Wait(raw.ReceiveAsync(), 1000).ConfigureAwait(false), "discovery: wrong probe magic ignored");
            }

            // 假主机回错误 magic 的应答：扫描器不得上报
            using (var fakeHost = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            {
                var fakePort = ((IPEndPoint)fakeHost.Client.LocalEndPoint!).Port;
                _ = Task.Run(async () =>
                {
                    var r = await fakeHost.ReceiveAsync().ConfigureAwait(false);
                    var bad = Encoding.UTF8.GetBytes("{\"magic\":\"WRONG\",\"name\":\"假主机\",\"port\":1}");
                    await fakeHost.SendAsync(bad, bad.Length, r.RemoteEndPoint).ConfigureAwait(false);
                });
                var fooled = NewTcs<bool>();
                scanner.HostFound += (a, _) => { if (a.Name == "假主机") fooled.TrySetResult(true); };
                scanner.Scan(new IPEndPoint(IPAddress.Loopback, fakePort), TimeSpan.FromSeconds(1));
                Check(!await Wait(fooled.Task, 2000).ConfigureAwait(false), "discovery: wrong answer magic ignored");
            }

            // 响应器停止后不再应答（生命周期严格随主机会话）
            responder.Stop();
            var afterStop = NewTcs<bool>();
            scanner.HostFound += (a, _) => { if (a.Name == "新名字") afterStop.TrySetResult(true); };
            scanner.Scan(target, TimeSpan.FromSeconds(1));
            Check(!await Wait(afterStop.Task, 2000).ConfigureAwait(false), "discovery: no answer after stop");
        }
        catch (Exception ex)
        {
            Check(false, "discovery threw: " + ex);
        }

        sb.AppendLine();
        sb.AppendLine(ok ? "ALL PASSED" : "SOME TESTS FAILED");
        try { File.WriteAllText(logPath, sb.ToString()); } catch { /* 写不了就算了 */ }
        return ok ? 0 : 1;
    }

    private static TaskCompletionSource<T> NewTcs<T>()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<bool> Wait(Task task, int timeoutMs)
    {
        if (task.IsCompleted) return true;
        if (timeoutMs <= 0) return false;
        var done = await Task.WhenAny(task, Task.Delay(timeoutMs)).ConfigureAwait(false);
        return done == task && task.IsCompleted;
    }
}
