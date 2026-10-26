using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClaudeAE;

// Measurements on rendered frames for matching a reference: colors at points,
// foreground bounds (to track an element over time), holds/cuts, and side-by-side diffs.
internal static class Analysis
{
    // BGRA, not premultiplied.
    internal sealed record Px(byte[] Data, int W, int H)
    {
        public (double R, double G, double B, double A) At(int x, int y)
        {
            var i = (y * W + x) * 4;
            return (Data[i + 2], Data[i + 1], Data[i], Data[i + 3]);
        }
    }

    public static Px Pixels(string path, int decodeWidth = 0) => Imaging.Sta(() => Decode(path, decodeWidth));

    // 3x3 per-channel median: removes compression speckle and ringing without moving edges, so traced
    // outlines come out clean and continuous instead of following the noise.
    public static Px Median3(Px p)
    {
        var o = (byte[])p.Data.Clone();
        var win = new byte[9];
        for (var y = 1; y < p.H - 1; y++)
        for (var x = 1; x < p.W - 1; x++)
        for (var ch = 0; ch < 3; ch++)
        {
            var n = 0;
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
                win[n++] = p.Data[((y + dy) * p.W + x + dx) * 4 + ch];
            Array.Sort(win);
            o[(y * p.W + x) * 4 + ch] = win[4];
        }
        return new Px(o, p.W, p.H);
    }

