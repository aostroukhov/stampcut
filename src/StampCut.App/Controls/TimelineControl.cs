using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using StampCut.Core;

namespace StampCut.App.Controls;

public sealed record TimelineMarker(double From, double To, Brush Brush, int Lane);

/// <summary>Лента времени: позиция, точки входа/выхода, интервалы ретуши и запикивания, ключевые кадры.</summary>
public sealed class TimelineControl : FrameworkElement
{
    public double Duration { get; set; }
    public double Position { get; set; }
    public double InPoint { get; set; }
    public double OutPoint { get; set; }
    public List<TimelineMarker> Markers { get; } = new();
    public IReadOnlyList<double> Keyframes { get; set; } = Array.Empty<double>();
    public bool SnapToKeyframes { get; set; }

    public event Action<double>? SeekRequested;
    public event Action<double>? InPointChanged;
    public event Action<double>? OutPointChanged;
    /// <summary>Перетаскивание маркера входа/выхода закончено.</summary>
    public event Action? DragCompleted;

    private enum Drag { None, Seek, In, Out }
    private Drag _drag;
    private const double Pad = 10;

    private static readonly Brush Track = new SolidColorBrush(Color.FromRgb(0xE1, 0xE4, 0xE8));
    private static readonly Brush Selected = new SolidColorBrush(Color.FromRgb(0xBF, 0xD4, 0xF7));
    private static readonly Brush Playhead = new SolidColorBrush(Color.FromRgb(0xD9, 0x3A, 0x3A));
    private static readonly Brush Marker = new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xED));
    private static readonly Brush KeyframeBrush = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0));

    public TimelineControl()
    {
        Height = 64;
        Focusable = false;
        Cursor = Cursors.Hand;
        foreach (var b in new[] { Track, Selected, Playhead, Marker, KeyframeBrush }) b.Freeze();
    }

    private double W => Math.Max(1, ActualWidth - 2 * Pad);
    private double X(double t) => Pad + (Duration > 0 ? t / Duration : 0) * W;
    private double T(double x) => Duration > 0 ? Math.Clamp((x - Pad) / W, 0, 1) * Duration : 0;

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, h));
        double trackTop = 14, trackH = 18;
        dc.DrawRoundedRectangle(Track, null, new Rect(Pad, trackTop, W, trackH), 3, 3);
        if (Duration <= 0) return;

        dc.DrawRectangle(Selected, null, new Rect(X(InPoint), trackTop, Math.Max(0, X(OutPoint) - X(InPoint)), trackH));

        if (SnapToKeyframes)
            foreach (var k in Keyframes)
                dc.DrawLine(new Pen(KeyframeBrush, 1), new Point(X(k), trackTop), new Point(X(k), trackTop + trackH));

        foreach (var m in Markers)
        {
            double y = trackTop + trackH + 3 + m.Lane * 6;
            dc.DrawRectangle(m.Brush, null, new Rect(X(m.From), y, Math.Max(2, X(m.To) - X(m.From)), 4));
        }

        // маркеры входа/выхода
        DrawMarker(dc, X(InPoint), trackTop, trackH, true);
        DrawMarker(dc, X(OutPoint), trackTop, trackH, false);

        // позиция
        double px = X(Position);
        dc.DrawLine(new Pen(Playhead, 2), new Point(px, 4), new Point(px, trackTop + trackH + 4));
        var geo = new StreamGeometry();
        using (var g = geo.Open()) { g.BeginFigure(new Point(px - 6, 0), true, true); g.LineTo(new Point(px + 6, 0), true, false); g.LineTo(new Point(px, 7), true, false); }
        dc.DrawGeometry(Playhead, null, geo);

        // подписи времени
        var tf = new Typeface("Segoe UI");
        DrawText(dc, TimeFormat.Format(0, false), Pad, h - 14, tf, HorizontalAlignment.Left);
        DrawText(dc, TimeFormat.Format(Duration, false), Pad + W, h - 14, tf, HorizontalAlignment.Right);
    }

    private static void DrawMarker(DrawingContext dc, double x, double top, double h, bool isIn)
    {
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            if (isIn) { g.BeginFigure(new Point(x, top), true, true); g.LineTo(new Point(x + 7, top), true, false); g.LineTo(new Point(x, top + 8), true, false); }
            else { g.BeginFigure(new Point(x, top), true, true); g.LineTo(new Point(x - 7, top), true, false); g.LineTo(new Point(x, top + 8), true, false); }
        }
        dc.DrawGeometry(Marker, null, geo);
        dc.DrawLine(new Pen(Marker, 2), new Point(x, top), new Point(x, top + h));
    }

    private static void DrawText(DrawingContext dc, string text, double x, double y, Typeface tf, HorizontalAlignment align)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tf, 10, Brushes.Gray, 1.0);
        dc.DrawText(ft, new Point(align == HorizontalAlignment.Right ? x - ft.Width : x, y));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (Duration <= 0) return;
        var p = e.GetPosition(this);
        double xi = X(InPoint), xo = X(OutPoint);
        if (Math.Abs(p.X - xi) <= 7 && p.Y < 36) _drag = Drag.In;
        else if (Math.Abs(p.X - xo) <= 7 && p.Y < 36) _drag = Drag.Out;
        else { _drag = Drag.Seek; SeekRequested?.Invoke(T(p.X)); }
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_drag == Drag.None || !IsMouseCaptured) return;
        double t = Snap(T(e.GetPosition(this).X));
        switch (_drag)
        {
            case Drag.Seek: SeekRequested?.Invoke(t); break;
            case Drag.In: InPointChanged?.Invoke(Math.Min(t, OutPoint - 0.05)); break;
            case Drag.Out: OutPointChanged?.Invoke(Math.Max(t, InPoint + 0.05)); break;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        bool wasMarker = _drag is Drag.In or Drag.Out;
        _drag = Drag.None;
        ReleaseMouseCapture();
        if (wasMarker) DragCompleted?.Invoke();
    }

    public double Snap(double t)
    {
        if (!SnapToKeyframes || Keyframes.Count == 0) return t;
        double best = t, dist = double.MaxValue;
        foreach (var k in Keyframes) { var d = Math.Abs(k - t); if (d < dist) { dist = d; best = k; } }
        return best;
    }
}
