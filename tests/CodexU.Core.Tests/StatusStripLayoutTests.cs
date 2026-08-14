using CodexU.Core;

namespace CodexU.Core.Tests;

public sealed class StatusStripLayoutTests
{
    [Theory]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void FractionalDpiLayout_RemainsInsideWorkAreaMargins(double dpiScale)
    {
        var workArea = new StatusStripPixelRect(0, 0, 1920, 1040);
        var anchor = new StatusStripPixelRect(180, 80, 1600, 900);

        var layout = StatusStripLayout.Calculate(workArea, anchor, dpiScale, expanded: true);

        AssertInsideWorkArea(layout, workArea, dpiScale);
        Assert.Equal(
            (int)Math.Ceiling(StatusStripLayout.PreferredWidthDip * dpiScale),
            layout.WidthPixels);
        Assert.Equal(
            (int)Math.Ceiling(StatusStripLayout.ExpandedHeightDip * dpiScale),
            layout.HeightPixels);
    }

    [Fact]
    public void SmallHighDpiWorkArea_ShrinksExpandedWindowInsteadOfClipping()
    {
        var workArea = new StatusStripPixelRect(0, 0, 600, 400);

        var layout = StatusStripLayout.Calculate(
            workArea,
            anchor: null,
            dpiScale: 2d,
            expanded: true);

        Assert.Equal(284d, layout.WidthDip);
        Assert.Equal(184d, layout.HeightDip);
        AssertInsideWorkArea(layout, workArea, 2d);
    }

    [Fact]
    public void NegativeOriginMonitor_UsesItsOwnCoordinatesAndDpi()
    {
        var workArea = new StatusStripPixelRect(-2560, -80, 2560, 1360);
        var anchor = new StatusStripPixelRect(-2460, -40, 2200, 1200);

        var layout = StatusStripLayout.Calculate(
            workArea,
            anchor,
            dpiScale: 1.5d,
            expanded: false);

        AssertInsideWorkArea(layout, workArea, 1.5d);
        Assert.True(layout.LeftPixels < 0);
        Assert.Equal(
            (int)Math.Ceiling(StatusStripLayout.CollapsedHeightDip * 1.5d),
            layout.HeightPixels);
    }

    [Fact]
    public void RequestedPosition_IsPreservedAndClampedToWorkArea()
    {
        var workArea = new StatusStripPixelRect(-1920, 0, 1920, 1080);

        var positioned = StatusStripLayout.Calculate(
            workArea,
            anchor: null,
            dpiScale: 1d,
            expanded: false,
            requestedPosition: new StatusStripPixelPoint(-1200, 200));
        var clamped = StatusStripLayout.Calculate(
            workArea,
            anchor: null,
            dpiScale: 1d,
            expanded: true,
            requestedPosition: new StatusStripPixelPoint(-50, 1000));

        Assert.Equal(-1200, positioned.LeftPixels);
        Assert.Equal(200, positioned.TopPixels);
        AssertInsideWorkArea(positioned, workArea, 1d);
        AssertInsideWorkArea(clamped, workArea, 1d);
        Assert.Equal(-438, clamped.LeftPixels);
        Assert.Equal(782, clamped.TopPixels);
    }

    [Fact]
    public void RecoverRequestedPosition_UsesCollapsedSizeSoHoverDoesNotRewritePlacement()
    {
        var workArea = new StatusStripPixelRect(0, 0, 1920, 1040);
        var requested = new StatusStripPixelPoint(1400, 900);

        var recovered = StatusStripLayout.RecoverRequestedPosition(workArea, 1d, requested);
        var expanded = StatusStripLayout.Calculate(
            workArea,
            anchor: null,
            dpiScale: 1d,
            expanded: true,
            requestedPosition: recovered);

        Assert.Equal(1400, recovered.Left);
        Assert.Equal(900, recovered.Top);
        Assert.Equal(742, expanded.TopPixels);
    }

    [Fact]
    public void RecoverRequestedPosition_ClampsDisconnectedMonitorCoordinates()
    {
        var remainingWorkArea = new StatusStripPixelRect(0, 0, 1920, 1040);

        var recovered = StatusStripLayout.RecoverRequestedPosition(
            remainingWorkArea,
            1.25d,
            new StatusStripPixelPoint(-2500, 140));

        Assert.Equal(10, recovered.Left);
        Assert.Equal(140, recovered.Top);
    }

    private static void AssertInsideWorkArea(
        StatusStripWindowLayout layout,
        StatusStripPixelRect workArea,
        double dpiScale)
    {
        var margin = (int)Math.Ceiling(StatusStripLayout.WorkAreaMarginDip * dpiScale);
        Assert.True(layout.LeftPixels >= workArea.Left + margin);
        Assert.True(layout.TopPixels >= workArea.Top + margin);
        Assert.True(layout.LeftPixels + layout.WidthPixels <= workArea.Right - margin);
        Assert.True(layout.TopPixels + layout.HeightPixels <= workArea.Bottom - margin);
    }
}