    static Px Decode(string path, int decodeWidth)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        if (decodeWidth > 0) bi.DecodePixelWidth = decodeWidth;
        bi.StreamSource = new MemoryStream(File.ReadAllBytes(path));
        bi.EndInit();
        bi.Freeze();
        var conv = new FormatConvertedBitmap(bi, PixelFormats.Bgra32, null, 0);
        conv.Freeze();
        var data = new byte[conv.PixelWidth * conv.PixelHeight * 4];
        conv.CopyPixels(data, conv.PixelWidth * 4, 0);
        return new Px(data, conv.PixelWidth, conv.PixelHeight);
    }

    static (double R, double G, double B, double A) Avg(Px p, int x, int y, int r)
    {
        double R = 0, G = 0, B = 0, A = 0;
        var n = 0;
        for (var yy = Math.Max(0, y - r); yy <= Math.Min(p.H - 1, y + r); yy++)
        for (var xx = Math.Max(0, x - r); xx <= Math.Min(p.W - 1, x + r); xx++)
        {
            var c = p.At(xx, yy);
            R += c.R; G += c.G; B += c.B; A += c.A; n++;
        }
        return n == 0 ? (0, 0, 0, 0) : (R / n, G / n, B / n, A / n);
    }

    static string Hex(double r, double g, double b) =>
        $"#{(int)Math.Round(Math.Clamp(r, 0, 255)):X2}{(int)Math.Round(Math.Clamp(g, 0, 255)):X2}{(int)Math.Round(Math.Clamp(b, 0, 255)):X2}";

    static (double R, double G, double B) ParseHex(string hex)
    {
        var h = hex.TrimStart('#');
        return (Convert.ToInt32(h[..2], 16), Convert.ToInt32(h.Substring(2, 2), 16), Convert.ToInt32(h.Substring(4, 2), 16));
    }

    // Colors at points (3x3 average) and the bounds/centroid/mean color of everything that differs from the
    // background, given as a color or as a background-only plate frame (better for gradients).
    public static JsonObject Probe(RenderedFrame f, int[][]? points, int[]? region, string? bgHex, Px? plate, double tol, string? matchHex = null,
        int blobs = 0, int minBlob = 20, int open = 0)
    {
        var p = Pixels(f.Path);
        var o = new JsonObject { ["t"] = Math.Round(f.Time, 3) };
        if (points is { Length: > 0 })
        {
            var arr = new JsonArray();
            foreach (var pt in points)
            {
                var c = Avg(p, pt[0], pt[1], 1);
                arr.Add(new JsonObject { ["at"] = new JsonArray(pt[0], pt[1]), ["hex"] = Hex(c.R, c.G, c.B), ["alpha"] = Math.Round(c.A / 255, 2) });
            }
            o["colors"] = arr;
        }
        if (region == null && bgHex == null && plate == null && matchHex == null) return o;
        var match = matchHex != null ? ParseHex(matchHex) : ((double, double, double)?)null;

        int x0 = Math.Clamp(region?[0] ?? 0, 0, p.W - 1), y0 = Math.Clamp(region?[1] ?? 0, 0, p.H - 1);
        int x1 = Math.Clamp(x0 + (region?[2] ?? p.W), 1, p.W), y1 = Math.Clamp(y0 + (region?[3] ?? p.H), 1, p.H);
        var bg = bgHex != null ? ParseHex(bgHex) : AvgRgb(Avg(p, x0 + 2, y0 + 2, 2));
        long n = 0;
        double sx = 0, sy = 0, sr = 0, sg = 0, sb = 0;
        int bx0 = int.MaxValue, by0 = int.MaxValue, bx1 = -1, by1 = -1;
        var usePlate = plate != null && plate.W == p.W && plate.H == p.H;
        var mask = blobs > 0 ? new bool[(x1 - x0) * (y1 - y0)] : null;
        for (var y = y0; y < y1; y++)
        for (var x = x0; x < x1; x++)
        {
            var c = p.At(x, y);
            if (c.A < 128) continue;
            if (match is { } m)
            {
                // Foreground = pixels close to the given color.
                var mr = c.R - m.Item1; var mg = c.G - m.Item2; var mb = c.B - m.Item3;
                if (Math.Sqrt(mr * mr + mg * mg + mb * mb) > tol) continue;
            }
            else
            {
                var b = usePlate ? AvgRgb(plate!.At(x, y)) : bg;
                var dr = c.R - b.R; var dg = c.G - b.G; var db = c.B - b.B;
                if (Math.Sqrt(dr * dr + dg * dg + db * db) <= tol) continue;
            }
            n++;
            if (mask != null) mask[(y - y0) * (x1 - x0) + (x - x0)] = true;
            sx += x; sy += y; sr += c.R; sg += c.G; sb += c.B;
            if (x < bx0) bx0 = x;
            if (y < by0) by0 = y;
            if (x > bx1) bx1 = x;
            if (y > by1) by1 = y;
        }
        o["fg"] = n == 0 ? null : new JsonObject
        {
            ["bbox"] = new JsonArray(bx0, by0, bx1, by1),
            ["center"] = new JsonArray(Math.Round(sx / n, 1), Math.Round(sy / n, 1)),
            ["size"] = new JsonArray(bx1 - bx0 + 1, by1 - by0 + 1),
            ["area"] = n,
            ["color"] = Hex(sr / n, sg / n, sb / n),
        };
        if (mask != null)
        {
            if (open > 0) mask = Open(mask, x1 - x0, y1 - y0, open);
            o["blobs"] = Blobs(mask, x1 - x0, y1 - y0, x0, y0, blobs, minBlob);
        }
        return o;
    }

    // Erode then dilate with a (2r+1)^2 square: removes lines thinner than about 2r px, keeps solid shapes.
    static bool[] Open(bool[] m, int w, int h, int r)
    {
        bool[] Pass(bool[] src, bool erode)
        {
            var dst = new bool[src.Length];
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var hit = erode;
                for (var dy = -r; dy <= r && hit == erode; dy++)
                for (var dx = -r; dx <= r; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    var v = nx >= 0 && ny >= 0 && nx < w && ny < h && src[ny * w + nx];
                    if (erode && !v) { hit = false; break; }
                    if (!erode && v) { hit = true; break; }
                }
                dst[y * w + x] = hit;
            }
            return dst;
        }
        return Pass(Pass(m, true), false);
    }

    // Connected components (8-neighbour) of the foreground mask, largest first. `r` is the radius of a
    // disc with the same area, handy for balls.
    static JsonArray Blobs(bool[] mask, int w, int h, int ox, int oy, int max, int minArea)
    {
        var label = new int[mask.Length];
        var found = new List<(long Area, int X0, int Y0, int X1, int Y1, double Cx, double Cy)>();
        var stack = new Stack<int>();
        var next = 0;
        for (var i = 0; i < mask.Length; i++)
        {
            if (!mask[i] || label[i] != 0) continue;
            next++;
            long area = 0; double cx = 0, cy = 0;
            int bx0 = int.MaxValue, by0 = int.MaxValue, bx1 = -1, by1 = -1;
            label[i] = next;
            stack.Push(i);
            while (stack.Count > 0)
            {
                var k = stack.Pop();
                int x = k % w, y = k / w;
                area++; cx += x; cy += y;
                bx0 = Math.Min(bx0, x); by0 = Math.Min(by0, y); bx1 = Math.Max(bx1, x); by1 = Math.Max(by1, y);
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    var j = ny * w + nx;
                    if (!mask[j] || label[j] != 0) continue;
                    label[j] = next;
                    stack.Push(j);
                }
            }
            if (area >= minArea) found.Add((area, bx0 + ox, by0 + oy, bx1 + ox, by1 + oy, cx / area + ox, cy / area + oy));
        }
        var arr = new JsonArray();
        foreach (var b in found.OrderByDescending(b => b.Area).Take(max))
        {
            arr.Add(new JsonObject
            {
                ["center"] = new JsonArray(Math.Round(b.Cx, 1), Math.Round(b.Cy, 1)),
                ["bbox"] = new JsonArray(b.X0, b.Y0, b.X1, b.Y1),
                ["area"] = b.Area,
                ["r"] = Math.Round(Math.Sqrt(b.Area / Math.PI), 1),
            });
        }
        return arr;
    }

    static (double R, double G, double B) AvgRgb((double R, double G, double B, double A) c) => (c.R, c.G, c.B);

    // Holds versus motion (and cut-like jumps) from small grayscale thumbnails.
    public static string Scenes(IReadOnlyList<RenderedFrame> frames, double holdPct, double cutPct)
    {
        var thumbs = frames.Select(f => Gray(Pixels(f.Path, 96))).ToList();
        var d = new double[frames.Count];
        for (var i = 1; i < frames.Count; i++)
        {
            double s = 0;
            var a = thumbs[i]; var b = thumbs[i - 1];
            var len = Math.Min(a.Length, b.Length);
            for (var k = 0; k < len; k++) s += Math.Abs(a[k] - b[k]);
            d[i] = 100 * s / (len * 255.0);
        }
        var n = frames.Count;
        var moving = new bool[n];
        for (var i = 1; i < n; i++) moving[i] = d[i] >= holdPct;
        if (n > 1) moving[0] = moving[1];

        var sb = new StringBuilder();
        sb.AppendLine($"Sampled {n} frames {frames[0].Time:0.00}-{frames[^1].Time:0.00}s; change between samples in % (hold < {holdPct}%, jump > {cutPct}%).");
        var start = 0;
        for (var i = 1; i <= n; i++)
        {
            if (i < n && moving[i] == moving[start]) continue;
            // Segment start..i-1; d[k] measures the change arriving at sample k.
            var ks = Enumerable.Range(Math.Max(1, start), Math.Max(0, i - Math.Max(1, start))).ToList();
            sb.Append($"{frames[start].Time,7:0.00} - {frames[i - 1].Time,7:0.00}s  {(moving[start] ? "motion" : "hold"),-6}");
            if (moving[start] && ks.Count > 0) sb.Append($" peak {ks.Max(k => d[k]):0.0}%");
            var jumps = ks.Where(k => d[k] > cutPct).Select(k => $"{frames[k].Time:0.00}s").ToList();
            if (jumps.Count > 0) sb.Append($"  jumps at {string.Join(", ", jumps)}");
            sb.AppendLine();
            start = i;
        }
        return sb.ToString().TrimEnd();
    }

    static byte[] Gray(Px p)
    {
        var g = new byte[p.W * p.H];
        for (var i = 0; i < g.Length; i++)
        {
            var c = p.At(i % p.W, i / p.W);
            g[i] = (byte)Math.Round(0.299 * c.R + 0.587 * c.G + 0.114 * c.B);
        }
        return g;
    }

    // Rows of [A | B | difference]; the difference is the mean absolute RGB difference.
    public static byte[] Compare(IReadOnlyList<RenderedFrame> a, IReadOnlyList<RenderedFrame> b, string nameA, string nameB,
        bool showDiff, int cellWidth, out List<double> diffs)
    {
        var rows = Math.Min(a.Count, b.Count);
        var measured = new List<double>();
        var png = Imaging.Sta(() =>
        {
            var pairs = new List<(BitmapSource A, BitmapSource B, BitmapSource? D, double Diff)>();
            for (var i = 0; i < rows; i++)
            {
                var ia = Imaging.Load(a[i].Path);
                var ib = Imaging.Load(b[i].Path);
                var pa = Decode(a[i].Path, 320);
                var pb = Decode(b[i].Path, 320);
                var (diff, dImg) = DiffImage(pa, pb, showDiff);
                measured.Add(diff);
                pairs.Add((ia, ib, dImg, diff));
            }
            var cellH = (int)Math.Round(cellWidth * pairs[0].A.PixelHeight / (double)pairs[0].A.PixelWidth);
            const int pad = 8, labelH = 22, headH = 28;
            var cols = showDiff ? 3 : 2;
            int w = cols * cellWidth + (cols + 1) * pad, h = headH + rows * (cellH + labelH + pad) + pad;
            var fg = Imaging.Brush("#D8D8DE");
            var dim = Imaging.Brush("#8A8A94");
            var checker = Imaging.CheckerBrush();
            return Imaging.Render(w, h, dc =>
            {
                dc.DrawRectangle(Imaging.Brush("#111114"), null, new Rect(0, 0, w, h));
                Imaging.Label(dc, nameA, pad, 6, 13, fg);
                Imaging.Label(dc, nameB, pad * 2 + cellWidth, 6, 13, fg);
                if (showDiff) Imaging.Label(dc, "difference (brighter = more different)", pad * 3 + cellWidth * 2, 6, 13, fg);
                for (var i = 0; i < rows; i++)
                {
                    var y = headH + i * (cellH + labelH + pad);
                    var cells = new List<BitmapSource?> { pairs[i].A, pairs[i].B };
                    if (showDiff) cells.Add(pairs[i].D);
                    for (var c = 0; c < cells.Count; c++)
                    {
                        var rect = new Rect(pad + c * (cellWidth + pad), y, cellWidth, cellH);
                        dc.DrawRectangle(checker, null, rect);
                        if (cells[c] != null) dc.DrawImage(cells[c], rect);
                    }
                    Imaging.Label(dc, $"A {a[i].Time:0.00}s   B {b[i].Time:0.00}s   diff {pairs[i].Diff:0.0}%", pad + 2, y + cellH + 3, 12, dim);
                }
            });
        });
        diffs = measured;
        return png;
    }

    static (double Diff, BitmapSource? Img) DiffImage(Px a, Px b, bool makeImage)
    {
        int w = Math.Min(a.W, b.W), h = Math.Min(a.H, b.H);
        var img = makeImage ? new byte[w * h * 4] : null;
        double sum = 0;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var ca = a.At(x, y); var cb = b.At(x, y);
            var dd = (Math.Abs(ca.R - cb.R) + Math.Abs(ca.G - cb.G) + Math.Abs(ca.B - cb.B)) / 3.0;
            sum += dd;
            if (img == null) continue;
            var v = (byte)Math.Min(255, dd * 3);
            var i = (y * w + x) * 4;
            img[i] = v; img[i + 1] = v; img[i + 2] = v; img[i + 3] = 255;
        }
        BitmapSource? bmp = null;
        if (img != null)
        {
            bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, img, w * 4);
            bmp.Freeze();
        }
        return (100 * sum / (w * h * 255.0), bmp);
    }
}
