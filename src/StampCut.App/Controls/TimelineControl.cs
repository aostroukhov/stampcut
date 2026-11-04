using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using StampCut.Core;
using StampCut.Core.Models;

namespace StampCut.App.Controls;

/// <summary>Редактируемый интервал на ленте (ретушь, запикивание): перетаскиваются обе границы.</summary>
public sealed class TimelineMarker
{
    public required object Tag { get; init; }
    public double From { get; set; }
    public double To { get; set; }
    public required Brush Brush { get; init; }
    public int Lane { get; init; }
    public string Label { get; init; } = "";
    /// <summary>Моменты ключевых точек движения — рисуются ромбиками на полосе.</summary>
    public List<double> KeyTimes { get; init; } = new();
}

/// <summary>
/// Лента времени: масштаб (Ctrl+колесо), прокрутка (колесо), фрагменты с перетаскиваемыми границами,
/// волна звука, интервалы ретуши и запикивания с перетаскиваемыми границами, ключевые кадры, контекстное меню.
/// </summary>
public sealed class TimelineControl : FrameworkElement
{
    public double Duration { get; set; }
    public double Position { get; set; }
    public List<Segment> Segments { get; set; } = new();
    public Segment? ActiveSegment { get; set; }
    public List<TimelineMarker> Markers { get; } = new();
    public object? SelectedMarkerTag { get; set; }
    public IReadOnlyList<double> Keyframes { get; set; } = Array.Empty<double>();
    public bool SnapToKeyframes { get; set; }
    public bool ShowKeyframes { get; set; } = true;
    /// <summary>Огибающая звука 0..1, WaveformRate значений в секунду; null — нет звука или ещё считается.</summary>
    public float[]? Waveform { get; set; }
    /// <summary>Полоска кадров (N кадров в ряд) на всю длительность ролика.</summary>
    public System.Windows.Media.Imaging.BitmapSource? Filmstrip { get; set; }
    public int FilmstripFrames { get; set; } = 40;
    public double WaveformRate { get; set; } = 100;
    public bool Muted { get; set; }

    // окно просмотра
    public double ViewStart { get; private set; }
    public double ViewDuration { get; private set; }
    public bool IsZoomed => Duration > 0 && ViewDuration < Duration - 1e-6;

    public event Action<double>? SeekRequested;
    public event Action<Segment, bool, double>? SegmentEdgeChanged;
    public event Action<Segment?>? ActiveSegmentChanged;
    public event Action<TimelineMarker, bool, double>? MarkerEdgeChanged;
    public event Action<TimelineMarker?>? MarkerSelected;
    public event Action? DragCompleted;
    public event Action<double>? ContextRequested;

    private enum Drag { None, Seek, SegmentEdge, MarkerEdge, Pan }
    private Drag _drag;
    private Segment? _dragSegment;
    private TimelineMarker? _dragMarker;
    private bool _dragIsIn;
    private double _panStartX, _panStartView;
    public bool IsDragging => _drag != Drag.None;

    private const double Pad = 10;
    private const double RulerH = 14, StripTop = 15, StripH = 40, TrackTop = 57, TrackH = 20, WaveTop = 80, WaveH = 24, LaneTop = 108, LaneH = 5;

    public TimelineControl()
    {
        Height = 132;
        Focusable = false;
        Cursor = Cursors.Hand;
        ClipToBounds = true;
    }

    public void ResetView() { ViewStart = 0; ViewDuration = Duration; InvalidateVisual(); }
    public void SetDuration(double d) { Duration = d; ResetView(); }

    private double W => Math.Max(1, ActualWidth - 2 * Pad);
    private double View => ViewDuration > 0 ? ViewDuration : Duration;
    private double X(double t) => Pad + (View > 0 ? (t - ViewStart) / View : 0) * W;
    private double T(double x) => View > 0 ? Math.Clamp(ViewStart + (x - Pad) / W * View, 0, Duration) : 0;

