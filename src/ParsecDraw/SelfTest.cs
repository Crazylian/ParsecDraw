using System.IO;
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
            store.SetViewport(1920, 1080);
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
            store.SetViewport(1920, 1080);

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
