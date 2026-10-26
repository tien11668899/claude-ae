using System.Text.Json.Nodes;

namespace ClaudeAE;

// Style guides live in <root>/style/*.json and are re-read on every use, so edits apply immediately.
internal sealed class Style(string root)
{
    public JsonObject Load(string? name = null)
    {
        var file = Path.Combine(root, "style", (string.IsNullOrWhiteSpace(name) ? "default" : name) + ".json");
        if (!File.Exists(file)) throw new BridgeException($"No style guide at {file}");
        return JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? new JsonObject();
    }
}

internal static class Instructions
{
    public const string Text = """
        claude-ae drives Adobe After Effects through a bridge panel running inside AE. Every mutating tool call is one undo step
        in AE (the user can Ctrl+Z it) and is all-or-nothing: a call that fails rolls itself back. ae_undo reverts your own last
        step, e.g. when a render shows an idea did not work. The last saved project is backed up before the first change of a session.

        Workflow:
        1. ae_status first. If the bridge is offline, tell the user how to start it instead of retrying.
        2. Read ae_style_guide and follow it unless the user asks otherwise.
        3. Build with high-level tools: ae_create_comp, ae_add_layer, ae_animate (tuned presets), ae_set_keyframes (real easing), ae_effect.
           Use ae_eval only when nothing else fits.
        4. Look at the result before moving on: ae_contact_sheet for layout and rhythm, ae_render_frame for detail,
           ae_motion_curve to check easing. Critique what you see like a senior motion designer, fix it, and look again.
           Two or three build-look-fix rounds are normal; do not declare a piece finished without having looked at it.

        Quality bar:
        - No linear keyframes except type-on, loops and constant drifts. Arrivals use out_expo/out_quint, exits in_expo, moves between
          two rest states in_out or in_out_expo. Use out_back or the spring expression for playful overshoot, sparingly.
        - Stagger related elements by 0.04-0.1 s instead of moving them in unison; lead with the most important element.
        - Keep motion blur on. Keep content inside the style guide's safe margin.
        - Clear hierarchy: one focal point per moment, at most two typefaces, sizes from the type scale, strong contrast.
        - Text must stay still and readable long enough: about 0.3 s per word plus 1 s.
        - Every element that comes in should have an intentional exit or a cut; avoid everything fading out at once.
        - Prefer effect matchNames (locale-independent). If a property path fails, use ae_layer_props to find the right path.
        """;
}
