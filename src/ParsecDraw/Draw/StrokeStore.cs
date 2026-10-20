using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ParsecDraw.Protocol;

namespace ParsecDraw.Draw;

/// <summary>缓存冻结画刷，避免每帧分配。</summary>
public static class BrushCache
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SolidColorBrush> Cache = new();

    public static Brush Get(string hex)
        => Cache.GetOrAdd(hex, static h =>
        {
            var b = new SolidColorBrush(ParseHex(h));
            b.Freeze();
            return b;
        });

    public static Color ParseHex(string hex)
    {
        // 支持 #RGB / #RRGGBB / #AARRGGBB
        var s = hex.TrimStart('#');
        switch (s.Length)
        {
            case 3:
                return Color.FromRgb(
                    Convert.ToByte(new string(s[0], 2), 16),
                    Convert.ToByte(new string(s[1], 2), 16),
                    Convert.ToByte(new string(s[2], 2), 16));
            case 6:
                return Color.FromRgb(
                    byte.Parse(s[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    byte.Parse(s[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    byte.Parse(s[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            case 8:
                return Color.FromArgb(
                    byte.Parse(s[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    byte.Parse(s[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    byte.Parse(s[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    byte.Parse(s[6..8], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            default:
                return Colors.White;
        }
    }
}

/// <summary>一条笔迹。几何与画笔在 UI 线程构建并 Freeze。</summary>
public sealed class Stroke
{
    public required string Id { get; init; }
    public required string Author { get; init; }
    public required ToolKind Tool { get; init; }

    /// <summary>基准粗细（px @1080p）。</summary>
    public required double Width { get; init; }
    public required string ColorHex { get; init; }

    public bool Ended { get; internal set; }
    public bool Pinned { get; internal set; }

    /// <summary>最近一次活动的本机时间戳（Environment.TickCount64 毫秒），淡出计时基于它。</summary>
    public long LastActivity { get; internal set; }

    /// <summary>归一化坐标 [0,1]。</summary>
    public List<Point> Points { get; } = new();

    public Geometry? Geometry { get; internal set; }
    public Pen? Pen { get; internal set; }
}

/// <summary>
/// 笔迹仓库：唯一的绘制状态源。只在 UI 线程访问（网络消息由 Session 转发到 Dispatcher）。
/// 渲染策略：每条笔迹缓存 StreamGeometry + 冻结 Pen，淡出只改透明度不重建几何；
/// 淡出计时用"每条笔迹最后活动时间"，避免跨机器时钟同步问题。
/// </summary>
public sealed class StrokeStore
{
    /// <summary>画笔：结束后保持 4s，第 5s 内淡出。</summary>
    public const double PenHoldMs = 4000, PenFadeMs = 1000;
    /// <summary>激光笔：结束后 0.6s 内淡出。</summary>
    public const double LaserLifeMs = 600;

    private readonly List<Stroke> _strokes = new();
    private readonly Dictionary<string, Stroke> _byId = new(StringComparer.Ordinal);
    private readonly Func<long> _clock;

    private double _mapX, _mapY, _mapW = 1920, _mapH = 1080;

    public StrokeStore(Func<long>? clock = null)
        => _clock = clock ?? (() => Environment.TickCount64);

    /// <summary>当前应渲染的笔迹（未过期、未剪除）。</summary>
    public IReadOnlyList<Stroke> Strokes => _strokes;

    public event Action? Changed;

    public Stroke? GetById(string id) => _byId.GetValueOrDefault(id);

    /// <summary>归一化坐标 → 画布 DIP 坐标（按当前映射矩形）。</summary>
    public Point ToScreen(Point normalized)
        => new(_mapX + normalized.X * _mapW, _mapY + normalized.Y * _mapH);

    /// <summary>
    /// 整笔橡皮命中：找距屏幕点 radiusDip 内、id 前缀匹配作者（null=不限）的笔迹，
    /// 后画的优先（视觉在上层）。点距近似线距（采样密集）。
    /// </summary>
    public Stroke? FindStrokeNear(Point screenPt, double radiusDip, string? authorPrefix)
    {
        var r2 = radiusDip * radiusDip;
        for (var i = _strokes.Count - 1; i >= 0; i--)
        {
            var s = _strokes[i];
            if (authorPrefix is not null && !s.Id.StartsWith(authorPrefix + "-", StringComparison.Ordinal))
                continue;
            foreach (var p in s.Points)
            {
                var sp = ToScreen(p);
                var dx = sp.X - screenPt.X;
                var dy = sp.Y - screenPt.Y;
                if (dx * dx + dy * dy <= r2)
                    return s;
            }
        }
        return null;
    }

    /// <summary>某人最后一条已结束的笔迹（撤销用）。</summary>
    public Stroke? LastEndedStrokeOf(string clientId)
    {
        for (var i = _strokes.Count - 1; i >= 0; i--)
        {
            var s = _strokes[i];
            if (s.Ended && s.Id.StartsWith(clientId + "-", StringComparison.Ordinal))
                return s;
        }
        return null;
    }

    /// <summary>
    /// 设定映射矩形（画布 DIP 坐标）：[0,1] 归一化坐标渲染/输入都落在该矩形内。
    /// 主机端恒为 (0,0,屏宽,屏高)；观众端为 Parsec 视频画面实际所在区域（黑边补偿）。
    /// </summary>
    public void SetMap(Rect map)
    {
        if (map.Width <= 0 || map.Height <= 0 || double.IsNaN(map.X) || double.IsNaN(map.Y)) return;
        if (Math.Abs(map.X - _mapX) < 0.5 && Math.Abs(map.Y - _mapY) < 0.5
            && Math.Abs(map.Width - _mapW) < 0.5 && Math.Abs(map.Height - _mapH) < 0.5) return;
        _mapX = map.X;
        _mapY = map.Y;
        _mapW = map.Width;
        _mapH = map.Height;
        foreach (var s in _strokes)
        {
            RebuildGeometry(s);
            RebuildPen(s);
        }
        Raise();
    }

    /// <summary>应用一条协议消息（本地产生或网络收到）。</summary>
    public void Apply(Msg m)
    {
        var now = _clock();
        switch (m.T)
        {
            case MsgTypes.Begin:
            {
                if (m.Id is null || _byId.ContainsKey(m.Id)) return;
                var s = new Stroke
                {
                    Id = m.Id,
                    Author = m.Name ?? "",
                    Tool = m.K,
                    Width = m.W <= 0 ? 6 : m.W,
                    ColorHex = string.IsNullOrEmpty(m.Color) ? "#FFFFFF" : m.Color,
                };
                s.LastActivity = now;
                if (m.P is not null) AppendPoints(s, m.P);
                _strokes.Add(s);
                _byId[m.Id] = s;
                RebuildGeometry(s);
                RebuildPen(s);
                break;
            }
            case MsgTypes.Point:
            {
                if (m.Id is null || !_byId.TryGetValue(m.Id, out var s) || m.P is null) return;
                AppendPoints(s, m.P);
                s.LastActivity = now;
                RebuildGeometry(s);
                break;
            }
            case MsgTypes.End:
            {
                if (m.Id is null || !_byId.TryGetValue(m.Id, out var s)) return;
                s.Ended = true;
                s.Pinned = m.Pin;
                s.LastActivity = now;
                break;
            }
            case MsgTypes.Undo:
            {
                if (m.Id is null) return;
                Remove(m.Id);
                break;
            }
            case MsgTypes.Clear:
                _strokes.Clear();
                _byId.Clear();
                break;
            case MsgTypes.Pin:
                // 只钉住当前可见的笔迹；已淡出的不该被"救活"（否则永远无法剪除）
                foreach (var s in _strokes)
                {
                    if (s.Ended && !s.Pinned && OpacityOf(s) > 0)
                        s.Pinned = true;
                }
                break;
            default:
                return; // join 等消息不涉及绘制状态
        }
        Raise();
    }

    /// <summary>透明度：0（不可见）~ 1（完全不透明）。钉住/未结束恒为 1。</summary>
    public double OpacityOf(Stroke s)
    {
        if (s.Pinned || !s.Ended) return 1;
        var age = _clock() - s.LastActivity;
        if (s.Tool == ToolKind.Laser)
        {
            return age >= LaserLifeMs ? 0 : 1 - age / LaserLifeMs;
        }
        if (age <= PenHoldMs) return 1;
        if (age >= PenHoldMs + PenFadeMs) return 0;
        return 1 - (age - PenHoldMs) / PenFadeMs;
    }

    /// <summary>是否还有需要逐帧重绘的笔迹（淡出中或终将淡出）。无则渲染循环可完全停止。</summary>
    public bool HasPendingFade()
    {
        foreach (var s in _strokes)
        {
            if (!s.Pinned && OpacityOf(s) > 0)
                return true;
        }
        return false;
    }

    /// <summary>剪除已完全淡出的笔迹，防止长会话内存增长。返回是否有剪除发生。</summary>
    public bool PruneExpired()
    {
        var removed = false;
        for (var i = _strokes.Count - 1; i >= 0; i--)
        {
            var s = _strokes[i];
            if (!s.Pinned && s.Ended && OpacityOf(s) <= 0)
            {
                _strokes.RemoveAt(i);
                _byId.Remove(s.Id);
                removed = true;
            }
        }
        if (removed) Raise();
        return removed;
    }

    public void ClearAll()
    {
        _strokes.Clear();
        _byId.Clear();
        Raise();
    }

    private void Remove(string id)
    {
        if (!_byId.Remove(id)) return;
        for (var i = 0; i < _strokes.Count; i++)
        {
            if (_strokes[i].Id == id)
            {
                _strokes.RemoveAt(i);
                break;
            }
        }
        Raise();
    }

    private static void AppendPoints(Stroke s, double[][] pts)
    {
        foreach (var p in pts)
        {
            if (p.Length >= 2)
                s.Points.Add(new Point(p[0], p[1]));
        }
    }

    /// <summary>用中点二次贝塞尔平滑重建几何并冻结。点数最多几百条，重建在微秒级。</summary>
    private void RebuildGeometry(Stroke s)
    {
        var n = s.Points.Count;
        if (n == 0)
        {
            s.Geometry = null;
            return;
        }
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            var p0 = ToDip(s.Points[0]);
            ctx.BeginFigure(p0, false, false);
            if (n == 1)
            {
                // 单点：微线段 + 圆帽 = 圆点
                ctx.LineTo(new Point(p0.X + 0.01, p0.Y), true, false);
            }
            else
            {
                for (var i = 1; i < n - 1; i++)
                {
                    var c = ToDip(s.Points[i]);
                    var nx = ToDip(s.Points[i + 1]);
                    var mid = new Point((c.X + nx.X) / 2, (c.Y + nx.Y) / 2);
                    ctx.QuadraticBezierTo(c, mid, true, false);
                }
                ctx.LineTo(ToDip(s.Points[n - 1]), true, false);
            }
        }
        geo.Freeze();
        s.Geometry = geo;
    }

    private void RebuildPen(Stroke s)
    {
        var scale = Math.Clamp(_mapH / 1080.0, 0.5, 3.0);
        var pen = new Pen(BrushCache.Get(s.ColorHex), Math.Max(1, s.Width * scale))
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();
        s.Pen = pen;
    }

    private Point ToDip(Point normalized) => new(_mapX + normalized.X * _mapW, _mapY + normalized.Y * _mapH);

    private void Raise() => Changed?.Invoke();
}
