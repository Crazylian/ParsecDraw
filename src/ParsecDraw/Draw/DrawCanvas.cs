using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ParsecDraw.Protocol;

namespace ParsecDraw.Draw;

/// <summary>
/// 覆盖层画布：纯渲染元件，不参与命中测试/输入。
/// WPF 保留模式 —— 只在笔迹状态变化时 InvalidateVisual；无淡出中笔迹时帧循环完全停止（零 CPU）。
/// </summary>
public sealed class DrawCanvas : FrameworkElement
{
    private const int FadeFps = 30;

    private StrokeStore? _store;
    private DispatcherTimer? _fadeTimer;

    private static readonly Typeface LabelTypeface = new("Segoe UI");
    private static readonly Brush LabelBrush = MakeFrozen(Colors.White);
    private static readonly Brush LabelBg = MakeFrozen(Color.FromArgb(160, 0, 0, 0));

    public DrawCanvas()
        => IsHitTestVisible = false;

    public void Init(StrokeStore store)
    {
        _store = store;
        store.Changed += OnStoreChanged;
        SizeChanged += (_, e) => store.SetViewport(e.NewSize.Width, e.NewSize.Height);
    }

    private static Brush MakeFrozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private void OnStoreChanged()
    {
        _store?.PruneExpired();
        InvalidateVisual();
        UpdateFadeTimer();
    }

    private void UpdateFadeTimer()
    {
        if (_store is null) return;
        var needed = _store.HasPendingFade();
        if (needed && _fadeTimer is null)
        {
            _fadeTimer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(1000 / FadeFps),
                DispatcherPriority.Render,
                (_, _) => OnFadeTick(),
                Dispatcher);
        }
        else if (!needed && _fadeTimer is not null)
        {
            _fadeTimer.Stop();
            _fadeTimer = null;
        }
        else if (needed)
        {
            _fadeTimer!.Start();
        }
    }

    private void OnFadeTick()
    {
        if (_store is null) return;
        _store.PruneExpired();
        InvalidateVisual();
        if (!_store.HasPendingFade())
            UpdateFadeTimer(); // 全部淡完 → 停表归零 CPU
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_store is null) return;
        var nowVisible = _store.Strokes;
        if (nowVisible.Count == 0) return;

        foreach (var s in nowVisible)
        {
            var alpha = _store.OpacityOf(s);
            if (alpha <= 0 || s.Geometry is null || s.Pen is null) continue;

            var pushed = false;
            if (alpha < 1)
            {
                dc.PushOpacity(alpha);
                pushed = true;
            }
            try
            {
                dc.DrawGeometry(null, s.Pen, s.Geometry);

                // 作者昵称标签：画在笔迹起点上方，随笔迹一起淡出
                if (s.Points.Count > 0 && !string.IsNullOrEmpty(s.Author))
                {
                    var p0 = new Point(s.Points[0].X * ActualWidth, s.Points[0].Y * ActualHeight);
                    var ft = new FormattedText(
                        s.Author,
                        CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        LabelTypeface,
                        12,
                        LabelBrush,
                        VisualTreeHelper.GetDpi(this).PixelsPerDip);
                    dc.DrawRoundedRectangle(LabelBg, null, new Rect(p0.X + 5, p0.Y - 17, ft.Width + 7, ft.Height + 3), 3, 3);
                    dc.DrawText(ft, new Point(p0.X + 8, p0.Y - 15.5));
                }
            }
            finally
            {
                if (pushed) dc.Pop();
            }
        }
    }
}
