using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using StampCut.Core;
using StampCut.Core.Models;

namespace StampCut.App.Controls;

public sealed record TimelineMarker(double From, double To, Brush Brush, int Lane);

/// <summary>Лента времени: позиция, фрагменты (вход/выход каждого), интервалы ретуши и запикивания, ключевые кадры.</summary>
public sealed class TimelineControl : FrameworkElement
{
    public double Duration { get; set; }
    public double Position { get; set; }
    public List<Segment> Segments { get; set; } = new();
    public Segment? ActiveSegment { get; set; }
    public List<TimelineMarker> Markers { get; } = new();
    public IReadOnlyList<double> Keyframes { get; set; } = Array.Empty<double>();
    public bool SnapToKeyframes { get; set; }

    public event Action<double>? SeekRequested;
    /// <summary>Изменена граница фрагмента: (фрагмент, это вход?, новое значение).</summary>
    public event Action<Segment, bool, double>? SegmentEdgeChanged;
    public event Action<Segment?>? ActiveSegmentChanged;
    public event Action? DragCompleted;
    /// <summary>Правая кнопка: время под курсором.</summary>
    public event Action<double>? ContextRequested;
    public bool ShowKeyframes { get; set; } = true;

    private enum Drag { None, Seek, Edge }
    private Drag _drag;
    private Segment? _dragSegment;
    private bool _dragIsIn;
    private const double Pad = 10;
    private const double TrackTop = 14, TrackH = 20;

    public TimelineControl()
    {
        Height = 66;
        Focusable = false;
        Cursor = Cursors.Hand;
    }

    private double W => Math.Max(1, ActualWidth - 2 * Pad);
    private double X(double t) => Pad + (Duration > 0 ? t / Duration : 0) * W;
    private double T(double x) => Duration > 0 ? Math.Clamp((x - Pad) / W, 0, 1) * Duration : 0;

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double h = ActualHeight;
        var track = Ui.ThemeBrush("ControlStrongFillColorDisabledBrush", new SolidColorBrush(Color.FromRgb(0xE1, 0xE4, 0xE8)));
        var accent = Ui.ThemeBrush("AccentFillColorDefaultBrush", Ui.Accent);
        var textBrush = Ui.ThemeBrush("TextFillColorSecondaryBrush", Ui.Muted);
        var playhead = Ui.Danger;

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, h));
        dc.DrawRoundedRectangle(track, null, new Rect(Pad, TrackTop, W, TrackH), 4, 4);
        if (Duration <= 0) return;

        foreach (var s in Segments)
        {
            bool active = ReferenceEquals(s, ActiveSegment);
            var fill = accent.Clone(); fill.Opacity = active ? 0.55 : 0.28; fill.Freeze();
            dc.DrawRectangle(fill, null, new Rect(X(s.InSec), TrackTop, Math.Max(1, X(s.OutSec) - X(s.InSec)), TrackH));
        }

        if (ShowKeyframes || SnapToKeyframes)
        {
            var kf = new Pen(new SolidColorBrush(Color.FromArgb(90, 128, 128, 128)), 1);
            foreach (var k in Keyframes) dc.DrawLine(kf, new Point(X(k), TrackTop), new Point(X(k), TrackTop + TrackH));
        }

        foreach (var m in Markers)
        {
            double y = TrackTop + TrackH + 3 + m.Lane * 6;
            dc.DrawRectangle(m.Brush, null, new Rect(X(m.From), y, Math.Max(2, X(m.To) - X(m.From)), 4));
        }

        foreach (var s in Segments)
        {
            bool active = ReferenceEquals(s, ActiveSegment);
            DrawMarker(dc, accent, X(s.InSec), true, active);
            DrawMarker(dc, accent, X(s.OutSec), false, active);
        }

        double px = X(Position);
        dc.DrawLine(new Pen(playhead, 2), new Point(px, 4), new Point(px, TrackTop + TrackH + 4));
        var geo = new StreamGeometry();
        using (var g = geo.Open()) { g.BeginFigure(new Point(px - 6, 0), true, true); g.LineTo(new Point(px + 6, 0), true, false); g.LineTo(new Point(px, 7), true, false); }
        dc.DrawGeometry(playhead, null, geo);

        var tf = new Typeface("Segoe UI");
        DrawText(dc, textBrush, TimeFormat.Format(0, false), Pad, h - 15, tf, HorizontalAlignment.Left);
        DrawText(dc, textBrush, TimeFormat.Format(Duration, false), Pad + W, h - 15, tf, HorizontalAlignment.Right);
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

    private static void DrawText(DrawingContext dc, Brush brush, string text, double x, double y, Typeface tf, HorizontalAlignment align)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tf, 10, brush, 1.0);
        dc.DrawText(ft, new Point(align == HorizontalAlignment.Right ? x - ft.Width : x, y));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (Duration <= 0) return;
        var p = e.GetPosition(this);
        _dragSegment = null;
        if (p.Y < TrackTop + TrackH + 6)
        {
            // ближайший маркер любого фрагмента
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
            _drag = Drag.Edge;
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
        double t = Snap(T(e.GetPosition(this).X));
        if (_drag == Drag.Seek) { SeekRequested?.Invoke(t); return; }
        if (_dragSegment is null) return;
        if (_dragIsIn) SegmentEdgeChanged?.Invoke(_dragSegment, true, Math.Min(t, _dragSegment.OutSec - 0.05));
        else SegmentEdgeChanged?.Invoke(_dragSegment, false, Math.Max(t, _dragSegment.InSec + 0.05));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        bool wasEdge = _drag == Drag.Edge;
        _drag = Drag.None;
        _dragSegment = null;
        ReleaseMouseCapture();
        if (wasEdge) DragCompleted?.Invoke();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        if (Duration <= 0) return;
        ContextRequested?.Invoke(T(e.GetPosition(this).X));
        e.Handled = true;
    }

    /// <summary>Ближайший ключевой кадр к t (или t, если ключевых кадров нет).</summary>
    public double NearestKeyframe(double t)
    {
        if (Keyframes.Count == 0) return t;
        double best = t, dist = double.MaxValue;
        foreach (var k in Keyframes) { var d = Math.Abs(k - t); if (d < dist) { dist = d; best = k; } }
        return best;
    }

    public double? PrevKeyframe(double t) => Keyframes.Count == 0 ? null : Keyframes.LastOrDefault(k => k < t - 0.001, double.NaN) is var v && !double.IsNaN(v) ? v : null;
    public double? NextKeyframe(double t) => Keyframes.Count == 0 ? null : Keyframes.FirstOrDefault(k => k > t + 0.001, double.NaN) is var v && !double.IsNaN(v) ? v : null;

    public double Snap(double t)
    {
        if (!SnapToKeyframes || Keyframes.Count == 0) return t;
        double best = t, dist = double.MaxValue;
        foreach (var k in Keyframes) { var d = Math.Abs(k - t); if (d < dist) { dist = d; best = k; } }
        return best;
    }
}
