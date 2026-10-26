using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClaudeAE;

// Frame downscaling, contact sheets and motion charts, drawn with WPF (in the
// .NET desktop runtime, so no extra packages). WPF needs an STA thread.
internal static class Imaging
{
    static readonly string[] DimColors = ["#4FC3F7", "#FFB74D", "#81C784", "#E57373"];

    // `region` (x, y, width, height in frame pixels) crops first and may magnify up to 4x, for inspecting detail.
    public static byte[] Fit(string path, int maxWidth, int[]? region = null) => Sta(() =>
    {
        BitmapSource src = Load(path);
        var maxScale = 1.0;
        if (region is { Length: 4 })
        {
            int x = Math.Clamp(region[0], 0, src.PixelWidth - 1), y = Math.Clamp(region[1], 0, src.PixelHeight - 1);
            int cw = Math.Clamp(region[2], 1, src.PixelWidth - x), ch = Math.Clamp(region[3], 1, src.PixelHeight - y);
            src = new CroppedBitmap(src, new Int32Rect(x, y, cw, ch));
            src.Freeze();
            maxScale = 4.0;
        }
        var s = Math.Min(maxScale, maxWidth / (double)src.PixelWidth);
        int w = Math.Max(1, (int)Math.Round(src.PixelWidth * s)), h = Math.Max(1, (int)Math.Round(src.PixelHeight * s));
        var checker = CheckerBrush();
        return Render(w, h, dc =>
        {
            dc.DrawRectangle(checker, null, new Rect(0, 0, w, h));
            dc.DrawImage(src, new Rect(0, 0, w, h));
        });
    });

    public static byte[] Sheet(IReadOnlyList<RenderedFrame> frames, int columns, int cellWidth, string title) => Sta(() =>
    {
        var imgs = frames.Select(f => Load(f.Path)).ToList();
        var cellH = (int)Math.Round(cellWidth * imgs[0].PixelHeight / (double)imgs[0].PixelWidth);
        const int pad = 8, labelH = 22, titleH = 30;
        var cols = Math.Max(1, Math.Min(columns, frames.Count));
        var rows = (frames.Count + cols - 1) / cols;
        int w = cols * cellWidth + (cols + 1) * pad, h = titleH + rows * (cellH + labelH + pad) + pad;
        var fg = Brush("#D8D8DE");
        var dim = Brush("#8A8A94");
        var checker = CheckerBrush();
        return Render(w, h, dc =>
        {
            dc.DrawRectangle(Brush("#111114"), null, new Rect(0, 0, w, h));
            Label(dc, title, pad, 6, 14, fg);
            for (var i = 0; i < frames.Count; i++)
            {
                int c = i % cols, r = i / cols;
                double x = pad + c * (cellWidth + pad), y = titleH + r * (cellH + labelH + pad);
                var rect = new Rect(x, y, cellWidth, cellH);
                dc.DrawRectangle(checker, null, rect);
                dc.DrawImage(imgs[i], rect);
                Label(dc, $"#{i + 1}   {frames[i].Time:0.00}s   f{frames[i].Index}", x + 2, y + cellH + 3, 12, dim);
            }
        });
    });

