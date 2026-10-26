using System.Text.Json.Nodes;

namespace ClaudeAE;

// Raster-to-vector tracing for recreating reference artwork: marching squares on a scalar field
// (sub-pixel edges), stitched into closed loops, simplified (Ramer-Douglas-Peucker) and smoothed
// into Bezier paths. Holes run opposite to their outlines and loops are filled non-zero, so holes come out right.
internal static class Trace
{
    // Palette: for mode "nearest", the other colours `Color` competes with (the background included).
    // Targets: for mode "nearest", a set of colours that all count as inside (used for seam-free colour stacks).
    // BgModel: a radial grey background (centre cx, cy; grey g0 there to g1 at radius r) that also competes, per pixel,
    // in mode "nearest", so the same grey counts as artwork where the background is lighter and as background where not.
    // EdgeSmooth: Gaussian sigma (px of arc) for corner-preserving smoothing of every contour (SmoothLoop), against
    // the wobble compression noise puts into edges that were straight or smooth in the original artwork.
    // MinHole: holes smaller than this (px²) are filled.
    public sealed record Options(string Mode, double Threshold, string? Color, double Tolerance, double Epsilon, double MinArea, bool Smooth, double CornerAngle,
        List<(double R, double G, double B)>? Palette = null, List<(double R, double G, double B)>? Targets = null,
        (double Cx, double Cy, double G0, double G1, double R)? BgModel = null, double EdgeSmooth = 0, double MinHole = 0);

    static double BgDist((double R, double G, double B, double A) c, int x, int y, Options o)
    {
        if (o.BgModel is not { } m) return double.MaxValue / 4;
        var rr = Math.Sqrt((x + 0.5 - m.Cx) * (x + 0.5 - m.Cx) + (y + 0.5 - m.Cy) * (y + 0.5 - m.Cy));
        var g = m.G0 + (m.G1 - m.G0) * Math.Min(1, rr / m.R);
        return Dist(c, (g, g, g));
    }

    public sealed record Path(List<(double X, double Y)> Points, List<(double X, double Y)> In, List<(double X, double Y)> Out);

    public static (List<Path> Paths, string MeanColor, int[] BBox) Run(Analysis.Px p, int[] region, Options o)
    {
        int x0 = Math.Clamp(region[0], 0, p.W - 1), y0 = Math.Clamp(region[1], 0, p.H - 1);
        int w = Math.Clamp(region[2], 2, p.W - x0), h = Math.Clamp(region[3], 2, p.H - y0);

        // Field: inside where value < 0. One pixel of "outside" padding closes every loop.
        int gw = w + 2, gh = h + 2;
        var f = new double[gw * gh];
        Array.Fill(f, 1.0);
        var target = o.Color != null ? Hex(o.Color) : (0, 0, 0);
        double sr = 0, sg = 0, sb = 0; long n = 0;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var c = p.At(x0 + x, y0 + y);
            var lum = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
            double v = o.Mode switch
            {
                "light" => (o.Threshold - lum) / 255.0,
                "color" => (Dist(c, target) - o.Tolerance) / 255.0,
                "far" => (o.Tolerance - Dist(c, target)) / 255.0, // anything at least `tolerance` away from `color`
                // closer to `color` than to any palette colour: the edge lands on the 50% mix between neighbours
                "nearest" => ((o.Targets != null ? Nearest(c, o.Targets) : Dist(c, target)) - Math.Min(Nearest(c, o.Palette), BgDist(c, x0 + x, y0 + y, o))) / 255.0,
                _ => (lum - o.Threshold) / 255.0, // dark
            };
            if (c.A < 128) v = 1;
            f[(y + 1) * gw + x + 1] = v;
            if (v < 0) { sr += c.R; sg += c.G; sb += c.B; n++; }
        }

