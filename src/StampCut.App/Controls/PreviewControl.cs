using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using StampCut.Core.Geometry;
using StampCut.Core.Models;

namespace StampCut.App.Controls;

public enum EditMode { View, Crop, Redact }

public sealed class OverlayItem
{
    public required OverlaySettings Settings { get; init; }
    public BitmapSource? Image { get; set; }
    public int PngWidth { get; set; }
    public int PngHeight { get; set; }
    public string? ResolvedPath { get; set; }
}

/// <summary>
/// Область просмотра: видео (MediaElement), постер, и поверх — слой с оверлеями, рамкой кропа и областями ретуши.
/// Все координаты модели — в пикселях исходника (с учётом поворота) или долях кадра; слой пересчитывает их в экранные.
/// </summary>
public sealed class PreviewControl : Grid
{
    public MediaElement Player { get; } = new()
    {
        LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, ScrubbingEnabled = true, Stretch = Stretch.Uniform,
    };
    private readonly Image _poster = new() { Stretch = Stretch.Uniform, IsHitTestVisible = false };
    private readonly Canvas _layer = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, ClipToBounds = false };
    private readonly TextBlock _message = new()
    {
        Foreground = Brushes.White, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        Background = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), Padding = new Thickness(14, 8, 14, 8), Visibility = Visibility.Collapsed, TextAlignment = TextAlignment.Center,
    };

    // --- модель
    public int VideoWidth { get; private set; }
    public int VideoHeight { get; private set; }
    public Clip? CurrentClip { get; set; }
    public List<OverlayItem> Overlays { get; } = new();
    public OverlaySettings? SelectedOverlay { get; set; }
    public RedactRegion? SelectedRedaction { get; set; }
    public EditMode Mode { get; private set; } = EditMode.View;
    /// <summary>Размер выходного кадра и способ вписывания текущего ролика в него (для WYSIWYG оверлея).</summary>
    public int OutputWidth { get; set; }
    public int OutputHeight { get; set; }
    public FitMode Fit { get; set; } = FitMode.Keep;
    /// <summary>Фиксированное соотношение сторон кропа (w/h) или null — свободно.</summary>
    public double? CropAspect { get; set; }
    /// <summary>Позиция в секундах исходника (для интервалов ретуши).</summary>
    public double CurrentTimeSec { get; set; }
    /// <summary>Позиция на выходной ленте (для интервалов показа оверлея).</summary>
    public double OutputTimeSec { get; set; }

    public event Action<OverlaySettings>? OverlayChanged;
    public event Action<OverlaySettings>? OverlaySelected;
    public event Action? CropChanged;
    public event Action<RedactRegion>? RedactionChanged;
    public event Action<RedactRegion?>? RedactionSelected;

    private double _scale = 1; // экранных px на 1 px исходника
    private readonly List<Action> _layouters = new();

    public PreviewControl()
    {
        Background = Ui.PreviewBg;
        ClipToBounds = true;
        Children.Add(Player);
        Children.Add(_poster);
        Children.Add(_layer);
        Children.Add(_message);
        SizeChanged += (_, _) => Layout();
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource == this || e.OriginalSource == Player || e.OriginalSource == _poster)
            {
                if (Mode == EditMode.Redact) { SelectedRedaction = null; RedactionSelected?.Invoke(null); Refresh(); }
            }
        };
    }

    public void SetVideoSize(int w, int h)
    {
        VideoWidth = Math.Max(1, w); VideoHeight = Math.Max(1, h);
        Layout();
    }

    public void SetMode(EditMode mode)
    {
        Mode = mode;
        Refresh();
    }

    public void ShowPoster(BitmapSource? image)
    {
        _poster.Source = image;
        _poster.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
    }

    public void HidePoster() => _poster.Visibility = Visibility.Collapsed;

    public void ShowMessage(string? text)
    {
        _message.Text = text ?? "";
        _message.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    // ------------------------------------------------------------ геометрия

    private Rect VideoRectOnScreen()
    {
        double cw = ActualWidth, ch = ActualHeight;
        if (cw <= 0 || ch <= 0 || VideoWidth <= 0) return Rect.Empty;
        _scale = Math.Min(cw / VideoWidth, ch / VideoHeight);
        double vw = VideoWidth * _scale, vh = VideoHeight * _scale;
        return new Rect((cw - vw) / 2, (ch - vh) / 2, vw, vh);
    }

    /// <summary>Прямоугольник кропа в пикселях исходника.</summary>
    private Rect CropRectPx()
    {
        if (CurrentClip?.Crop is { IsFull: false } c)
        {
            var (x, y, w, h) = c.ToPixels(VideoWidth, VideoHeight);
            return new Rect(x, y, w, h);
        }
        return new Rect(0, 0, VideoWidth, VideoHeight);
    }

    /// <summary>Выходной кадр в пикселях исходника: куда «ляжет» оверлей с учётом кропа и вписывания.</summary>
    public Rect OutputFramePx()
    {
        var crop = CropRectPx();
        if (OutputWidth <= 0 || OutputHeight <= 0) return crop;
        double k = Fit == FitMode.Crop
            ? Math.Max(OutputWidth / crop.Width, OutputHeight / crop.Height)
            : Math.Min(OutputWidth / crop.Width, OutputHeight / crop.Height);
        if (Fit == FitMode.Keep && Math.Abs(OutputWidth / crop.Width - OutputHeight / crop.Height) < 0.01) return crop;
        double fw = OutputWidth / k, fh = OutputHeight / k;
        return new Rect(crop.X + (crop.Width - fw) / 2, crop.Y + (crop.Height - fh) / 2, fw, fh);
    }

    private Rect ToScreen(Rect px) => new(px.X * _scale, px.Y * _scale, px.Width * _scale, px.Height * _scale);

    // ------------------------------------------------------------ построение слоя

    public void Refresh()
    {
        _layer.Children.Clear();
        _layouters.Clear();
        if (VideoWidth <= 0 || CurrentClip is null) { Layout(); return; }

        BuildMask();
        BuildRedactions();
        BuildOverlays();
        if (Mode == EditMode.Crop) BuildCropEditor();
        Layout();
    }

    public void Layout()
    {
        var vr = VideoRectOnScreen();
        if (vr.IsEmpty) return;
        _layer.Width = vr.Width; _layer.Height = vr.Height;
        _layer.Margin = new Thickness(vr.X, vr.Y, 0, 0);
        foreach (var l in _layouters) l();
    }

    private void BuildMask()
    {
        var dim = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)); dim.Freeze();
        var rects = new Rectangle[4];
        for (int i = 0; i < 4; i++) { rects[i] = new Rectangle { Fill = dim, IsHitTestVisible = false }; _layer.Children.Add(rects[i]); }
        var frame = new Rectangle { Stroke = Brushes.White, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 }, IsHitTestVisible = false, Opacity = 0.8 };
        _layer.Children.Add(frame);
        _layouters.Add(() =>
        {
            var area = ToScreen(Mode == EditMode.Crop ? CropRectPx() : OutputFramePx());
            double W = _layer.Width, H = _layer.Height;
            // четыре прямоугольника вокруг области
            Place(rects[0], 0, 0, W, Math.Max(0, area.Y));
            Place(rects[1], 0, area.Bottom, W, Math.Max(0, H - area.Bottom));
            Place(rects[2], 0, area.Y, Math.Max(0, area.X), area.Height);
            Place(rects[3], area.Right, area.Y, Math.Max(0, W - area.Right), area.Height);
            Place(frame, area.X, area.Y, area.Width, area.Height);
            frame.Visibility = Math.Abs(area.Width - W) < 1 && Math.Abs(area.Height - H) < 1 && Mode != EditMode.Crop ? Visibility.Collapsed : Visibility.Visible;
        });
    }

    private void BuildOverlays()
    {
        foreach (var item in Overlays)
        {
            var s = item.Settings;
            var img = new Image { Source = item.Image, Stretch = Stretch.Fill, Opacity = s.Opacity, IsHitTestVisible = false };
            var border = new Rectangle { Stroke = Ui.Accent, StrokeThickness = 1.5, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            var move = Ui.TransparentThumb();
            var resize = Ui.Handle(10, Cursors.SizeNWSE, Ui.Accent);
            move.ToolTip = s.Name.Length > 0 ? s.Name : "Оверлей";
            _layer.Children.Add(img); _layer.Children.Add(border); _layer.Children.Add(move); _layer.Children.Add(resize);
            bool visibleNow = true;

            Rect RectOut() => RectToRect(OverlayGeometry.Compute(OutputWidth, OutputHeight, s, item.PngWidth, item.PngHeight));
            void LayoutItem()
            {
                var frame = OutputFramePx();
                double k = frame.Width / Math.Max(1, OutputWidth); // px исходника на 1 px выхода
                var r = RectOut();
                var px = new Rect(frame.X + r.X * k, frame.Y + r.Y * k, r.Width * k, r.Height * k);
                var sc = ToScreen(px);
                visibleNow = s.FromSec is null && s.ToSec is null || (OutputTimeSec >= (s.FromSec ?? 0) && OutputTimeSec <= (s.ToSec ?? double.MaxValue));
                img.Opacity = s.Opacity * (visibleNow ? 1 : 0.25);
                Place(img, sc.X, sc.Y, sc.Width, sc.Height);
                Place(move, sc.X, sc.Y, sc.Width, sc.Height);
                bool selected = ReferenceEquals(SelectedOverlay, s) && Mode == EditMode.View;
                border.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
                resize.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
                Place(border, sc.X, sc.Y, sc.Width, sc.Height);
                Canvas.SetLeft(resize, sc.Right - 5); Canvas.SetTop(resize, sc.Bottom - 5);
                move.IsHitTestVisible = Mode == EditMode.View;
            }
            _layouters.Add(LayoutItem);

            move.DragStarted += (_, _) => { SelectedOverlay = s; OverlaySelected?.Invoke(s); Layout(); };
            move.DragDelta += (_, e) =>
            {
                var frame = OutputFramePx();
                double k = frame.Width / Math.Max(1, OutputWidth);
                var r = RectOut();
                double dx = e.HorizontalChange / _scale / k, dy = e.VerticalChange / _scale / k;
                OverlayGeometry.ApplyPosition(OutputWidth, OutputHeight, s, item.PngWidth, item.PngHeight, r.X + dx, r.Y + dy, r.Width);
                OverlayChanged?.Invoke(s); Layout();
            };
            move.DragCompleted += (_, _) =>
            {
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
                var r = RectOut();
                var nearest = OverlayGeometry.NearestAnchor(OutputWidth, OutputHeight, r.X + r.Width / 2, r.Y + r.Height / 2);
                if (nearest != s.Anchor)
                {
                    s.Anchor = nearest;
                    OverlayGeometry.ApplyPosition(OutputWidth, OutputHeight, s, item.PngWidth, item.PngHeight, r.X, r.Y, r.Width);
                    OverlayChanged?.Invoke(s); Layout();
                }
            };
            resize.DragDelta += (_, e) =>
            {
                var frame = OutputFramePx();
                double k = frame.Width / Math.Max(1, OutputWidth);
                var r = RectOut();
                double w = Math.Max(8, r.Width + e.HorizontalChange / _scale / k);
                OverlayGeometry.ApplyPosition(OutputWidth, OutputHeight, s, item.PngWidth, item.PngHeight, r.X, r.Y, w);
                OverlayChanged?.Invoke(s); Layout();
            };
        }
    }

    private void BuildRedactions()
    {
        if (CurrentClip is null) return;
        foreach (var rd in CurrentClip.Redactions)
        {
            var region = rd;
            var fill = new Rectangle
            {
                Fill = new VisualBrush(Player) { ViewboxUnits = BrushMappingMode.RelativeToBoundingBox, Stretch = Stretch.Fill },
                Effect = new BlurEffect { Radius = 6 + region.Strength * 3, KernelType = KernelType.Box },
                IsHitTestVisible = false,
            };
            var border = new Rectangle { Stroke = region.Mode == RedactMode.Pixelate ? Brushes.Orange : Brushes.Yellow, StrokeThickness = 1.5, IsHitTestVisible = false, StrokeDashArray = new DoubleCollection { 3, 2 } };
            var label = new TextBlock { Text = region.Mode == RedactMode.Pixelate ? "мозаика" : "размытие", Foreground = Brushes.White, FontSize = 10, Background = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), Padding = new Thickness(3, 0, 3, 0), IsHitTestVisible = false };
            var move = Ui.TransparentThumb();
            var resize = Ui.Handle(10, Cursors.SizeNWSE, Brushes.Yellow);
            _layer.Children.Add(fill); _layer.Children.Add(border); _layer.Children.Add(label); _layer.Children.Add(move); _layer.Children.Add(resize);

            Rect Px() => new(region.X * VideoWidth, region.Y * VideoHeight, region.W * VideoWidth, region.H * VideoHeight);
            void LayoutItem()
            {
                var sc = ToScreen(Px());
                bool active = region.FromSec is null && region.ToSec is null || (CurrentTimeSec >= (region.FromSec ?? 0) && CurrentTimeSec <= (region.ToSec ?? double.MaxValue));
                ((VisualBrush)fill.Fill).Viewbox = new Rect(region.X, region.Y, region.W, region.H);
                fill.Opacity = active ? 1 : 0.3;
                Place(fill, sc.X, sc.Y, sc.Width, sc.Height);
                Place(border, sc.X, sc.Y, sc.Width, sc.Height);
                Place(move, sc.X, sc.Y, sc.Width, sc.Height);
                Canvas.SetLeft(label, sc.X + 2); Canvas.SetTop(label, sc.Y + 2);
                bool editing = Mode == EditMode.Redact;
                bool selected = editing && ReferenceEquals(SelectedRedaction, region);
                border.StrokeThickness = selected ? 2.5 : 1.5;
                border.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
                label.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
                resize.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
                move.IsHitTestVisible = editing;
                Canvas.SetLeft(resize, sc.Right - 5); Canvas.SetTop(resize, sc.Bottom - 5);
            }
            _layouters.Add(LayoutItem);

            move.DragStarted += (_, _) => { SelectedRedaction = region; RedactionSelected?.Invoke(region); Layout(); };
            move.DragDelta += (_, e) =>
            {
                region.X = Math.Clamp(region.X + e.HorizontalChange / _scale / VideoWidth, 0, 1 - region.W);
                region.Y = Math.Clamp(region.Y + e.VerticalChange / _scale / VideoHeight, 0, 1 - region.H);
                RedactionChanged?.Invoke(region); Layout();
            };
            resize.DragDelta += (_, e) =>
            {
                region.W = Math.Clamp(region.W + e.HorizontalChange / _scale / VideoWidth, 8.0 / VideoWidth, 1 - region.X);
                region.H = Math.Clamp(region.H + e.VerticalChange / _scale / VideoHeight, 8.0 / VideoHeight, 1 - region.Y);
                RedactionChanged?.Invoke(region); Layout();
            };
        }
    }

    private void BuildCropEditor()
    {
        if (CurrentClip is null) return;
        CurrentClip.Crop ??= new CropRect();
        var crop = CurrentClip.Crop;
        var border = new Rectangle { Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false };
        var move = Ui.TransparentThumb();
        var tl = Ui.Handle(12, Cursors.SizeNWSE); var tr = Ui.Handle(12, Cursors.SizeNESW);
        var bl = Ui.Handle(12, Cursors.SizeNESW); var br = Ui.Handle(12, Cursors.SizeNWSE);
        foreach (var el in new UIElement[] { move, border, tl, tr, bl, br }) _layer.Children.Add(el);

        Rect Px() => new(crop.X * VideoWidth, crop.Y * VideoHeight, crop.W * VideoWidth, crop.H * VideoHeight);
        void Set(Rect px)
        {
            px.Width = Math.Clamp(px.Width, 32, VideoWidth); px.Height = Math.Clamp(px.Height, 32, VideoHeight);
            px.X = Math.Clamp(px.X, 0, VideoWidth - px.Width); px.Y = Math.Clamp(px.Y, 0, VideoHeight - px.Height);
            crop.X = px.X / VideoWidth; crop.Y = px.Y / VideoHeight; crop.W = px.Width / VideoWidth; crop.H = px.Height / VideoHeight;
            CropChanged?.Invoke(); Layout();
        }
        void LayoutItem()
        {
            var sc = ToScreen(Px());
            Place(border, sc.X, sc.Y, sc.Width, sc.Height);
            Place(move, sc.X, sc.Y, sc.Width, sc.Height);
            Canvas.SetLeft(tl, sc.X - 6); Canvas.SetTop(tl, sc.Y - 6);
            Canvas.SetLeft(tr, sc.Right - 6); Canvas.SetTop(tr, sc.Y - 6);
            Canvas.SetLeft(bl, sc.X - 6); Canvas.SetTop(bl, sc.Bottom - 6);
            Canvas.SetLeft(br, sc.Right - 6); Canvas.SetTop(br, sc.Bottom - 6);
        }
        _layouters.Add(LayoutItem);

        move.DragDelta += (_, e) => { var r = Px(); r.X += e.HorizontalChange / _scale; r.Y += e.VerticalChange / _scale; Set(r); };
        void Corner(Thumb t, bool left, bool top)
        {
            t.DragDelta += (_, e) =>
            {
                var r = Px();
                double dx = e.HorizontalChange / _scale, dy = e.VerticalChange / _scale;
                double right = r.Right, bottom = r.Bottom;
                double nx = left ? r.X + dx : r.X, ny = top ? r.Y + dy : r.Y;
                double nw = left ? right - nx : r.Width + dx, nh = top ? bottom - ny : r.Height + dy;
                if (CropAspect is { } a && a > 0)
                {
                    // ведущая — ширина; высота по пропорции
                    nw = Math.Max(32, nw); nh = nw / a;
                    if (top) ny = bottom - nh;
                }
                nw = Math.Max(32, nw); nh = Math.Max(32, nh);
                if (left) nx = right - nw;
                if (top) ny = bottom - nh;
                Set(new Rect(nx, ny, nw, nh));
            };
        }
        Corner(tl, true, true); Corner(tr, false, true); Corner(bl, true, false); Corner(br, false, false);
    }

    private static Rect RectToRect(PixelRect r) => new(r.X, r.Y, r.W, r.H);

    private static void Place(FrameworkElement el, double x, double y, double w, double h)
    {
        Canvas.SetLeft(el, x); Canvas.SetTop(el, y);
        el.Width = Math.Max(0, w); el.Height = Math.Max(0, h);
    }
}