    public static byte[] MotionChart(double[] t, double[][] v, double[] keys, string title, out string summary, double fps = 0)
    {
        var n = t.Length;
        var dims = v[0].Length;
        // Speed between consecutive samples, at the later sample.
        var sp = new double[n];
        for (var i = 1; i < n; i++)
        {
            double s2 = 0;
            for (var d = 0; d < dims; d++) s2 += Math.Pow(v[i][d] - v[i - 1][d], 2);
            var dt = t[i] - t[i - 1];
            sp[i] = dt > 0 ? Math.Sqrt(s2) / dt : 0;
        }
        summary = Summarize(t, v, sp, keys, title, fps > 0 ? fps : (n > 1 ? 1 / (t[1] - t[0]) : 0));

        var live = Enumerable.Range(0, dims).Where(d => v.Max(x => x[d]) - v.Min(x => x[d]) > 1e-6).ToList();
        if (live.Count == 0) live.Add(0);
        var peak = sp.Max();

        return Sta(() =>
        {
            const int W = 1200, H = 640, L = 70, R = 24, top1 = 60, h1 = 236, top2 = 350, h2 = 230;
            double t0 = t[0], t1 = t[^1];
            double X(double tt) => L + (tt - t0) / Math.Max(1e-9, t1 - t0) * (W - L - R);
            var fg = Brush("#D8D8DE");
            var dim = Brush("#8A8A94");
            var grid = Pen(Brush("#2A2A30"), 1);
            var keyPen = Pen(Brush("#6A6A74"), 1, dashed: true);
            return Render(W, H, dc =>
            {
                dc.DrawRectangle(Brush("#16161A"), null, new Rect(0, 0, W, H));
                Label(dc, title, L, 10, 16, fg);
                Label(dc, "value (each channel normalized to its own range)", L, top1 - 18, 11, dim);
                Label(dc, "speed (units/s)", L, top2 - 18, 11, dim);

                foreach (var frac in new[] { 0.0, 0.5, 1.0 })
                {
                    dc.DrawLine(grid, new Point(L, top1 + h1 * frac), new Point(W - R, top1 + h1 * frac));
                    dc.DrawLine(grid, new Point(L, top2 + h2 * frac), new Point(W - R, top2 + h2 * frac));
                }
                Label(dc, peak.ToString("0.#", CultureInfo.InvariantCulture), L - 6, top2 - 7, 11, dim, alignRight: true);
                Label(dc, "0", L - 6, top2 + h2 - 7, 11, dim, alignRight: true);

                foreach (var k in keys.Where(k => k >= t0 && k <= t1))
                {
                    dc.DrawLine(keyPen, new Point(X(k), top1), new Point(X(k), top2 + h2));
                    Label(dc, "key", X(k) + 3, top1 + 2, 10, dim);
                }

                var step = NiceStep(t1 - t0);
                for (var tt = Math.Ceiling(t0 / step) * step; tt <= t1 + 1e-9; tt += step)
                {
                    dc.DrawLine(grid, new Point(X(tt), top2 + h2), new Point(X(tt), top2 + h2 + 5));
                    Label(dc, $"{tt:0.##}s", X(tt) - 12, top2 + h2 + 8, 11, dim);
                }

                for (var li = 0; li < live.Count; li++)
                {
                    var d = live[li];
                    double lo = v.Min(x => x[d]), hi = v.Max(x => x[d]), span = Math.Max(1e-9, hi - lo);
                    var pts = new List<Point>();
                    for (var i = 0; i < n; i++) pts.Add(new Point(X(t[i]), top1 + h1 - (v[i][d] - lo) / span * h1));
                    var color = Brush(DimColors[li % DimColors.Length]);
                    Poly(dc, pts, Pen(color, 2));
                    Label(dc, $"[{d}] {lo:0.##} .. {hi:0.##}", W - R - 190, top1 + 4 + li * 15, 11, color);
                }

                var spts = new List<Point>();
                for (var i = 1; i < n; i++) spts.Add(new Point(X(t[i]), top2 + h2 - (peak > 0 ? sp[i] / peak : 0) * h2));
                Poly(dc, spts, Pen(Brush("#F06292"), 2));
            });
        });
    }

    static string Summarize(double[] t, double[][] v, double[] sp, double[] keys, string title, double fps)
    {
        var n = t.Length;
        var sb = new StringBuilder();
        sb.Append($"{title}: sampled {t[0]:0.00}-{t[^1]:0.00}s at {fps:0.##} fps");
        sb.AppendLine(keys.Length > 0 ? $"; keys at {string.Join(", ", keys.Select(k => $"{k:0.00}s"))}." : "; no keyframes.");
        var peak = sp.Max();
        if (peak <= 1e-9)
        {
            sb.Append("No motion in this range.");
            return sb.ToString();
        }
        // Separate moves (an entrance and an exit, say) are judged on their own: runs of frames
        // above 2% of the overall peak speed, bridging dips of up to two frames.
        var threshold = peak * 0.02;
        var moves = new List<(int First, int Last)>();
        for (var i = 1; i < n; i++)
        {
            if (sp[i] <= threshold) continue;
            if (moves.Count > 0 && i - moves[^1].Last <= 3) moves[^1] = (moves[^1].First, i);
            else moves.Add((i, i));
        }
        for (var m = 0; m < moves.Count; m++)
        {
            var (first, last) = moves[m];
            var ip = first;
            for (var i = first; i <= last; i++) if (sp[i] > sp[ip]) ip = i;
            double mp = sp[ip], ms = t[first - 1], me = t[last];
            if (mp < peak * 0.08)
            {
                // Overshoot recoveries and drifts are too slow to judge as moves of their own.
                sb.AppendLine($"Move {m + 1}: {ms:0.00}-{me:0.00}s, small settle (peak {mp:0.#}/s, {100 * mp / peak:0}% of the fastest move), e.g. recovering from an overshoot.");
                continue;
            }
            double startPct = 100 * sp[first] / mp, endPct = 100 * sp[last] / mp;
            bool fastStart = startPct >= 35, fastEnd = endPct >= 35;
            var shape = last >= n - 1 ? "still moving at the end of the sampled range"
                : !fastStart && !fastEnd ? "eases in and out"
                : fastStart && !fastEnd ? "fast start that eases to rest (arrival)"
                : !fastStart ? "accelerates into its last frame (exit): fine if the layer is hidden or off-frame by then, otherwise it stops abruptly"
                : "starts and stops abruptly (no easing)";

            var notes = new List<string>();
            var seg = Enumerable.Range(first, last - first + 1).Select(i => sp[i]).ToList();
            if (seg.Count > 4)
            {
                var mean = seg.Average();
                var cv = Math.Sqrt(seg.Average(s => (s - mean) * (s - mean))) / mean;
                if (cv < 0.12) notes.Add("near-constant speed, reads linear/mechanical");
            }
            for (var d = 0; d < v[0].Length; d++)
            {
                double a = v[first - 1][d], b = v[last][d], range = Math.Abs(b - a);
                if (range < 1e-6) continue;
                double beyond = 0;
                for (var i = first; i <= last; i++)
                    beyond = Math.Max(beyond, b > a ? v[i][d] - b : b - v[i][d]);
                if (beyond > range * 0.01) notes.Add($"channel [{d}] overshoots its end value by {100 * beyond / range:0}%");
            }
            sb.AppendLine($"Move {m + 1}: {ms:0.00}-{me:0.00}s ({me - ms:0.00}s), peak {mp:0.#}/s at {t[ip]:0.00}s " +
                          $"({100 * (t[ip] - ms) / Math.Max(1e-9, me - ms):0}% in); first/last frame at {startPct:0}%/{endPct:0}% of its peak: {shape}" +
                          (notes.Count > 0 ? "; " + string.Join("; ", notes) : "") + ".");
        }
        return sb.ToString().TrimEnd();
    }

