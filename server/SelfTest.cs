using System.Text.Json.Nodes;

namespace ClaudeAE;

// `claude-ae --selftest`: checks imaging, the spool protocol (against a fake AE)
// and the tool table without After Effects. Images land in .spool/selftest.
internal static class SelfTest
{
    public static async Task<int> Run(string root)
    {
        var failures = 0;
        void Check(bool ok, string what)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
            if (!ok) failures++;
        }

        var outDir = Path.Combine(root, ".spool", "selftest");
        Directory.CreateDirectory(outDir);

        var frames = new List<RenderedFrame>();
        for (var i = 0; i < 6; i++)
        {
            var p = Path.Combine(outDir, $"synthetic_{i}.png");
            File.WriteAllBytes(p, Imaging.Synthetic(1920, 1080, i / 5.0));
            frames.Add(new RenderedFrame(p, i * 0.5, i * 15));
        }
        var sheet = Imaging.Sheet(frames, 3, 512, "selftest  1920x1080");
        File.WriteAllBytes(Path.Combine(outDir, "contact_sheet.png"), sheet);
        Check(PngSize(sheet) == (1568, 30 + 2 * (288 + 22 + 8) + 8), $"contact sheet is 3x2 cells ({PngSize(sheet)})");
        var fit = Imaging.Fit(frames[0].Path, 640);
        Check(PngSize(fit) == (640, 360), $"frame downscales to 640x360 ({PngSize(fit)})");

        var (t, expo) = Curve(x => 1 - Math.Pow(2, -10 * x));
        Imaging.MotionChart(t, expo, [0.5, 1.5], "out_expo x", out var sExpo);
        File.WriteAllBytes(Path.Combine(outDir, "curve_out_expo.png"), Imaging.MotionChart(t, expo, [0.5, 1.5], "out_expo x", out _));
        Console.WriteLine("      " + sExpo.Replace("\n", "\n      "));
        Check(!sExpo.Contains("abruptly") && !sExpo.Contains("linear"), "eased move is not flagged");

        var (_, linear) = Curve(x => x);
        File.WriteAllBytes(Path.Combine(outDir, "curve_linear.png"), Imaging.MotionChart(t, linear, [0.5, 1.5], "linear x", out var sLin));
        Console.WriteLine("      " + sLin.Replace("\n", "\n      "));
        Check(sLin.Contains("abruptly") && sLin.Contains("linear"), "linear move is flagged as abrupt and linear");

        var (_, back) = Curve(x => 1 + 2.70158 * Math.Pow(x - 1, 3) + 1.70158 * Math.Pow(x - 1, 2));
        Imaging.MotionChart(t, back, [0.5, 1.5], "out_back x", out var sBack);
        Check(sBack.Contains("overshoots"), "overshoot is detected");

        // A thick ring traces to two loops (outer edge + hole) at the right radii.
        var ringPath = Path.Combine(outDir, "ring.png");
        File.WriteAllBytes(ringPath, Imaging.SyntheticRing(400, 300, 200, 150, 100, 30));
        var (loops, _, bbox) = Trace.Run(Analysis.Pixels(ringPath), new[] { 0, 0, 400, 300 },
            new Trace.Options("dark", 128, null, 40, 0.7, 12, true, 55));
        Check(loops.Count == 2, $"ring traces to 2 loops ({loops.Count})");
        Check(Math.Abs(bbox[0] - 85) <= 2 && Math.Abs(bbox[2] - 315) <= 2, $"traced outer edge at radius ~115 (bbox {string.Join(",", bbox)})");

        var fakeRoot = Path.Combine(outDir, "fake");
        Directory.CreateDirectory(fakeRoot);
        var bridge = new Bridge(fakeRoot);
        Bridge.TryDelete(Path.Combine(bridge.Spool, "heartbeat.json"));
        try
        {
            await bridge.Call("status", new JsonObject(), TimeSpan.FromSeconds(2));
            Check(false, "offline bridge is reported");
        }
        catch (BridgeException e)
        {
            Check(e.Message.Contains("never run"), "offline bridge is reported");
        }

