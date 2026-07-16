using SkiaSharp;

namespace GraphMailer.Service.Services.Reporting;

/// <summary>
/// Renders the daily-volume chart to a PNG (area + line, two series). Used instead of inline
/// SVG because Outlook Classic (Word engine) and new Outlook / webmail both strip SVG; the PNG
/// is embedded as a CID inline image and renders everywhere. Sent and failed use independent
/// vertical scales (left/right axis) so the much smaller failed series stays visible.
/// Drawn at 2× and downscaled by the client for crisp lines on HiDPI displays.
/// </summary>
internal static class DailyChartImage
{
    private const int Scale = 2;

    // Logical layout (×Scale for the bitmap). Mirrors the original SVG geometry.
    private const float X0 = 38, X1 = 566, Top = 12, Baseline = 132;
    private const float CanvasW = 600, CanvasH = 170;

    private static readonly SKColor Grid = SKColor.Parse("#eaeef2");
    private static readonly SKColor Axis = SKColor.Parse("#d0d7de");
    private static readonly SKColor Faint = SKColor.Parse("#8c959f");

    public static byte[] Render(IReadOnlyList<DailyPoint> days, string sentHex = "#0969da", string failedHex = "#cf222e")
    {
        var sent = SKColor.Parse(sentHex);
        var failed = SKColor.Parse(failedHex);

        int n = days.Count;
        float plotW = X1 - X0, plotH = Baseline - Top;
        double sentMax = Math.Max(1, days.Max(d => d.Sent));
        double failedMax = Math.Max(1, days.Max(d => d.Failed));

        float X(int i) => n == 1 ? (X0 + X1) / 2 : X0 + i * plotW / (n - 1);
        float YSent(long v) => (float)(Baseline - v / sentMax * plotH);
        float YFailed(long v) => (float)(Baseline - v / failedMax * plotH);

        var info = new SKImageInfo((int)(CanvasW * Scale), (int)(CanvasH * Scale));
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        canvas.Scale(Scale);

        // Gridlines + baseline
        using (var grid = new SKPaint { Color = Grid, StrokeWidth = 1, IsAntialias = false })
        {
            foreach (var y in new[] { 12f, 42f, 72f, 102f })
                canvas.DrawLine(X0, y, X1, y, grid);
            grid.Color = Axis;
            canvas.DrawLine(X0, Baseline, X1, Baseline, grid);
        }

        DrawSeries(canvas, days, X, YSent, d => d.Sent, sent);
        DrawSeries(canvas, days, X, YFailed, d => d.Failed, failed);

        // Axis labels (left = sent, right = failed)
        DrawText(canvas, ((long)sentMax).ToString(), 34, 15, SKTextAlign.Right, sent);
        DrawText(canvas, ((long)Math.Round(sentMax / 2)).ToString(), 34, 75, SKTextAlign.Right, sent);
        DrawText(canvas, "0", 34, 135, SKTextAlign.Right, sent);
        DrawText(canvas, ((long)failedMax).ToString(), 570, 15, SKTextAlign.Left, failed);
        DrawText(canvas, ((long)Math.Round(failedMax / 2)).ToString(), 570, 75, SKTextAlign.Left, failed);
        DrawText(canvas, "0", 570, 135, SKTextAlign.Left, failed);

        // Date labels (thinned when many)
        int step = n <= 8 ? 1 : (int)Math.Ceiling(n / 8.0);
        for (int i = 0; i < n; i++)
            if (i % step == 0 || i == n - 1)
                DrawText(canvas, days[i].Date.ToString("MM-dd"), X(i), 150, SKTextAlign.Center, Faint);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static void DrawSeries(
        SKCanvas canvas, IReadOnlyList<DailyPoint> days, Func<int, float> x, Func<long, float> y,
        Func<DailyPoint, long> value, SKColor color)
    {
        int n = days.Count;

        // Filled area under the line (semi-transparent)
        using var area = new SKPath();
        area.MoveTo(x(0), Baseline);
        for (int i = 0; i < n; i++) area.LineTo(x(i), y(value(days[i])));
        area.LineTo(x(n - 1), Baseline);
        area.Close();
        using (var fill = new SKPaint { Color = color.WithAlpha(41), Style = SKPaintStyle.Fill, IsAntialias = true })
            canvas.DrawPath(area, fill);

        // Line on top
        using var line = new SKPath();
        for (int i = 0; i < n; i++)
        {
            if (i == 0) line.MoveTo(x(i), y(value(days[i])));
            else line.LineTo(x(i), y(value(days[i])));
        }
        using (var stroke = new SKPaint { Color = color, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round, IsAntialias = true })
            canvas.DrawPath(line, stroke);

        // Data points
        using var dot = new SKPaint { Color = color, Style = SKPaintStyle.Fill, IsAntialias = true };
        float r = n > 15 ? 1.5f : 2f;
        for (int i = 0; i < n; i++)
            canvas.DrawCircle(x(i), y(value(days[i])), r, dot);
    }

    private static void DrawText(SKCanvas canvas, string text, float x, float y, SKTextAlign align, SKColor color)
    {
        using var paint = new SKPaint
        {
            Color = color,
            TextSize = 9,
            IsAntialias = true,
            TextAlign = align,
            Typeface = SKTypeface.FromFamilyName("Consolas")
                       ?? SKTypeface.FromFamilyName("Courier New")
                       ?? SKTypeface.Default,
        };
        canvas.DrawText(text, x, y, paint);
    }
}