    // Test helper: a gradient frame with a ball that moves with `phase`.
    public static byte[] Synthetic(int w, int h, double phase) => Sta(() => Render(w, h, dc =>
    {
        var bg = new LinearGradientBrush(Color.FromRgb(20, 24, 40), Color.FromRgb(70, 30, 60), 0);
        dc.DrawRectangle(bg, null, new Rect(0, 0, w, h));
        dc.DrawEllipse(Brush("#FF5A36"), null, new Point(w * (0.15 + 0.7 * phase), h * 0.5), h * 0.12, h * 0.12);
        Label(dc, $"phase {phase:0.00}", 40, 40, 64, Brush("#FFFFFF"));
    }));

    // Test helper: a black ring (centerline radius r, stroke width sw) on white.
    public static byte[] SyntheticRing(int w, int h, double cx, double cy, double r, double sw) => Sta(() => Render(w, h, dc =>
    {
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
        var pen = new Pen(Brushes.Black, sw);
        pen.Freeze();
        dc.DrawEllipse(null, pen, new Point(cx, cy), r, r);
    }));

    // ------------------------------------------------------------- plumbing

    internal static T Sta<T>(Func<T> f)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = f(); }
            catch (Exception e) { error = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        return result;
    }

    internal static BitmapSource Load(string path)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bi.StreamSource = new MemoryStream(File.ReadAllBytes(path));
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    internal static byte[] Render(int w, int h, Action<DrawingContext> draw)
    {
        var dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
        using (var dc = dv.RenderOpen()) draw(dc);
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    internal static SolidColorBrush Brush(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    static Pen Pen(Brush b, double width, bool dashed = false)
    {
        var p = new Pen(b, width);
        if (dashed) p.DashStyle = new DashStyle([4.0, 4.0], 0);
        p.Freeze();
        return p;
    }

    // Transparent areas show as a dark checkerboard, so missing backgrounds are obvious.
    internal static Brush CheckerBrush()
    {
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(Brush("#2A2A2F"), null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        var dark = new GeometryGroup();
        dark.Children.Add(new RectangleGeometry(new Rect(0, 0, 8, 8)));
        dark.Children.Add(new RectangleGeometry(new Rect(8, 8, 8, 8)));
        g.Children.Add(new GeometryDrawing(Brush("#1E1E22"), null, dark));
        var b = new DrawingBrush(g) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute };
        b.Freeze();
        return b;
    }

    internal static void Label(DrawingContext dc, string s, double x, double y, double size, Brush b, bool alignRight = false)
    {
        var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, b, 1.0);
        dc.DrawText(ft, new Point(alignRight ? x - ft.Width : x, y));
    }

    static void Poly(DrawingContext dc, List<Point> pts, Pen pen)
    {
        if (pts.Count < 2) return;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(pts[0], false, false);
            c.PolyLineTo(pts.Skip(1).ToList(), true, true);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }

    static double NiceStep(double span)
    {
        foreach (var s in new[] { 0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 30 })
            if (span / s <= 12) return s;
        return 60;
    }
}
