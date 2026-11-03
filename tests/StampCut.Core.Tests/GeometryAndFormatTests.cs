using StampCut.Core;
using StampCut.Core.Geometry;
using StampCut.Core.Models;
using Xunit;

namespace StampCut.Core.Tests;

public class GeometryAndFormatTests
{
    [Theory]
    [InlineData(Anchor.TopLeft, 58, 32)]
    [InlineData(Anchor.TopRight, 1382, 32)]
    [InlineData(Anchor.BottomLeft, 58, 856)]
    [InlineData(Anchor.BottomRight, 1382, 856)]
    [InlineData(Anchor.Center, 720, 444)]
    [InlineData(Anchor.TopCenter, 720, 32)]
    public void Compute_PlacesByAnchor(Anchor anchor, int x, int y)
    {
        var o = new OverlaySettings { Anchor = anchor, OffsetX = 3, OffsetY = 3, Width = 25 };
        var r = OverlayGeometry.Compute(1920, 1080, o, 1000, 400);
        Assert.Equal((x, y, 480, 192), (r.X, r.Y, r.W, r.H));
    }

    [Fact]
    public void ApplyPosition_RoundTrips()
    {
        var o = new OverlaySettings { Anchor = Anchor.BottomRight, OffsetX = 3, OffsetY = 3, Width = 25 };
        OverlayGeometry.ApplyPosition(1920, 1080, o, 1000, 400, x: 100, y: 200, w: 300);
        var r = OverlayGeometry.Compute(1920, 1080, o, 1000, 400);
        Assert.InRange(r.X, 99, 101);
        Assert.InRange(r.Y, 199, 201);
        Assert.InRange(r.W, 299, 301);
    }

    [Fact]
    public void CenteredCrop_ForVerticalFromHorizontal()
    {
        var c = CropRect.Centered(1920, 1080, 9.0 / 16);
        Assert.Equal(1, c.H, 6);
        Assert.InRange(c.W, 0.316, 0.317);
        Assert.InRange(c.X, 0.341, 0.342);
        var px = c.ToPixels(1920, 1080);
        Assert.InRange(px.W, 606, 608);
    }

    [Theory]
    [InlineData("5", 5)]
    [InlineData("1:30", 90)]
    [InlineData("01:02:03.5", 3723.5)]
    [InlineData("0:02,25", 2.25)]
    public void TimeParse(string text, double expected)
    {
        Assert.True(TimeFormat.TryParse(text, out var s));
        Assert.Equal(expected, s, 6);
    }

    [Fact]
    public void TimeFormat_Formats()
    {
        Assert.Equal("01:05.250", TimeFormat.Format(65.25));
        Assert.Equal("1:00:00.000", TimeFormat.Format(3600));
        Assert.Equal("1.5", TimeFormat.Num(1.5));
        Assert.Equal("0.333", TimeFormat.Num(1.0 / 3));
    }
}
