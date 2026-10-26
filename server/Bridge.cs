using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace ClaudeAE;

internal sealed class BridgeException(string message) : Exception(message);

internal sealed class AeException(string op, string message) : Exception($"After Effects error in `{op}`: {message}");

// File spool shared with ae/ClaudeBridge.jsx. A command is written as
// <id>.cmd.json (via rename, so AE never sees half a file); AE answers with
// <id>.res.json and refreshes heartbeat.json about once a second.
internal sealed class Bridge
{
    public string Root { get; }
    public string Spool { get; }
    long _seq;

    public Bridge(string root)
    {
        Root = root;
        Spool = Path.Combine(root, ".spool");
        Directory.CreateDirectory(Path.Combine(Spool, "frames"));
    }

    public JsonObject? Heartbeat()
    {
        var path = Path.Combine(Spool, "heartbeat.json");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            }
            catch (FileNotFoundException)
            {
                // AE replaces the file by rename; retry once it is back.
            }
            catch (Exception) when (attempt < 2)
            {
            }
            catch (Exception)
            {
                return null;
            }
            Thread.Sleep(20);
        }
        return null;
    }

    public static double AgeSeconds(JsonObject? hb) =>
        hb?["time"] is JsonNode t
            ? (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - t.GetValue<double>()) / 1000.0
            : double.PositiveInfinity;

    public static bool IsAlive(JsonObject? hb) => AgeSeconds(hb) < 6;

    public async Task<JsonNode?> Call(string op, JsonObject args, TimeSpan timeout)
    {
        var hb = Heartbeat();
        var age = AgeSeconds(hb);
        var busy = hb?["busy"]?.GetValue<bool>() == true;
        // AE pauses scheduled scripts while it is busy or the user drags panels; ride out short gaps.
        if (age > 6 && age < 120 && !busy)
        {
            var wait = Stopwatch.StartNew();
            while (wait.Elapsed < TimeSpan.FromSeconds(20) && age > 6)
            {
                await Task.Delay(250);
                hb = Heartbeat();
                age = AgeSeconds(hb);
                busy = hb?["busy"]?.GetValue<bool>() == true;
            }
        }
        if (age > 6 && !(busy && age < 3600))
        {
            throw new BridgeException(hb == null
                ? "The After Effects bridge has never run. Ask the user to open After Effects and Window > Claude Bridge.jsx (see the claude-ae README)."
                : $"The After Effects bridge is not responding (last heartbeat {age:0}s ago). Ask the user to check that After Effects is open, the Claude Bridge panel says Running, and no dialog is open.");
        }

        var id = $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Interlocked.Increment(ref _seq):D5}";
        var cmdPath = Path.Combine(Spool, id + ".cmd.json");
        var resPath = Path.Combine(Spool, id + ".res.json");
        var cmd = new JsonObject { ["id"] = id, ["op"] = op, ["args"] = args.DeepClone() };
        // The default encoder escapes all non-ASCII, so ExtendScript reads it regardless of code page.
        await File.WriteAllTextAsync(cmdPath + ".tmp", cmd.ToJsonString(), new UTF8Encoding(false));
        File.Move(cmdPath + ".tmp", cmdPath);

        var sw = Stopwatch.StartNew();
        while (!File.Exists(resPath))
        {
            if (sw.Elapsed > timeout)
            {
                if (TryDelete(cmdPath))
                    throw new BridgeException($"After Effects never picked up `{op}` within {timeout.TotalSeconds:0}s, so it was cancelled. Is the Claude Bridge panel running and AE free of dialogs?");
                throw new BridgeException($"After Effects is still running `{op}` after {timeout.TotalSeconds:0}s; its result will be discarded.");
            }
            await Task.Delay(30);
        }

        var text = await ReadWithRetry(resPath);
        TryDelete(resPath);
        var res = JsonNode.Parse(text) as JsonObject ?? throw new BridgeException("Malformed response from After Effects");
        if (res["ok"]?.GetValue<bool>() != true)
            throw new AeException(op, res["error"]?.GetValue<string>() ?? "unknown error");
        return res["result"];
    }

    // saveFrameToPng may finish writing after the command returns, so wait for a complete PNG.
    public static async Task WaitForPng(string path, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        var tail = new byte[12];
        while (true)
        {
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 64)
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    fs.Seek(-12, SeekOrigin.End);
                    fs.ReadExactly(tail);
                    if (tail[4] == 'I' && tail[5] == 'E' && tail[6] == 'N' && tail[7] == 'D') return;
                }
            }
            catch (IOException)
            {
            }
            if (sw.Elapsed > timeout) throw new BridgeException($"After Effects did not finish writing {path}");
            await Task.Delay(50);
        }
    }

    public void CleanStale()
    {
        var cutoff = DateTime.Now.AddMinutes(-10);
        foreach (var dir in new[] { Spool, Path.Combine(Spool, "frames") })
        {
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                if (Path.GetFileName(f) == "heartbeat.json") continue;
                if (File.GetLastWriteTime(f) < cutoff) TryDelete(f);
            }
        }
    }

    static async Task<string> ReadWithRetry(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await File.ReadAllTextAsync(path);
            }
            catch (IOException) when (attempt < 20)
            {
                await Task.Delay(25);
            }
        }
    }

    public static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
