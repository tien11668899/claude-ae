using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeAE;

// MCP over stdio: one JSON-RPC message per line. Only the tools capability is
// implemented, which is all Claude Code needs from this server.
internal static class Program
{
    public const string Version = "0.1.0";

    static readonly object WriteLock = new();
    static Stream _stdout = Stream.Null;

    static async Task<int> Main(string[] args)
    {
        var root = Paths.FindRoot();
        if (args.Length > 0 && args[0] == "--selftest") return await SelfTest.Run(root);
        if (args.Length > 0 && args[0] == "--call") return await Cli.Call(root, args[1..]);

        var bridge = new Bridge(root);
        bridge.CleanStale();
        var tools = new Tools(bridge, new Style(root));

        _stdout = Console.OpenStandardOutput();
        using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var pending = new List<Task>();
        string? line;
        while ((line = await stdin.ReadLineAsync()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonObject? msg;
            try
            {
                msg = JsonNode.Parse(line) as JsonObject;
            }
            catch (JsonException)
            {
                Send(Error(null, -32700, "Parse error"));
                continue;
            }
            if (msg == null) continue;
            pending.RemoveAll(t => t.IsCompleted);
            pending.Add(Task.Run(() => Handle(msg, tools)));
        }
        // stdin closed: finish what is in flight so no reply is lost.
        await Task.WhenAll(pending);
        return 0;
    }

    static async Task Handle(JsonObject msg, Tools tools)
    {
        var id = msg["id"]?.DeepClone();
        var method = msg["method"]?.GetValue<string>();
        if (method == null) return; // a response to something we never send
        try
        {
            JsonNode? result = method switch
            {
                "initialize" => Initialize(msg["params"] as JsonObject),
                "ping" => new JsonObject(),
                "tools/list" => new JsonObject { ["tools"] = tools.List() },
                "tools/call" => await tools.Call(msg["params"] as JsonObject),
                _ => null,
            };
            if (id == null) return; // notification
            if (result == null)
            {
                Send(Error(id, -32601, $"Method not found: {method}"));
                return;
            }
            Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });
        }
        catch (Exception e)
        {
            Log(e.ToString());
            if (id != null) Send(Error(id, -32603, e.Message));
        }
    }

    static JsonObject Initialize(JsonObject? p)
    {
        var requested = p?["protocolVersion"]?.GetValue<string>() ?? "2025-06-18";
        return new JsonObject
        {
            ["protocolVersion"] = requested,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = "claude-ae", ["version"] = Version },
            ["instructions"] = Instructions.Text,
        };
    }

    static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    static void Send(JsonObject msg)
    {
        var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString() + "\n");
        lock (WriteLock)
        {
            _stdout.Write(bytes);
            _stdout.Flush();
        }
    }

    public static void Log(string s) => Console.Error.WriteLine($"[claude-ae] {s}");
}

internal static class Paths
{
    // The repo root holds ae/ClaudeBridge.jsx; both sides derive .spool from it.
    public static string FindRoot()
    {
        var env = Environment.GetEnvironmentVariable("CLAUDE_AE_ROOT");
        if (!string.IsNullOrEmpty(env)) return Path.GetFullPath(env);
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ae", "ClaudeBridge.jsx"))) return dir.FullName;
        }
        throw new InvalidOperationException("Cannot find the claude-ae folder (ae/ClaudeBridge.jsx). Set CLAUDE_AE_ROOT.");
    }
}