        var (paths, bbox) = FromField(f, gw, gh, x0, y0, o);
        var mean = n == 0 ? "#000000" : $"#{(int)(sr / n):X2}{(int)(sg / n):X2}{(int)(sb / n):X2}";
        return (paths, mean, bbox);
    }

    // Padded field (inside < 0) to smoothed comp-space paths and their bbox.
    static (List<Path> Paths, int[] BBox) FromField(double[] f, int gw, int gh, int x0, int y0, Options o)
    {
        var loops = March(f, gw, gh);
        // Loops of one field never cross, so each one's nesting depth tells outlines (even) from holes (odd). Outlines
        // run one way and holes the other, filled non-zero: where smoothing folds a wiggly boundary over itself the
        // overlap stays filled instead of turning into a hole, as it would under even-odd.
        var boxes = loops.Select(l => (X0: l.Min(q => q.X), Y0: l.Min(q => q.Y), X1: l.Max(q => q.X), Y1: l.Max(q => q.Y))).ToList();
        var depth = new int[loops.Count];
        for (var i = 0; i < loops.Count; i++)
        {
            var pt = loops[i][0];
            for (var j = 0; j < loops.Count; j++)
                if (i != j && pt.X >= boxes[j].X0 && pt.X <= boxes[j].X1 && pt.Y >= boxes[j].Y0 && pt.Y <= boxes[j].Y1 && Inside(loops[j], pt)) depth[i]++;
            if ((depth[i] % 2 == 0) != (Area(loops[i]) > 0)) loops[i].Reverse();
        }
        // holes smaller than MinHole are compression specks, not artwork
        if (o.MinHole > 0) loops = loops.Where((l, i) => depth[i] % 2 == 0 || Math.Abs(Area(l)) >= o.MinHole).ToList();
        var paths = new List<Path>();
        double bx0 = double.MaxValue, by0 = double.MaxValue, bx1 = double.MinValue, by1 = double.MinValue;
        foreach (var loop in loops)
        {
            if (Math.Abs(Area(loop)) < o.MinArea) continue;
            var simple = Simplify(o.EdgeSmooth > 0 ? SmoothLoop(loop, o.EdgeSmooth) : loop, o.Epsilon);
            if (simple.Count < 3) continue;
            // Back to comp coordinates: undo the padding; pixel centers sit at +0.5 in AE.
            var pts = simple.Select(q => (q.X - 1 + x0 + 0.5, q.Y - 1 + y0 + 0.5)).ToList();
            foreach (var (px, py) in pts)
            {
                bx0 = Math.Min(bx0, px); by0 = Math.Min(by0, py);
                bx1 = Math.Max(bx1, px); by1 = Math.Max(by1, py);
            }
            paths.Add(o.Smooth ? Bezier(pts, o.CornerAngle) : Straight(pts));
        }
        var bbox = paths.Count == 0 ? new[] { 0, 0, 0, 0 } : new[] { (int)Math.Floor(bx0), (int)Math.Floor(by0), (int)Math.Ceiling(bx1), (int)Math.Ceiling(by1) };
        return (paths, bbox);
    }

    // Colour layers for flat multi-colour art, bottom to top. Each colour's edges against the colours below it (and so
    // drawn under it) are its exact 50% boundaries; where a colour above it is nearest, it reaches only `extend` px
    // under that colour, enough to hide the seam, so no other layer puts an edge on a boundary that isn't its own.
    // The background takes no part: the caller mattes the result with the silhouette.
    public static List<(int Index, List<Path> Paths)> StackLayers(Analysis.Px p, int[] region, Options o, List<(double R, double G, double B)> colors, double extend)
    {
        int x0 = Math.Clamp(region[0], 0, p.W - 1), y0 = Math.Clamp(region[1], 0, p.H - 1);
        int w = Math.Clamp(region[2], 2, p.W - x0), h = Math.Clamp(region[3], 2, p.H - y0);
        int k = colors.Count, gw = w + 2, gh = h + 2;
        var d = new double[w * h * k];
        var nearest = new int[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var c = p.At(x0 + x, y0 + y);
            int i = y * w + x, best = 0;
            for (var m = 0; m < k; m++)
            {
                // Integer colours are often exactly as far from two palette colours (a flat compression block can be
                // all such pixels); without a tie-break neither colour would own them and the stack gets a hole.
                d[i * k + m] = Dist(c, colors[m]) + m * 1e-6;
                if (d[i * k + m] < d[i * k + best]) best = m;
            }
            nearest[i] = best;
        }
        var result = new List<(int, List<Path>)>();
        var own = new double[w * h];
        var dist = new double[w * h];
        for (var m = 0; m < k; m++)
        {
            for (var i = 0; i < w * h; i++)
            {
                double other = double.MaxValue;
                for (var q = 0; q < k; q++) if (q != m && d[i * k + q] < other) other = d[i * k + q];
                own[i] = (d[i * k + m] - other) / 255.0;
                dist[i] = own[i] < 0 ? 0 : 1e9;
            }
            // two-pass chamfer distance (px) to this colour's own region
            const double D1 = 1, D2 = 1.41421356;
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x; var v = dist[i];
                if (x > 0) v = Math.Min(v, dist[i - 1] + D1);
                if (y > 0) { v = Math.Min(v, dist[i - w] + D1); if (x > 0) v = Math.Min(v, dist[i - w - 1] + D2); if (x < w - 1) v = Math.Min(v, dist[i - w + 1] + D2); }
                dist[i] = v;
            }
            for (var y = h - 1; y >= 0; y--)
            for (var x = w - 1; x >= 0; x--)
            {
                var i = y * w + x; var v = dist[i];
                if (x < w - 1) v = Math.Min(v, dist[i + 1] + D1);
                if (y < h - 1) { v = Math.Min(v, dist[i + w] + D1); if (x < w - 1) v = Math.Min(v, dist[i + w + 1] + D2); if (x > 0) v = Math.Min(v, dist[i + w - 1] + D2); }
                dist[i] = v;
            }
            var f = new double[gw * gh];
            Array.Fill(f, 1.0);
            var any = false;
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                var v = own[i];
                if (v >= 0 && nearest[i] > m && dist[i] <= extend) v = -1e-3;
                f[(y + 1) * gw + x + 1] = v;
                if (v < 0) any = true;
            }
            if (!any) continue;
            var (paths, _) = FromField(f, gw, gh, x0, y0, o);
            if (paths.Count > 0) result.Add((m, paths));
        }
        return result;
    }

    public sealed record GradFit(int Index, (double X, double Y) P0, (double X, double Y) P1,
        (double R, double G, double B) C0, (double R, double G, double B) C1, List<Path> Paths);

    // Linear gradients inside a flat-colour stack. For each colour, a least-squares linear colour field over its core
    // pixels (nearer to it than to any other colour by a margin, and inside the silhouette `sil`, which is traced like
    // Run's nearest mode). Where that is a real gradient (end colours at least minContrast apart, explaining over half
    // the colour variance), the colour's exact region (nearest to it and inside the silhouette, no extension) comes back
    // with the gradient's end points (comp px) and colours, to be filled with a ramp over the flat colour.
    public static List<GradFit> Gradients(Analysis.Px p, int[] region, Options o, Options sil, List<(double R, double G, double B)> colors, double minContrast)
    {
        int x0 = Math.Clamp(region[0], 0, p.W - 1), y0 = Math.Clamp(region[1], 0, p.H - 1);
        int w = Math.Clamp(region[2], 2, p.W - x0), h = Math.Clamp(region[3], 2, p.H - y0);
        int k = colors.Count, gw = w + 2, gh = h + 2;
        var sf = new double[w * h];
        var d = new double[w * h * k];
        var px = new (double R, double G, double B)[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var c = p.At(x0 + x, y0 + y);
            int i = y * w + x;
            px[i] = (c.R, c.G, c.B);
            sf[i] = c.A < 128 ? 1 : (Nearest(c, sil.Targets) - Math.Min(Nearest(c, sil.Palette), BgDist(c, x0 + x, y0 + y, sil))) / 255.0;
            for (var m = 0; m < k; m++) d[i * k + m] = Dist(c, colors[m]) + m * 1e-6; // tie-break as in StackLayers
        }
        // Colours whose shared boundary is a soft transition (image gradient there small next to their colour distance:
        // a band of one gradient sliced up by the palette) are "smooth"; each such band gets its own local ramp so that
        // neighbouring bands meet on matching colours. Colours behind sharp edges keep the stricter test.
        var lab = new int[w * h];
        for (var i = 0; i < w * h; i++)
        {
            if (sf[i] >= 0) { lab[i] = -1; continue; }
            int best = 0;
            for (var m = 1; m < k; m++) if (d[i * k + m] < d[i * k + best]) best = m;
            lab[i] = best;
        }
        double Grad(int x, int y)
        {
            int xl = Math.Max(0, x - 1), xr = Math.Min(w - 1, x + 1), yu = Math.Max(0, y - 1), yd = Math.Min(h - 1, y + 1);
            var (l, r, u, dn) = (px[y * w + xl], px[y * w + xr], px[yu * w + x], px[yd * w + x]);
            double gx = Math.Sqrt(Sq(r.R - l.R) + Sq(r.G - l.G) + Sq(r.B - l.B)) / (xr - xl), gy = Math.Sqrt(Sq(dn.R - u.R) + Sq(dn.G - u.G) + Sq(dn.B - u.B)) / (yd - yu);
            return Math.Max(gx, gy);
        }
        var gsum = new double[k, k];
        var gcnt = new int[k, k];
        for (var y = 0; y < h - 1; y++)
        for (var x = 0; x < w - 1; x++)
        {
            int a = lab[y * w + x];
            if (a < 0) continue;
            foreach (var nb in new[] { lab[y * w + x + 1], lab[(y + 1) * w + x] })
            {
                if (nb < 0 || nb == a) continue;
                var gm = Grad(x, y);
                gsum[a, nb] += gm; gsum[nb, a] += gm; gcnt[a, nb]++; gcnt[nb, a]++;
            }
        }
        var smooth = new bool[k];
        for (var a = 0; a < k; a++)
        for (var b = 0; b < k; b++)
        {
            if (a == b || gcnt[a, b] < 20) continue;
            var dist = Math.Sqrt(Sq(colors[a].R - colors[b].R) + Sq(colors[a].G - colors[b].G) + Sq(colors[a].B - colors[b].B));
            if (dist > 1 && gsum[a, b] / gcnt[a, b] / dist < 0.3) smooth[a] = true;
        }
        var result = new List<GradFit>();
        var own = new double[w * h];
        for (var m = 0; m < k; m++)
        {
            var idx = new List<int>();
            for (var i = 0; i < w * h; i++)
            {
                double other = double.MaxValue;
                for (var q = 0; q < k; q++) if (q != m && d[i * k + q] < other) other = d[i * k + q];
                own[i] = (d[i * k + m] - other) / 255.0;
                if (own[i] < (smooth[m] ? -0.01 : -0.04) && sf[i] < -0.04) idx.Add(i);
            }
            if (idx.Count < (smooth[m] ? 40 : 200)) continue;
            double mx = 0, my = 0, mr = 0, mg = 0, mb = 0;
            foreach (var i in idx) { mx += i % w; my += i / w; mr += px[i].R; mg += px[i].G; mb += px[i].B; }
            int n = idx.Count; mx /= n; my /= n; mr /= n; mg /= n; mb /= n;
            double sxx = 0, sxy = 0, syy = 0, total = 0;
            var sc = new double[3, 2];
            foreach (var i in idx)
            {
                double X = i % w - mx, Y = i / w - my, r = px[i].R - mr, g = px[i].G - mg, b = px[i].B - mb;
                sxx += X * X; sxy += X * Y; syy += Y * Y; total += r * r + g * g + b * b;
                sc[0, 0] += X * r; sc[0, 1] += Y * r; sc[1, 0] += X * g; sc[1, 1] += Y * g; sc[2, 0] += X * b; sc[2, 1] += Y * b;
            }
            var det = sxx * syy - sxy * sxy;
            if (det < 1e-6 || total < 1e-6) continue;
            // per-channel gradient (colour change per px in x and y)
            var G = new double[3, 2];
            for (var ch = 0; ch < 3; ch++)
            {
                G[ch, 0] = (sc[ch, 0] * syy - sc[ch, 1] * sxy) / det;
                G[ch, 1] = (sc[ch, 1] * sxx - sc[ch, 0] * sxy) / det;
            }
            // dominant direction: top eigenvector of G'G
            double a11 = 0, a12 = 0, a22 = 0;
            for (var ch = 0; ch < 3; ch++) { a11 += G[ch, 0] * G[ch, 0]; a12 += G[ch, 0] * G[ch, 1]; a22 += G[ch, 1] * G[ch, 1]; }
            var th = 0.5 * Math.Atan2(2 * a12, a11 - a22);
            double ux = Math.Cos(th), uy = Math.Sin(th);
            var v = new double[3];
            for (var ch = 0; ch < 3; ch++) v[ch] = G[ch, 0] * ux + G[ch, 1] * uy;
            var s = new double[n];
            double resid = 0;
            for (var j = 0; j < n; j++)
            {
                var i = idx[j];
                s[j] = (i % w - mx) * ux + (i / w - my) * uy;
                resid += Sq(px[i].R - mr - v[0] * s[j]) + Sq(px[i].G - mg - v[1] * s[j]) + Sq(px[i].B - mb - v[2] * s[j]);
            }
            var sorted = (double[])s.Clone();
            Array.Sort(sorted);
            double lo = sorted[(int)(0.02 * (n - 1))], hi = sorted[(int)(0.98 * (n - 1))];
            var vlen = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            if (smooth[m] ? vlen * (hi - lo) < 3 : vlen * (hi - lo) < minContrast || 1 - resid / total < 0.5) continue;
            // end colours stay within what the band really shows (2nd..98th percentile per channel): a narrow band's
            // fit must not run off to white or black at its ends
            double[] Pct(Func<int, double> ch) { var a = idx.Select(ch).ToArray(); Array.Sort(a); return new[] { a[(int)(0.02 * (n - 1))], a[(int)(0.98 * (n - 1))] }; }
            var (pr, pg, pb) = (Pct(i => px[i].R), Pct(i => px[i].G), Pct(i => px[i].B));
            (double R, double G, double B) At(double t) => (Math.Clamp(mr + v[0] * t, pr[0], pr[1]), Math.Clamp(mg + v[1] * t, pg[0], pg[1]), Math.Clamp(mb + v[2] * t, pb[0], pb[1]));
            // exact region: nearest to this colour and inside the silhouette
            var f = new double[gw * gh];
            Array.Fill(f, 1.0);
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                f[(y + 1) * gw + x + 1] = Math.Max(own[y * w + x], sf[y * w + x]);
            var (paths, _) = FromField(f, gw, gh, x0, y0, o);
            if (paths.Count == 0) continue;
            result.Add(new GradFit(m, (x0 + mx + 0.5 + ux * lo, y0 + my + 0.5 + uy * lo), (x0 + mx + 0.5 + ux * hi, y0 + my + 0.5 + uy * hi), At(lo), At(hi), paths));
        }
        return result;
    }

    static (double R, double G, double B) Hex(string hex)
    {
        var s = hex.TrimStart('#');
        return (Convert.ToInt32(s[..2], 16), Convert.ToInt32(s.Substring(2, 2), 16), Convert.ToInt32(s.Substring(4, 2), 16));
    }

    static double Dist((double R, double G, double B, double A) c, (double R, double G, double B) t) =>
        Math.Sqrt((c.R - t.R) * (c.R - t.R) + (c.G - t.G) * (c.G - t.G) + (c.B - t.B) * (c.B - t.B));

    static double Nearest((double R, double G, double B, double A) c, List<(double R, double G, double B)>? palette)
    {
        if (palette == null || palette.Count == 0) return double.MaxValue / 4;
        var best = double.MaxValue;
        foreach (var t in palette) best = Math.Min(best, Dist(c, t));
        return best;
    }

    public static (double R, double G, double B) ParseHex(string hex) { var (r, g, b) = Hex(hex); return (r, g, b); }

    // Marching squares over cells between pixel centers. Edge ids: horizontal (x,y)-(x+1,y) = 2*(y*gw+x),
    // vertical (x,y)-(x,y+1) = 2*(y*gw+x)+1. Every boundary edge is used by exactly two segments.
    static List<List<(double X, double Y)>> March(double[] f, int gw, int gh)
    {
        double F(int x, int y) => f[y * gw + x];
        var pointOf = new Dictionary<long, (double X, double Y)>();
        var adj = new Dictionary<long, List<long>>();

        (double, double) Interp(double ax, double ay, double fa, double bx, double by, double fb)
        {
            var t = Math.Abs(fb - fa) < 1e-12 ? 0.5 : fa / (fa - fb);
            t = Math.Clamp(t, 0, 1);
            return (ax + (bx - ax) * t, ay + (by - ay) * t);
        }

        long Edge(int x, int y, int side) => side switch
        {
            0 => 2L * (y * gw + x),           // top
            1 => 2L * (y * gw + x + 1) + 1,   // right
            2 => 2L * ((y + 1) * gw + x),     // bottom
            _ => 2L * (y * gw + x) + 1,       // left
        };

        void Link(int x, int y, int s1, int s2)
        {
            foreach (var side in new[] { s1, s2 })
            {
                var id = Edge(x, y, side);
                if (pointOf.ContainsKey(id)) continue;
                pointOf[id] = side switch
                {
                    0 => Interp(x, y, F(x, y), x + 1, y, F(x + 1, y)),
                    1 => Interp(x + 1, y, F(x + 1, y), x + 1, y + 1, F(x + 1, y + 1)),
                    2 => Interp(x, y + 1, F(x, y + 1), x + 1, y + 1, F(x + 1, y + 1)),
                    _ => Interp(x, y, F(x, y), x, y + 1, F(x, y + 1)),
                };
            }
            long a = Edge(x, y, s1), b = Edge(x, y, s2);
            (adj.TryGetValue(a, out var la) ? la : adj[a] = new List<long>()).Add(b);
            (adj.TryGetValue(b, out var lb) ? lb : adj[b] = new List<long>()).Add(a);
        }

        for (var y = 0; y < gh - 1; y++)
        for (var x = 0; x < gw - 1; x++)
        {
            int tl = F(x, y) < 0 ? 1 : 0, tr = F(x + 1, y) < 0 ? 1 : 0, br = F(x + 1, y + 1) < 0 ? 1 : 0, bl = F(x, y + 1) < 0 ? 1 : 0;
            var c = tl * 8 + tr * 4 + br * 2 + bl;
            switch (c)
            {
                case 0: case 15: break;
                case 1: case 14: Link(x, y, 3, 2); break;
                case 2: case 13: Link(x, y, 2, 1); break;
                case 3: case 12: Link(x, y, 3, 1); break;
                case 4: case 11: Link(x, y, 0, 1); break;
                case 6: case 9: Link(x, y, 0, 2); break;
                case 7: case 8: Link(x, y, 3, 0); break;
                case 5: case 10:
                {
                    // Saddle: resolve with the cell-center average.
                    var center = (F(x, y) + F(x + 1, y) + F(x + 1, y + 1) + F(x, y + 1)) / 4 < 0;
                    if ((c == 5) == center) { Link(x, y, 3, 0); Link(x, y, 2, 1); }
                    else { Link(x, y, 3, 2); Link(x, y, 0, 1); }
                    break;
                }
            }
        }

        var loops = new List<List<(double, double)>>();
        var used = new HashSet<long>();
        foreach (var start in adj.Keys)
        {
            if (used.Contains(start)) continue;
            var loop = new List<(double, double)>();
            long prev = -1, cur = start;
            while (true)
            {
                used.Add(cur);
                loop.Add(pointOf[cur]);
                var next = adj[cur].FirstOrDefault(e => e != prev && !used.Contains(e), -1);
                if (next == -1) break;
                prev = cur;
                cur = next;
            }
            if (loop.Count >= 3) loops.Add(loop);
        }
        return loops;
    }

    static double Area(List<(double X, double Y)> pts)
    {
        double a = 0;
        for (var i = 0; i < pts.Count; i++)
        {
            var (x1, y1) = pts[i];
            var (x2, y2) = pts[(i + 1) % pts.Count];
            a += x1 * y2 - x2 * y1;
        }
        return a / 2;
    }

    // Corner-preserving smoothing of a closed contour. Corners are points where the direction over ±3.5 px of arc
    // turns by more than 50° (greedy non-maximum suppression); a one-pixel jog of noise turns it only about 30°.
    // The runs between corners are smoothed with their ends pinned (SmoothRun), then corners that blur rounded off
    // are squared again (SquareCorners).
    static List<(double X, double Y)> SmoothLoop(List<(double X, double Y)> pts, double sigma)
    {
        var n = pts.Count;
        var seg = new double[n];
        var s = new double[n];
        double total = 0;
        for (var i = 0; i < n; i++)
        {
            s[i] = total;
            seg[i] = Math.Sqrt(Sq(pts[(i + 1) % n].X - pts[i].X) + Sq(pts[(i + 1) % n].Y - pts[i].Y));
            total += seg[i];
        }
        if (n < 8 || total < 12) return pts;
        const double L = 3.5, CornerDeg = 50;

        // the point d px of arc ahead of (d > 0) or behind (d < 0) point i
        (double X, double Y) Walk(int i, double d)
        {
            var j = i;
            var back = d < 0;
            d = Math.Abs(d);
            for (var k = 0; k < n; k++)
            {
                var sg = back ? (j - 1 + n) % n : j;
                var nb = back ? sg : (j + 1) % n;
                if (d <= seg[sg])
                {
                    var t = seg[sg] < 1e-12 ? 0 : d / seg[sg];
                    return (pts[j].X + (pts[nb].X - pts[j].X) * t, pts[j].Y + (pts[nb].Y - pts[j].Y) * t);
                }
                d -= seg[sg];
                j = nb;
            }
            return pts[i];
        }

        var turn = new double[n];
        for (var i = 0; i < n; i++)
        {
            var (a, b, p) = (Walk(i, -L), Walk(i, L), pts[i]);
            double ax = p.X - a.X, ay = p.Y - a.Y, bx = b.X - p.X, by = b.Y - p.Y;
            double la = Math.Sqrt(ax * ax + ay * ay), lb = Math.Sqrt(bx * bx + by * by);
            turn[i] = la * lb < 1e-9 ? 0 : Math.Acos(Math.Clamp((ax * bx + ay * by) / (la * lb), -1, 1)) * 180 / Math.PI;
        }
        var corners = new List<int>();
        foreach (var i in Enumerable.Range(0, n).Where(i => turn[i] > CornerDeg).OrderByDescending(i => turn[i]))
            if (!corners.Any(c => { var dd = Math.Abs(s[c] - s[i]); return Math.Min(dd, total - dd) < L; })) corners.Add(i);
        corners.Sort();

        // a loop without corners is pinned at its sharpest point, so it too is smoothed as one open run
        var pinnedOnly = corners.Count == 0;
        if (pinnedOnly) corners.Add(Enumerable.Range(0, n).OrderByDescending(i => turn[i]).First());
        var result = new List<(double X, double Y)>(n);
        var cornerAt = new List<int>();
        for (var k = 0; k < corners.Count; k++)
        {
            int a = corners[k], b = corners[(k + 1) % corners.Count];
            var len = (b - a + n) % n;
            if (len == 0) len = n;
            var run = new List<(double X, double Y)>(len + 1);
            var u = new double[len + 1];
            for (var t = 0; t <= len; t++)
            {
                run.Add(pts[(a + t) % n]);
                if (t > 0) u[t] = u[t - 1] + seg[(a + t - 1) % n];
            }
            cornerAt.Add(result.Count);
            var sm = SmoothRun(run, u, sigma);
            result.AddRange(sm.Take(sm.Count - 1));
        }
        return pinnedOnly ? result : SquareCorners(result, cornerAt);
    }

    // Gaussian smoothing along the arc of an open run with pinned ends, plus the shrinkage a second pass predicts
    // added back (Taubin-style) so curves keep their size. A first pass at σ shows where the run really bends: a point
    // with no curvature tighter than a 40 px radius within ±4σ gets 2σ (long edges come out straight even under heavy
    // compression noise), one with nothing tighter than 12 px keeps σ, others get σ/2; never more than a tenth of
    // the run, so caps and small marks keep their shape.
    static List<(double X, double Y)> SmoothRun(List<(double X, double Y)> run, double[] u, double sigma)
    {
        var m = run.Count;
        if (m < 5) return run;
        var cap = u[m - 1] / 10;
        var sg = new double[m];
        Array.Fill(sg, Math.Min(sigma, cap));
        var mid = Taubin(run, u, sg);
        // curvature of the σ pass: turn over ±4 px of arc / 8 px
        var kappa = new double[m];
        for (var t = 0; t < m; t++)
        {
            int a = t, b = t;
            while (a > 0 && u[t] - u[a] < 4) a--;
            while (b < m - 1 && u[b] - u[t] < 4) b++;
            if (a == t || b == t) continue;
            double ax = mid[t].X - mid[a].X, ay = mid[t].Y - mid[a].Y, bx = mid[b].X - mid[t].X, by = mid[b].Y - mid[t].Y;
            double la = Math.Sqrt(ax * ax + ay * ay), lb = Math.Sqrt(bx * bx + by * by);
            if (la * lb < 1e-9) continue;
            kappa[t] = Math.Acos(Math.Clamp((ax * bx + ay * by) / (la * lb), -1, 1)) / (u[b] - u[a]);
        }
        for (var t = 0; t < m; t++)
        {
            double k = 0;
            for (var j = t; j >= 0 && u[t] - u[j] <= 4 * sigma; j--) k = Math.Max(k, kappa[j]);
            for (var j = t; j < m && u[j] - u[t] <= 4 * sigma; j++) k = Math.Max(k, kappa[j]);
            sg[t] = Math.Min(k < 1.0 / 40 ? 2 * sigma : k < 1.0 / 12 ? sigma : sigma / 2, cap);
        }
        return Taubin(run, u, sg);
    }

    // Two Gaussian passes along the arc (per-point sigma, ends pinned), with the second pass's extra shrinkage added back.
    static List<(double X, double Y)> Taubin(List<(double X, double Y)> run, double[] u, double[] sg)
    {
        var m = run.Count;
        List<(double X, double Y)> Pass(List<(double X, double Y)> q)
        {
            var r = new List<(double X, double Y)>(m);
            for (var t = 0; t < m; t++)
            {
                var s = sg[t];
                if (t == 0 || t == m - 1 || s < 0.5) { r.Add(q[t]); continue; }
                double sx = q[t].X, sy = q[t].Y, sw = 1;
                for (var j = t - 1; j >= 0 && u[t] - u[j] <= 3 * s; j--) { var w = Math.Exp(-Sq(u[t] - u[j]) / (2 * s * s)); sx += w * q[j].X; sy += w * q[j].Y; sw += w; }
                for (var j = t + 1; j < m && u[j] - u[t] <= 3 * s; j++) { var w = Math.Exp(-Sq(u[j] - u[t]) / (2 * s * s)); sx += w * q[j].X; sy += w * q[j].Y; sw += w; }
                r.Add((sx / sw, sy / sw));
            }
            return r;
        }
        var p1 = Pass(run);
        var p2 = Pass(p1);
        return p1.Select((v, t) => (2 * v.X - p2[t].X, 2 * v.Y - p2[t].Y)).ToList();
    }

    // Least-squares line through q[lo..hi]: RMS distance off it, centroid and unit direction.
    static (double Rms, double Mx, double My, double Dx, double Dy) FitLine(List<(double X, double Y)> q, int lo, int hi)
    {
        var m = hi - lo + 1;
        double mx = 0, my = 0;
        for (var i = lo; i <= hi; i++) { mx += q[i].X; my += q[i].Y; }
        mx /= m; my /= m;
        double sxx = 0, syy = 0, sxy = 0;
        for (var i = lo; i <= hi; i++) { double dx = q[i].X - mx, dy = q[i].Y - my; sxx += dx * dx; syy += dy * dy; sxy += dx * dy; }
        var ang = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
        double ux = Math.Cos(ang), uy = Math.Sin(ang), ss = 0;
        for (var i = lo; i <= hi; i++) ss += Sq((q[i].Y - my) * ux - (q[i].X - mx) * uy);
        return (Math.Sqrt(ss / m), mx, my, ux, uy);
    }

    // Blur rounds sharp corners off. Where both sides of a corner are straight from 4 to 24 px out (RMS under 0.35 px)
    // and their lines cross at more than 30° within 2.5 px of the corner point, the corner moves to the crossing and
    // the rounded points within 4 px of it go. Designed rounded corners (radius over ~6 px) end farther out and stay.
    static List<(double X, double Y)> SquareCorners(List<(double X, double Y)> q, List<int> cornerAt)
    {
        var n = q.Count;
        if (n < 8) return q;
        var s = new double[n + 1];
        for (var i = 1; i <= n; i++) s[i] = s[i - 1] + Math.Sqrt(Sq(q[i % n].X - q[i - 1].X) + Sq(q[i % n].Y - q[i - 1].Y));
        var total = s[n];
        var isCorner = new bool[n];
        foreach (var c in cornerAt) isCorner[c] = true;
        var drop = new bool[n];
        var moved = q.ToList();
        // arc distance from point c to point j walking in direction dir
        double Arc(int c, int j, int dir) => dir > 0 ? (s[j] - s[c] + total) % total : (s[c] - s[j] + total) % total;
        foreach (var c in cornerAt)
        {
            var sides = new List<(double X, double Y)>[2];
            for (var side = 0; side < 2; side++)
            {
                var dir = side == 0 ? -1 : 1;
                sides[side] = new List<(double X, double Y)>();
                for (var k = 1; k < n; k++)
                {
                    var j = ((c + dir * k) % n + n) % n;
                    var d = Arc(c, j, dir);
                    if (isCorner[j] || d > 24) break;
                    if (d >= 4) sides[side].Add(q[j]);
                }
            }
            if (sides[0].Count < 6 || sides[1].Count < 6) continue;
            var (fa, fb) = (FitLine(sides[0], 0, sides[0].Count - 1), FitLine(sides[1], 0, sides[1].Count - 1));
            if (fa.Rms > 0.35 || fb.Rms > 0.35) continue;
            var cross = fa.Dx * fb.Dy - fa.Dy * fb.Dx;
            if (Math.Abs(cross) < 0.5) continue;
            var t = ((fb.Mx - fa.Mx) * fb.Dy - (fb.My - fa.My) * fb.Dx) / cross;
            double ix = fa.Mx + t * fa.Dx, iy = fa.My + t * fa.Dy;
            if (Math.Sqrt(Sq(ix - q[c].X) + Sq(iy - q[c].Y)) > 2.5) continue;
            moved[c] = (ix, iy);
            for (var dir = -1; dir <= 1; dir += 2)
            for (var k = 1; k < n; k++)
            {
                var j = ((c + dir * k) % n + n) % n;
                if (isCorner[j] || Arc(c, j, dir) >= 4) break;
                drop[j] = true;
            }
        }
        return moved.Where((_, i) => !drop[i]).ToList();
    }

    // RDP on a closed loop: split at the two mutually farthest-ish points, simplify both halves.
    static List<(double X, double Y)> Simplify(List<(double X, double Y)> pts, double eps)
    {
        if (pts.Count < 8) return pts;
        var far = 0;
        double best = -1;
        for (var i = 1; i < pts.Count; i++)
        {
            var d = Sq(pts[i].X - pts[0].X) + Sq(pts[i].Y - pts[0].Y);
            if (d > best) { best = d; far = i; }
        }
        var a = Rdp(pts.GetRange(0, far + 1), eps);
        var tail = pts.GetRange(far, pts.Count - far);
        tail.Add(pts[0]);
        var b = Rdp(tail, eps);
        var result = new List<(double, double)>(a);
        result.AddRange(b.Skip(1).Take(b.Count - 2));
        return result;
    }

    static List<(double X, double Y)> Rdp(List<(double X, double Y)> pts, double eps)
    {
        if (pts.Count < 3) return new List<(double, double)>(pts);
        var keep = new bool[pts.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, pts.Count - 1));
        while (stack.Count > 0)
        {
            var (s, e) = stack.Pop();
            double maxD = 0;
            var idx = -1;
            for (var i = s + 1; i < e; i++)
            {
                var d = SegDist(pts[i], pts[s], pts[e]);
                if (d > maxD) { maxD = d; idx = i; }
            }
            if (idx >= 0 && maxD > eps)
            {
                keep[idx] = true;
                stack.Push((s, idx));
                stack.Push((idx, e));
            }
        }
        return pts.Where((_, i) => keep[i]).ToList();
    }

    static double SegDist((double X, double Y) p, (double X, double Y) a, (double X, double Y) b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, len = dx * dx + dy * dy;
        if (len < 1e-12) return Math.Sqrt(Sq(p.X - a.X) + Sq(p.Y - a.Y));
        var t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len, 0, 1);
        return Math.Sqrt(Sq(p.X - (a.X + t * dx)) + Sq(p.Y - (a.Y + t * dy)));
    }

    static double Sq(double v) => v * v;

    static Path Straight(List<(double X, double Y)> pts)
    {
        var zero = pts.Select(_ => (0.0, 0.0)).ToList();
        return new Path(pts, zero, zero.ToList());
    }

    // Catmull-Rom tangents (as Bezier handles), dropped at sharp corners.
    static Path Bezier(List<(double X, double Y)> pts, double cornerDeg)
    {
        var n = pts.Count;
        var ins = new List<(double, double)>();
        var outs = new List<(double, double)>();
        for (var i = 0; i < n; i++)
        {
            var p0 = pts[(i - 1 + n) % n];
            var p1 = pts[i];
            var p2 = pts[(i + 1) % n];
            double ax = p1.X - p0.X, ay = p1.Y - p0.Y, bx = p2.X - p1.X, by = p2.Y - p1.Y;
            double la = Math.Sqrt(ax * ax + ay * ay), lb = Math.Sqrt(bx * bx + by * by);
            var cos = la * lb < 1e-9 ? 1 : (ax * bx + ay * by) / (la * lb);
            var turn = Math.Acos(Math.Clamp(cos, -1, 1)) * 180 / Math.PI;
            if (turn > cornerDeg)
            {
                ins.Add((0, 0));
                outs.Add((0, 0));
                continue;
            }
            // Direction p0->p2, handle lengths a third of each neighbouring segment.
            double tx = p2.X - p0.X, ty = p2.Y - p0.Y, tl = Math.Sqrt(tx * tx + ty * ty);
            if (tl < 1e-9) { ins.Add((0, 0)); outs.Add((0, 0)); continue; }
            tx /= tl; ty /= tl;
            ins.Add((-tx * la / 3, -ty * la / 3));
            outs.Add((tx * lb / 3, ty * lb / 3));
        }
        return new Path(pts, ins, outs);
    }

    // One compound shape per outer loop (with the holes directly inside it), each filled with the core colour of
    // the pixels it covers: the half of its inside pixels farthest from the background, per-channel median.
    // For artwork whose parts differ in colour; a single mean would wash them together.
    // With bandMax, a piece's colour comes from all its pixels closer than bandMax to the background instead:
    // the band that stays visible when a stronger level is stacked on top (soft glows traced as contour levels).
    public static JsonArray ToShapesAuto(List<Path> paths, (double X, double Y) origin, Analysis.Px p, Options o, string bgHex, double bandMax = 0)
    {
        var polys = paths.Select(q => q.Points).ToList();
        var n = polys.Count;
        var depth = new int[n];
        var parent = Enumerable.Repeat(-1, n).ToArray();
        for (var i = 0; i < n; i++)
            for (var j = 0; j < n; j++)
            {
                if (i == j || !Inside(polys[j], polys[i][0])) continue;
                depth[i]++;
                if (parent[i] < 0 || Math.Abs(Area(polys[j])) < Math.Abs(Area(polys[parent[i]]))) parent[i] = j;
            }
        var target = o.Color != null ? Hex(o.Color) : (0, 0, 0);
        var bg = Hex(bgHex);
        var shapes = new JsonArray();
        for (var i = 0; i < n; i++)
        {
            if (depth[i] % 2 == 1) continue;
            var holes = Enumerable.Range(0, n).Where(k => parent[k] == i && depth[k] == depth[i] + 1).ToList();
            double bx0 = polys[i].Min(q => q.X), bx1 = polys[i].Max(q => q.X), by0 = polys[i].Min(q => q.Y), by1 = polys[i].Max(q => q.Y);
            var px = new List<(double R, double G, double B, double D)>();
            for (var y = (int)Math.Floor(by0); y <= (int)Math.Ceiling(by1); y++)
            for (var x = (int)Math.Floor(bx0); x <= (int)Math.Ceiling(bx1); x++)
            {
                if (x < 0 || y < 0 || x >= p.W || y >= p.H) continue;
                var pt = (x + 0.5, y + 0.5);
                if (!Inside(polys[i], pt) || holes.Any(k => Inside(polys[k], pt))) continue;
                var c = p.At(x, y);
                var lum = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
                var inside = o.Mode switch
                {
                    "light" => lum > o.Threshold,
                    "color" => Dist(c, target) < o.Tolerance,
                    "far" => Dist(c, target) > o.Tolerance,
                    "nearest" => Dist(c, target) < Nearest(c, o.Palette),
                    _ => lum < o.Threshold,
                };
                if (inside) px.Add((c.R, c.G, c.B, Dist(c, bg)));
            }
            string fill;
            if (px.Count == 0) fill = "#000000";
            else
            {
                var band = bandMax > 0 ? px.Where(q => q.D < bandMax).ToList() : new();
                var core = band.Count > 0 ? band : px.OrderByDescending(q => q.D).Take(Math.Max(1, px.Count / 2)).ToList();
                double Med(Func<(double R, double G, double B, double D), double> sel) { var v = core.Select(sel).OrderBy(z => z).ToList(); return v[v.Count / 2]; }
                fill = $"#{(int)Med(q => q.R):X2}{(int)Med(q => q.G):X2}{(int)Med(q => q.B):X2}";
            }
            var group = new List<Path> { paths[i] };
            group.AddRange(holes.Select(k => paths[k]));
            shapes.Add(ToShape(group, origin, fill));
        }
        return shapes;
    }

    static bool Inside(List<(double X, double Y)> poly, (double X, double Y) pt)
    {
        var inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var (xi, yi) = poly[i];
            var (xj, yj) = poly[j];
            if ((yi > pt.Y) != (yj > pt.Y) && pt.X < (xj - xi) * (pt.Y - yi) / (yj - yi) + xi) inside = !inside;
        }
        return inside;
    }

    public static JsonObject ToShape(List<Path> paths, (double X, double Y) origin, string fill)
    {
        var arr = new JsonArray();
        foreach (var p in paths)
        {
            arr.Add(new JsonObject
            {
                ["points"] = new JsonArray(p.Points.Select(q => (JsonNode)new JsonArray(Math.Round(q.X - origin.X, 2), Math.Round(q.Y - origin.Y, 2))).ToArray()),
                ["inTangents"] = new JsonArray(p.In.Select(q => (JsonNode)new JsonArray(Math.Round(q.X, 2), Math.Round(q.Y, 2))).ToArray()),
                ["outTangents"] = new JsonArray(p.Out.Select(q => (JsonNode)new JsonArray(Math.Round(q.X, 2), Math.Round(q.Y, 2))).ToArray()),
                ["closed"] = true,
            });
        }
        // FromField orients outlines and holes oppositely, so the default non-zero rule keeps holes
        return new JsonObject { ["type"] = "compound", ["paths"] = arr, ["fill"] = fill };
    }
}