    /// <summary>Масштаб вокруг точки времени: factor &gt; 1 — приблизить.</summary>
    public void Zoom(double factor, double anchorTime)
    {
        if (Duration <= 0) return;
        double newView = Math.Clamp(View / factor, Math.Min(Duration, 0.5), Duration);
        double frac = View > 0 ? (anchorTime - ViewStart) / View : 0.5;
        ViewStart = Math.Clamp(anchorTime - frac * newView, 0, Math.Max(0, Duration - newView));
        ViewDuration = newView;
        InvalidateVisual();
    }

    public void Pan(double deltaSec)
    {
        if (!IsZoomed) return;
        ViewStart = Math.Clamp(ViewStart + deltaSec, 0, Math.Max(0, Duration - View));
        InvalidateVisual();
    }

    /// <summary>Держать позицию в окне (при воспроизведении в увеличенном масштабе).</summary>
    public void EnsureVisible(double t)
    {
        if (!IsZoomed) return;
        if (t < ViewStart || t > ViewStart + View) { ViewStart = Math.Clamp(t - View * 0.1, 0, Math.Max(0, Duration - View)); InvalidateVisual(); }
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double h = ActualHeight;
        var trackColor = Ui.ThemeColor("ControlStrongFillColorDisabledBrush", Color.FromRgb(0xE1, 0xE4, 0xE8));
        var accentColor = Ui.ThemeColor("AccentFillColorDefaultBrush", Color.FromRgb(0x00, 0x67, 0xC0));
        var textColor = Ui.ThemeColor("TextFillColorSecondaryBrush", Color.FromRgb(0x6B, 0x72, 0x80));
        var track = Ui.Solid(trackColor); var accent = Ui.Solid(accentColor); var textBrush = Ui.Solid(textColor);
        var wave = Ui.Solid(accentColor, Muted ? 0.25 : 0.7);
        var playhead = Ui.Danger;

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, h));
        if (Duration <= 0) { dc.DrawRoundedRectangle(track, null, new Rect(Pad, TrackTop, W, TrackH), 4, 4); return; }
        if (ViewDuration <= 0) ViewDuration = Duration;

        // линейка
        var tf = new Typeface("Segoe UI");
        double step = NiceStep(View / Math.Max(2, W / 90));
        for (double t = Math.Ceiling(ViewStart / step) * step; t <= ViewStart + View + 1e-9; t += step)
        {
            double x = X(t);
            dc.DrawLine(new Pen(textBrush, 1) { Brush = Ui.Solid(textColor, 0.5) }, new Point(x, RulerH - 4), new Point(x, RulerH));
            var ft = new FormattedText(TimeFormat.Format(t, step < 1), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tf, 10, textBrush, 1.0);
            dc.DrawText(ft, new Point(x + 2, 0));
        }

        // полоска кадров: показываем ту часть, что в окне просмотра
        if (Filmstrip is { } strip && Duration > 0)
        {
            double fx = ViewStart / Duration * strip.PixelWidth, fw = View / Duration * strip.PixelWidth;
            if (fw >= 1)
            {
                dc.PushClip(new RectangleGeometry(new Rect(Pad, StripTop, W, StripH)));
                try
                {
                    var crop = new System.Windows.Media.Imaging.CroppedBitmap(strip, new Int32Rect((int)Math.Clamp(fx, 0, strip.PixelWidth - 1), 0, (int)Math.Clamp(fw, 1, strip.PixelWidth - (int)Math.Clamp(fx, 0, strip.PixelWidth - 1)), strip.PixelHeight));
                    dc.DrawImage(crop, new Rect(Pad, StripTop, W, StripH));
                }
                catch { }
                dc.Pop();
            }
        }
        else dc.DrawRectangle(Ui.Solid(trackColor, 0.35), null, new Rect(Pad, StripTop, W, StripH));

        // дорожка и фрагменты
        dc.DrawRoundedRectangle(track, null, new Rect(Pad, TrackTop, W, TrackH), 4, 4);
        foreach (var s in Segments)
        {
            bool active = ReferenceEquals(s, ActiveSegment);
            double x1 = Math.Max(Pad, X(s.InSec)), x2 = Math.Min(Pad + W, X(s.OutSec));
            if (x2 <= x1) continue;
            dc.DrawRectangle(Ui.Solid(accentColor, active ? 0.55 : 0.28), null, new Rect(x1, TrackTop, x2 - x1, TrackH));
        }

        // волна звука
        dc.DrawRectangle(Ui.Solid(trackColor, 0.5), null, new Rect(Pad, WaveTop, W, WaveH));
        if (Waveform is { Length: > 0 } wf)
        {
            double mid = WaveTop + WaveH / 2, half = WaveH / 2 - 1;
            int cols = (int)W;
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                for (int px = 0; px < cols; px++)
                {
                    double t0 = ViewStart + (double)px / W * View, t1 = ViewStart + (double)(px + 1) / W * View;
                    int i0 = (int)(t0 * WaveformRate), i1 = Math.Max(i0 + 1, (int)(t1 * WaveformRate));
                    float peak = 0;
                    for (int i = i0; i < i1 && i < wf.Length; i++) if (wf[i] > peak) peak = wf[i];
                    double a = Math.Max(0.5, peak * half);
                    g.BeginFigure(new Point(Pad + px, mid - a), true, true);
                    g.LineTo(new Point(Pad + px + 1, mid - a), false, false);
                    g.LineTo(new Point(Pad + px + 1, mid + a), false, false);
                    g.LineTo(new Point(Pad + px, mid + a), false, false);
                }
            }
            geo.Freeze();
            dc.DrawGeometry(wave, null, geo);
            if (Muted)
            {
                var ft = new FormattedText("🔇", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tf, 12, textBrush, 1.0);
                dc.DrawText(ft, new Point(Pad + 4, WaveTop + 5));
            }
        }

        // ключевые кадры
        if (ShowKeyframes || SnapToKeyframes)
        {
            var kf = new Pen(Ui.Solid(textColor, 0.35), 1);
            foreach (var k in Keyframes)
            {
                if (k < ViewStart || k > ViewStart + View) continue;
                double x = X(k);
                dc.DrawLine(kf, new Point(x, TrackTop), new Point(x, TrackTop + TrackH));
            }
        }

        // интервалы ретуши/запикивания
        foreach (var m in Markers)
        {
            double y = LaneTop + m.Lane * (LaneH + 3);
            double x1 = Math.Max(Pad, X(m.From)), x2 = Math.Min(Pad + W, X(m.To));
            if (x2 <= x1) continue;
            bool sel = ReferenceEquals(m.Tag, SelectedMarkerTag);
            dc.DrawRectangle(m.Brush, sel ? new Pen(Brushes.White, 1) : null, new Rect(x1, y - (sel ? 1 : 0), x2 - x1, LaneH + (sel ? 2 : 0)));
            foreach (var kt in m.KeyTimes)
            {
                if (kt < ViewStart || kt > ViewStart + View) continue;
                double kx = X(kt), ky = y + LaneH / 2;
                var d = new StreamGeometry();
                using (var g = d.Open()) { g.BeginFigure(new Point(kx, ky - 5), true, true); g.LineTo(new Point(kx + 4, ky), true, false); g.LineTo(new Point(kx, ky + 5), true, false); g.LineTo(new Point(kx - 4, ky), true, false); }
                dc.DrawGeometry(Brushes.White, new Pen(m.Brush, 1), d);
            }
        }

        // маркеры фрагментов
        foreach (var s in Segments)
        {
            bool active = ReferenceEquals(s, ActiveSegment);
            if (s.InSec >= ViewStart && s.InSec <= ViewStart + View) DrawMarker(dc, accent, X(s.InSec), true, active);
            if (s.OutSec >= ViewStart && s.OutSec <= ViewStart + View) DrawMarker(dc, accent, X(s.OutSec), false, active);
        }

        // позиция
        if (Position >= ViewStart && Position <= ViewStart + View)
        {
            double px = X(Position);
            dc.DrawLine(new Pen(playhead, 1.5), new Point(px, RulerH), new Point(px, LaneTop + 2 * (LaneH + 3)));
            var geo = new StreamGeometry();
            using (var g = geo.Open()) { g.BeginFigure(new Point(px - 6, RulerH - 2), true, true); g.LineTo(new Point(px + 6, RulerH - 2), true, false); g.LineTo(new Point(px, RulerH + 5), true, false); }
            dc.DrawGeometry(playhead, null, geo);
        }

        if (IsZoomed)
        {
            // полоска обзора: где мы в ролике
            double y = h - 4;
            dc.DrawRectangle(Ui.Solid(trackColor), null, new Rect(Pad, y, W, 3));
            dc.DrawRectangle(Ui.Solid(accentColor, 0.8), null, new Rect(Pad + ViewStart / Duration * W, y, Math.Max(4, View / Duration * W), 3));
        }
    }

    private static double NiceStep(double raw)
    {
        double[] steps = { 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 };
        foreach (var s in steps) if (s >= raw) return s;
        return 600;
    }

    private static void DrawMarker(DrawingContext dc, Brush brush, double x, bool isIn, bool active)
    {
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            if (isIn) { g.BeginFigure(new Point(x, TrackTop), true, true); g.LineTo(new Point(x + 8, TrackTop), true, false); g.LineTo(new Point(x, TrackTop + 9), true, false); }
            else { g.BeginFigure(new Point(x, TrackTop), true, true); g.LineTo(new Point(x - 8, TrackTop), true, false); g.LineTo(new Point(x, TrackTop + 9), true, false); }
        }
        dc.DrawGeometry(brush, null, geo);
        dc.DrawLine(new Pen(brush, active ? 2.5 : 1.5), new Point(x, TrackTop), new Point(x, TrackTop + TrackH));
    }

    // ------------------------------------------------------------ мышь

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (Duration <= 0) return;
        var p = e.GetPosition(this);
        _dragSegment = null; _dragMarker = null;
        if (p.Y >= LaneTop - 3)
        {
            // границы интервалов ретуши/запикивания
            double best = 7;
            foreach (var m in Markers)
            {
                double y = LaneTop + m.Lane * (LaneH + 3);
                if (p.Y < y - 3 || p.Y > y + LaneH + 3) continue;
                double df = Math.Abs(p.X - X(m.From)), dt = Math.Abs(p.X - X(m.To));
                if (df < best) { best = df; _dragMarker = m; _dragIsIn = true; }
                if (dt < best) { best = dt; _dragMarker = m; _dragIsIn = false; }
            }
            if (_dragMarker is null)
            {
                var hit = Markers.LastOrDefault(m => p.Y >= LaneTop + m.Lane * (LaneH + 3) - 3 && p.Y <= LaneTop + m.Lane * (LaneH + 3) + LaneH + 3 && p.X >= X(m.From) && p.X <= X(m.To));
                if (hit is not null) { SelectedMarkerTag = hit.Tag; MarkerSelected?.Invoke(hit); InvalidateVisual(); e.Handled = true; return; }
            }
            else
            {
                _drag = Drag.MarkerEdge; SelectedMarkerTag = _dragMarker.Tag; MarkerSelected?.Invoke(_dragMarker);
                CaptureMouse(); e.Handled = true; return;
            }
        }
        if (p.Y >= TrackTop - 6 && p.Y < TrackTop + TrackH + 6)
        {
            double best = 8;
            foreach (var s in Segments)
            {
                double di = Math.Abs(p.X - X(s.InSec)), dout = Math.Abs(p.X - X(s.OutSec));
                if (di < best) { best = di; _dragSegment = s; _dragIsIn = true; }
                if (dout < best) { best = dout; _dragSegment = s; _dragIsIn = false; }
            }
        }
        if (_dragSegment is not null)
        {
            _drag = Drag.SegmentEdge;
            if (!ReferenceEquals(ActiveSegment, _dragSegment)) { ActiveSegment = _dragSegment; ActiveSegmentChanged?.Invoke(_dragSegment); }
        }
        else
        {
            _drag = Drag.Seek;
            double t = T(p.X);
            var hit = Segments.FirstOrDefault(s => s.Contains(t));
            if (hit is not null && !ReferenceEquals(hit, ActiveSegment)) { ActiveSegment = hit; ActiveSegmentChanged?.Invoke(hit); }
            SeekRequested?.Invoke(t);
        }
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_drag == Drag.None || !IsMouseCaptured) return;
        var p = e.GetPosition(this);
        if (_drag == Drag.Pan) { ViewStart = Math.Clamp(_panStartView - (p.X - _panStartX) / W * View, 0, Math.Max(0, Duration - View)); InvalidateVisual(); return; }
        double t = Snap(T(p.X));
        switch (_drag)
        {
            case Drag.Seek: SeekRequested?.Invoke(t); break;
            case Drag.SegmentEdge when _dragSegment is not null:
                if (_dragIsIn) SegmentEdgeChanged?.Invoke(_dragSegment, true, Math.Min(t, _dragSegment.OutSec - 0.05));
                else SegmentEdgeChanged?.Invoke(_dragSegment, false, Math.Max(t, _dragSegment.InSec + 0.05));
                break;
            case Drag.MarkerEdge when _dragMarker is not null:
                t = T(p.X); // интервалы ретуши/звука не прилипают к ключевым кадрам
                if (_dragIsIn) MarkerEdgeChanged?.Invoke(_dragMarker, true, Math.Min(t, _dragMarker.To - 0.05));
                else MarkerEdgeChanged?.Invoke(_dragMarker, false, Math.Max(t, _dragMarker.From + 0.05));
                break;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        bool wasEdit = _drag is Drag.SegmentEdge or Drag.MarkerEdge;
        _drag = Drag.None; _dragSegment = null; _dragMarker = null;
        ReleaseMouseCapture();
        if (wasEdit) DragCompleted?.Invoke();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton == MouseButton.Middle && IsZoomed && _drag == Drag.None)
        {
            _drag = Drag.Pan; _panStartX = e.GetPosition(this).X; _panStartView = ViewStart; CaptureMouse(); e.Handled = true;
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton == MouseButton.Middle && _drag == Drag.Pan) { _drag = Drag.None; ReleaseMouseCapture(); }
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        if (Duration <= 0) return;
        ContextRequested?.Invoke(T(e.GetPosition(this).X));
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (Duration <= 0) return;
        var p = e.GetPosition(this);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) Zoom(e.Delta > 0 ? 1.25 : 0.8, T(p.X));
        else Pan(-Math.Sign(e.Delta) * View * 0.1);
        e.Handled = true;
    }

    // ------------------------------------------------------------ ключевые кадры

    public double NearestKeyframe(double t)
    {
        if (Keyframes.Count == 0) return t;
        double best = t, dist = double.MaxValue;
        foreach (var k in Keyframes) { var d = Math.Abs(k - t); if (d < dist) { dist = d; best = k; } }
        return best;
    }

    public double? PrevKeyframe(double t) { double v = double.NaN; foreach (var k in Keyframes) if (k < t - 0.001) v = k; return double.IsNaN(v) ? null : v; }
    public double? NextKeyframe(double t) { foreach (var k in Keyframes) if (k > t + 0.001) return k; return null; }

    public double Snap(double t) => SnapToKeyframes ? NearestKeyframe(t) : t;
}