        using (var cts = new CancellationTokenSource())
        {
            var fake = Task.Run(() => FakeAe(bridge.Spool, cts.Token));
            await Task.Delay(300);
            var r = await bridge.Call("status", new JsonObject { ["text"] = "動態 \"quoted\"\n" }, TimeSpan.FromSeconds(5));
            Check(r?["echo"]?["text"]?.GetValue<string>() == "動態 \"quoted\"\n", "round trip keeps non-ASCII and escapes");
            try
            {
                await bridge.Call("boom", new JsonObject(), TimeSpan.FromSeconds(5));
                Check(false, "AE errors surface");
            }
            catch (AeException e)
            {
                Check(e.Message.Contains("kaboom"), "AE errors surface");
            }
            var calls = Enumerable.Range(0, 20).Select(i => bridge.Call("status", new JsonObject { ["i"] = i }, TimeSpan.FromSeconds(10))).ToList();
            var results = await Task.WhenAll(calls);
            Check(results.Select((x, i) => x?["echo"]?["i"]?.GetValue<int>() == i).All(ok => ok), "20 concurrent calls each get their own answer");
            cts.Cancel();
            await fake;
            Bridge.TryDelete(Path.Combine(bridge.Spool, "heartbeat.json"));
        }

        var tools = new Tools(bridge, new Style(root));
        var list = tools.List();
        Check(list.Count == 28, $"{list.Count} tools registered");
        Check(list.All(x => x!["inputSchema"]?["type"]?.GetValue<string>() == "object"), "every tool has an object schema");
        var style = await tools.Call(new JsonObject { ["name"] = "ae_style_guide", ["arguments"] = new JsonObject() });
        Check(style["isError"] == null && style.ToJsonString().Contains("palette"), "style guide loads");
        var offline = await tools.Call(new JsonObject { ["name"] = "ae_add_layer", ["arguments"] = new JsonObject { ["kind"] = "text" } });
        Check(offline["isError"]?.GetValue<bool>() == true, "tool calls fail cleanly while AE is offline");

        Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
        Console.WriteLine($"Images: {outDir}");
        return failures == 0 ? 0 : 1;
    }

    static (double[] T, double[][] V) Curve(Func<double, double> ease)
    {
        // 0.5 s hold, 1 s move from x=100 to x=900, 0.5 s hold, sampled at 30 fps.
        var t = Enumerable.Range(0, 61).Select(i => i / 30.0).ToArray();
        var v = t.Select(tt =>
        {
            var p = Math.Clamp(tt - 0.5, 0, 1);
            return new[] { 100 + 800 * ease(p), 540.0 };
        }).ToArray();
        return (t, v);
    }

    static (int, int) PngSize(byte[] png) =>
        ((png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19], (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23]);

    // Stands in for ClaudeBridge.jsx: heartbeat, echo every op, fail `boom`.
    static async Task FakeAe(string spool, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            File.WriteAllText(Path.Combine(spool, "heartbeat.json"), $"{{\"time\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},\"busy\":false}}");
            foreach (var f in Directory.GetFiles(spool, "*.cmd.json").Order())
            {
                var cmd = JsonNode.Parse(File.ReadAllText(f))!.AsObject();
                File.Delete(f);
                var id = cmd["id"]!.GetValue<string>();
                var res = cmd["op"]!.GetValue<string>() == "boom"
                    ? new JsonObject { ["id"] = id, ["ok"] = false, ["error"] = "kaboom" }
                    : new JsonObject { ["id"] = id, ["ok"] = true, ["result"] = new JsonObject { ["echo"] = cmd["args"]!.DeepClone() } };
                var final = Path.Combine(spool, id + ".res.json");
                File.WriteAllText(final + ".tmp", res.ToJsonString());
                File.Move(final + ".tmp", final);
            }
            await Task.Delay(40);
        }
    }
}
