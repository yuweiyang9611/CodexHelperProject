namespace CodexU.Core;

public readonly record struct StatusStripPixelRect(
    double Left,
    double Top,
    double Width,
    double Height)
{
    public double Right => Left + Width;

    public double Bottom => Top + Height;
}

public readonly record struct StatusStripPixelPoint(
    double Left,
    double Top);

public sealed record StatusStripWindowLayout(
    double WidthDip,
    double HeightDip,
    int LeftPixels,
    int TopPixels,
    int WidthPixels,
    int HeightPixels);

public static class StatusStripLayout
{
    public const double PreferredWidthDip = 430d;
    public const double CollapsedHeightDip = 46d;
    public const double ExpandedHeightDip = 290d;
    public const double WorkAreaMarginDip = 8d;
    public const double AnchorRightOffsetDip = 88d;
    public const double AnchorTopOffsetDip = 8d;
    public const double FallbackRightOffsetDip = 18d;
    public const double FallbackTopOffsetDip = 10d;

    public static StatusStripPixelPoint RecoverRequestedPosition(
        StatusStripPixelRect workingArea,
        double dpiScale,
        StatusStripPixelPoint requestedPosition)
    {
        var collapsed = Calculate(
            workingArea,
            anchor: null,
            dpiScale,
            expanded: false,
            requestedPosition);
        return new StatusStripPixelPoint(collapsed.LeftPixels, collapsed.TopPixels);
    }

    public static StatusStripWindowLayout Calculate(
        StatusStripPixelRect workingArea,
        StatusStripPixelRect? anchor,
        double dpiScale,
        bool expanded,
        StatusStripPixelPoint? requestedPosition = null)
    {
        if (!double.IsFinite(dpiScale) || dpiScale <= 0d)
        {
            dpiScale = 1d;
        }

        var workWidth = Math.Max(1d, workingArea.Width);
        var workHeight = Math.Max(1d, workingArea.Height);
        var horizontalMarginPixels = Math.Min(
            Math.Ceiling(WorkAreaMarginDip * dpiScale),
            Math.Max(0d, Math.Floor((workWidth - 1d) / 2d)));
        var verticalMarginPixels = Math.Min(
            Math.Ceiling(WorkAreaMarginDip * dpiScale),
            Math.Max(0d, Math.Floor((workHeight - 1d) / 2d)));
        var usableWidthDip = Math.Max(1d, (workWidth - (horizontalMarginPixels * 2d)) / dpiScale);
        var usableHeightDip = Math.Max(1d, (workHeight - (verticalMarginPixels * 2d)) / dpiScale);
        var widthDip = Math.Min(PreferredWidthDip, usableWidthDip);
        var requestedHeightDip = expanded ? ExpandedHeightDip : CollapsedHeightDip;
        var heightDip = Math.Min(requestedHeightDip, usableHeightDip);
        var widthPixels = Math.Max(1, (int)Math.Ceiling(widthDip * dpiScale));
        var heightPixels = Math.Max(1, (int)Math.Ceiling(heightDip * dpiScale));

        var desiredLeft = requestedPosition?.Left
            ?? (anchor is { } target
                ? target.Right - ((widthDip + AnchorRightOffsetDip) * dpiScale)
                : workingArea.Right - ((widthDip + FallbackRightOffsetDip) * dpiScale));
        var desiredTop = requestedPosition?.Top
            ?? (anchor is { } targetTop
                ? targetTop.Top + (AnchorTopOffsetDip * dpiScale)
                : workingArea.Top + (FallbackTopOffsetDip * dpiScale));
        var minimumLeft = workingArea.Left + horizontalMarginPixels;
        var maximumLeft = Math.Max(minimumLeft, workingArea.Right - horizontalMarginPixels - widthPixels);
        var minimumTop = workingArea.Top + verticalMarginPixels;
        var maximumTop = Math.Max(minimumTop, workingArea.Bottom - verticalMarginPixels - heightPixels);

        return new StatusStripWindowLayout(
            widthDip,
            heightDip,
            (int)Math.Round(Math.Clamp(desiredLeft, minimumLeft, maximumLeft)),
            (int)Math.Round(Math.Clamp(desiredTop, minimumTop, maximumTop)),
            widthPixels,
            heightPixels);
    }
}
