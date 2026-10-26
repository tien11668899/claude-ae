using System.Text;
using System.Text.Json.Nodes;

namespace ClaudeAE;

// `claude-ae --call <tool> [@args.json]`: runs one tool outside MCP, for testing and
// debugging. Arguments come from the @file (UTF-8) or stdin, default {}. Images are
// saved under .spool/cli and their paths printed.
internal static class Cli
{
    public static async Task<int> Call(string root, string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: claude-ae --call <tool> [@args.json]   (or JSON on stdin)");
            return 2;
        }
        // UTF-8 both ways, whatever the console code page is.
        Console.OutputEncoding = new UTF8Encoding(false);
        var json = "{}";
        if (args.Length > 1 && args[1].StartsWith('@')) json = File.ReadAllText(args[1][1..]);
        else if (Console.IsInputRedirected) json = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)).ReadToEnd();
        if (string.IsNullOrWhiteSpace(json)) json = "{}";

        var bridge = new Bridge(root);
        var tools = new Tools(bridge, new Style(root)) { Backups = false };
        var result = await tools.Call(new JsonObject { ["name"] = args[0], ["arguments"] = JsonNode.Parse(json) });

        var outDir = Path.Combine(bridge.Spool, "cli");
        Directory.CreateDirectory(outDir);
        var n = 0;
        foreach (var item in result["content"]!.AsArray())
        {
            if (item!["type"]!.GetValue<string>() == "image")
            {
                var path = Path.Combine(outDir, $"{DateTime.Now:HHmmss-fff}-{args[0]}-{n++}.png");
                File.WriteAllBytes(path, Convert.FromBase64String(item["data"]!.GetValue<string>()));
                Console.WriteLine($"[image] {path}");
            }
            else
            {
                Console.WriteLine(item["text"]!.GetValue<string>());
            }
        }
        var failed = result["isError"]?.GetValue<bool>() == true;
        if (failed) Console.WriteLine("[isError]");
        return failed ? 1 : 0;
    }
}
