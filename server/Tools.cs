using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeAE;

internal sealed record Tool(string Name, string Description, JsonObject InputSchema, Func<JsonObject, Task<JsonArray>> Run, bool Mutates);

internal sealed record RenderedFrame(string Path, double Time, int Index);

internal sealed class Tools
{
    static readonly JsonSerializerOptions Out = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly TimeSpan Normal = TimeSpan.FromSeconds(60);

    readonly Bridge _bridge;
    readonly Style _style;
    readonly Dictionary<string, Tool> _tools = new();
    readonly SemaphoreSlim _backupLock = new(1, 1);
    bool _backedUp;

    // One backup per MCP session; the one-shot CLI turns this off so each call does not copy the project.
    public bool Backups { get; init; } = true;

    public Tools(Bridge bridge, Style style)
    {
        _bridge = bridge;
        _style = style;
        Register();
    }

    public JsonArray List() => new(_tools.Values.Select(t => (JsonNode)new JsonObject
    {
        ["name"] = t.Name,
        ["description"] = t.Description,
        ["inputSchema"] = t.InputSchema.DeepClone(),
    }).ToArray());

    public async Task<JsonObject> Call(JsonObject? p)
    {
        var name = p?["name"]?.GetValue<string>() ?? "";
        var args = (p?["arguments"] as JsonObject)?.DeepClone().AsObject() ?? new JsonObject();
        if (!_tools.TryGetValue(name, out var tool)) return Failure($"Unknown tool: {name}");
        try
        {
            var note = tool.Mutates && Backups ? await BackupOnce() : null;
            var content = await tool.Run(args);
            if (note != null) content.Add(Text(note));
            return new JsonObject { ["content"] = content };
        }
        catch (Exception e) when (e is BridgeException or AeException)
        {
            return Failure(e.Message);
        }
        catch (Exception e)
        {
            Program.Log(e.ToString());
            return Failure($"{e.GetType().Name}: {e.Message}");
        }
    }

    void Register()
    {
        Add("ae_status",
            "Check that After Effects and the Claude Bridge panel are running; returns the AE version, project path and comps. Call this first.",
            Obj(), Status);

        Add("ae_project",
            "List every project item (comps, footage, solids, folders) with ids.",
            Obj(), Fwd("project"));

        Add("ae_comp_info",
            "Describe a comp: size, fps, duration, work area and every layer (index, type, in/out, parent, transform values, effects, animated properties with their paths).",
            Obj(("comp", Comp(), false), ("time", Num("Move the comp's current time first (seconds)."), false)),
            Fwd("comp_info"));

        Add("ae_layer_props",
            "Dump a layer's property tree with values, keyframes (time, value, interpolation, ease speed/influence) and expressions. " +
            "Each node has a `path` usable by other tools (matchNames, with 1-based indexes inside effects/masks/animators). " +
            "By default only modified or animated properties are shown; set all=true to see everything.",
            Obj(("comp", Comp(), false), ("layer", Layer(), true),
                ("path", Str("Start below this property path, e.g. \"ADBE Effect Parade/1\"."), false),
                ("depth", Num("Group depth to descend (default 4)."), false),
                ("all", Bool("Include unmodified properties."), false),
                ("time", Num("Time to read values at (default: comp time)."), false)),
            Fwd("layer_props"));

        Add("ae_create_comp",
            "Create a comp (motion blur on by default) and open it in the viewer.",
            Obj(("name", Str("Comp name."), true),
                ("width", Num("Default 1920."), false), ("height", Num("Default 1080."), false),
                ("fps", Num("Default 30."), false), ("duration", Num("Seconds, default 10."), false),
                ("bg", Str("Background color #RRGGBB (viewer only; add a solid for a rendered background)."), false),
                ("open", Bool("Open in viewer (default true)."), false)),
            Fwd("create_comp"), mutates: true);

        Add("ae_add_layer",
            "Add a layer at comp center unless `position` is given. Text layers get an anchor centered on the text; rect/ellipse shapes are " +
            "centered on the layer origin and paths on their bounds. Text defaults (font, size, color) come from the style guide, and a warning " +
            "is returned if After Effects substituted the font. Motion blur is switched on.",
            Obj(("comp", Comp(), false),
                ("kind", Enum("Layer kind.", "text", "shape", "solid", "adjustment", "null", "camera", "light", "footage", "comp"), true),
                ("name", Str("Layer name."), false),
                ("text", Str("Text content (kind=text). Use \\r for line breaks."), false),
                ("font", Str("PostScript font name, e.g. \"Inter-Bold\". See ae_list {what:'fonts'}."), false),
                ("fontSize", Num("Font size in px."), false),
                ("fillColor", Str("Text color #RRGGBB."), false),
                ("tracking", Num("Tracking in 1/1000 em."), false),
                ("leading", Num("Line spacing in px."), false),
                ("justification", Enum("Default center.", "left", "center", "right"), false),
                ("allCaps", Bool("All caps."), false),
                ("box", Arr(Num("px"), "[width, height] for a paragraph text box."), false),
                ("shapes", Arr(ShapeSchema(), "Shapes for kind=shape, each in its own group."), false),
                ("color", Str("Solid/light color #RRGGBB."), false),
                ("width", Num("Solid width (default comp width)."), false),
                ("height", Num("Solid height (default comp height)."), false),
                ("item", Ref("Project item id or name (kind=footage/comp)."), false),
                ("path", Str("File to import (kind=footage)."), false),
                ("sequence", Bool("Import `path` as an image sequence."), false),
                ("zoom", Num("Camera zoom."), false),
                ("lightType", Enum("Light type.", "point", "spot", "ambient", "parallel"), false),
                ("intensity", Num("Light intensity %."), false),
                ("position", Arr(Num("px"), "[x, y] or [x, y, z]."), false),
                ("scale", Any("Percent: a number or [x, y]."), false),
                ("rotation", Num("Degrees."), false),
                ("opacity", Num("0-100."), false),
                ("in", Num("In point (s)."), false), ("out", Num("Out point (s)."), false),
                ("start", Num("Start time (s)."), false),
                ("parent", Ref("Parent layer index or name."), false),
                ("threeD", Bool("Make it a 3D layer."), false),
                ("blend", Enum("Blending mode.", "normal", "add", "screen", "multiply", "overlay", "soft_light", "hard_light", "color_dodge", "lighten", "darken", "difference"), false),
                ("anchorCenter", Bool("true: center the anchor on the content bounds; false: leave it at the layer origin."), false)),
            AddLayer, mutates: true);

        Add("ae_set_props",
            "Set property values on one or more layers. Keys of `props` are paths: aliases (position, scale, rotation, opacity, anchor, " +
            "\"x position\", text), display names or matchNames joined by \"/\", or 1-based indexes. Colors are #RRGGBB. Source text takes a string " +
            "or {text, font, fontSize, fillColor, tracking, leading, justification}. Keyframed properties get a key at `time`.",
            Obj(("comp", Comp(), false), ("layer", Layer(), false), ("layers", Layers(), false),
                ("props", new JsonObject { ["type"] = "object", ["description"] = "{\"<path>\": value}" }, true),
                ("time", Num("Time for keyframed properties (default: comp time)."), false)),
            Fwd("set_props"), mutates: true);

        Add("ae_set_keyframes",
            "Set keyframes on one property with real easing. Each segment uses `ease` from its first key, else the call's `ease` (default in_out). " +
            "Existing keys strictly between the first and last new key are removed so they cannot split the eased segments (keepBetween:true keeps them). " +
            "Eases: linear, hold, ease, in_out, in_out_expo, out, out_quint, out_expo, out_back, in, in_expo, in_back, or a CSS cubic-bezier [x1,y1,x2,y2] " +
            "converted exactly into AE speed/influence.",
            Obj(("comp", Comp(), false), ("layer", Layer(), true),
                ("prop", Str("Property path (see ae_set_props)."), true),
                ("keys", Arr(new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["t"] = Num("Time (s)."),
                        ["v"] = Any("Value."),
                        ["ease"] = Any("Ease of the segment starting at this key."),
                    },
                    ["required"] = new JsonArray("t", "v"),
                }, "Keyframes."), true),
                ("ease", Any("Default ease name or [x1,y1,x2,y2]."), false),
                ("clear", Bool("Remove all of the property's existing keys first."), false),
                ("keepBetween", Bool("Keep existing keys that fall inside the new keys' span."), false)),
            Fwd("set_keyframes"), mutates: true);

        Add("ae_animate",
            "Apply a tuned motion preset (keyframes with proper easing, motion blur on). Presets: fade, slide_up, slide_down, slide_left, slide_right, " +
            "scale, pop (overshoot), blur, wipe, and for text layers type_on, cascade (per-character rise), tracking. Pass several presets to combine, " +
            "e.g. [\"slide_up\",\"blur\"]. With several layers, `stagger` offsets each one (0.04-0.1 s reads as polished).",
            Obj(("comp", Comp(), false), ("layer", Layer(), false), ("layers", Layers(), false),
                ("preset", Any("Preset name or list of names."), true),
                ("direction", Enum("in = arrive, out = leave (default in).", "in", "out"), false),
                ("time", Num("Start time (s, default comp time)."), false),
                ("duration", Num("Seconds (default 0.7 in / 0.45 out; text presets scale with length)."), false),
                ("stagger", Num("Delay between successive layers (s)."), false),
                ("ease", Any("Override the ease (default out_expo in / in_expo out)."), false),
                ("distance", Num("Slide/cascade distance in px."), false),
                ("from", Num("scale/pop: starting scale %; cascade: per-character scale %."), false),
                ("blur", Num("blur/cascade: blur amount."), false),
                ("spread", Num("cascade: width of the character ramp, 1-100 (smaller = more sequential)."), false),
                ("angle", Num("wipe: angle in degrees."), false),
                ("feather", Num("wipe: feather px."), false),
                ("tracking", Num("tracking: starting tracking."), false),
                ("motionBlur", Bool("Default true."), false)),
            Animate, mutates: true);

        Add("ae_effect",
            "Add an effect (by matchName, locale-safe, e.g. \"ADBE Gaussian Blur 2\", \"ADBE Glo2\", \"ADBE Ramp\", \"ADBE Fill\", \"ADBE Drop Shadow\", " +
            "\"ADBE Fractal Noise\", \"ADBE Turbulent Displace\") or edit an existing one by `index`, and set its parameters by name, matchName or 1-based index. " +
            "Layer-picker parameters (map/matte layers) accept a layer name. Returns the effect's parameter list. Gotcha: \"ADBE Gaussian Blur 2\" " +
            "defaults to Repeat Edge Pixels ON in AE 2023+, which clips/smears at layer bounds (e.g. facets a blur-and-threshold metaball); set it false. " +
            "See recipes/ for proven multi-layer looks such as liquid glass.",
            Obj(("comp", Comp(), false), ("layer", Layer(), false), ("layers", Layers(), false),
                ("effect", Str("Effect matchName or display name (to add)."), false),
                ("index", Num("Index of an existing effect to edit instead."), false),
                ("name", Str("Rename the effect."), false),
                ("params", new JsonObject { ["type"] = "object", ["description"] = "{\"<param>\": value}" }, false),
                ("time", Num("Time for keyframed parameters."), false)),
            Fwd("effect"), mutates: true);

        Add("ae_expression",
            "Set or clear (empty string) an expression, or apply a template: spring {freq, damping, min} (physical spring: every keyframe change " +
            "becomes a spring step starting at that key's time, overriding the keys' easing; good for arrivals, but for exits to 0 use keyframes " +
            "with in_back instead, since a spring rebounds after reaching 0; min:0 at least keeps scale/opacity from going negative), wiggle {freq, amp}, loop {type: cycle|pingpong|offset|continue}, time {speed}. Returns any expression error.",
            Obj(("comp", Comp(), false), ("layer", Layer(), false), ("layers", Layers(), false),
                ("prop", Str("Property path."), true),
                ("expression", Str("Expression source."), false),
                ("template", Enum("Template instead of `expression`.", "spring", "wiggle", "loop", "time"), false),
                ("options", new JsonObject { ["type"] = "object", ["description"] = "Template options." }, false)),
            Fwd("expression"), mutates: true);

        Add("ae_edit_layer",
            "Change a layer: name, timing (in/out/start/stretch), parent, 3D, motion blur, blend, solid color, enabled/shy/solo/guide/adjustment/collapse, " +
            "track matte {layer, type: alpha|alpha_inverted|luma|luma_inverted} or null, moveTo index, duplicate, precompose {name}, delete.",
            Obj(("comp", Comp(), false), ("layer", Layer(), true),
                ("name", Str(""), false), ("in", Num(""), false), ("out", Num(""), false), ("start", Num(""), false),
                ("stretch", Num("Time stretch %."), false), ("parent", Ref("Layer, or null to unparent."), false),
                ("threeD", Bool(""), false), ("motionBlur", Bool(""), false),
                ("blend", Str("Blending mode (see ae_add_layer)."), false),
                ("color", Str("Solid color #RRGGBB (solid/adjustment layers)."), false),
                ("enabled", Bool(""), false), ("shy", Bool(""), false), ("solo", Bool(""), false), ("guide", Bool(""), false),
                ("adjustment", Bool(""), false), ("collapse", Bool("Collapse transformations / continuous rasterize."), false),
                ("label", Num("Label color 0-16."), false),
                ("trackMatte", Any("{layer, type} or null."), false),
                ("moveTo", Num("New layer index."), false),
                ("duplicate", Bool(""), false),
                ("precompose", new JsonObject { ["type"] = "object", ["description"] = "{name}" }, false),
                ("delete", Bool(""), false)),
            Fwd("edit_layer"), mutates: true);

        Add("ae_import",
            "Import a file into the project, optionally adding it to a comp as a layer.",
            Obj(("path", Str("Absolute file path."), true), ("comp", Comp(), false),
                ("sequence", Bool("Import as image sequence."), false),
                ("position", Arr(Num("px"), "[x, y]."), false), ("scale", Any("Percent."), false)),
            Fwd("import_file"), mutates: true);

        Add("ae_render_frame",
            "Render one frame and show it to you. Use it constantly: after building or changing anything, look before moving on.",
            Obj(("comp", Comp(), false), ("time", Num("Seconds (default: comp time)."), false),
                ("maxWidth", Num("Downscale to this width (default 1280)."), false),
                ("region", Arr(Num("px"), "Zoom: [x, y, width, height] of the frame in comp pixels, shown magnified up to 4x. Use it to judge edges, type and fine detail."), false)),
            RenderFrame);

        Add("ae_contact_sheet",
            "Render several frames into one labeled grid image: the fastest way to judge layout, rhythm and how a whole animation reads. " +
            "Give `times`, or `count` frames spread over start..end (default 9 over the comp). For a motion check use a short range " +
            "with many frames, e.g. start=1, end=1.6, count=12.",
            Obj(("comp", Comp(), false), ("times", Arr(Num("s"), "Exact times."), false),
                ("count", Num("Frames to sample (default 9, max 48)."), false),
                ("start", Num("Seconds."), false), ("end", Num("Seconds."), false),
                ("columns", Num("Grid columns."), false), ("cellWidth", Num("Cell width px."), false)),
            ContactSheet);

        Add("ae_motion_curve",
            "Plot a property's value and speed over time (sampled every frame, after expressions) and summarize the motion: peak speed, " +
            "whether it starts/stops abruptly, reads linear, or overshoots. Use it to check easing quality.",
            Obj(("comp", Comp(), false), ("layer", Layer(), true), ("prop", Str("Property path, e.g. position."), true),
                ("start", Num("Seconds (default: just before the first key)."), false),
                ("end", Num("Seconds (default: just after the last key)."), false)),
            MotionCurve);

        Add("ae_scenes",
            "Map a comp's timeline into holds and motion (and cut-like jumps) by sampling frames every `step` seconds. Use it first when " +
            "matching a reference video, to get the shot list and timings without looking at every frame.",
            Obj(("comp", Comp(), false), ("start", Num("Seconds (default 0)."), false), ("end", Num("Seconds (default comp end)."), false),
                ("step", Num("Sample spacing in seconds (default 0.25)."), false),
                ("hold", Num("Change below this % counts as a hold (default 0.15)."), false),
                ("jump", Num("Change above this % is reported as a jump/cut (default 6)."), false)),
            Scenes);

        Add("ae_probe",
            "Measure rendered frames. `points` returns exact colors (3x3 average). With `bg` (#RRGGBB) or `bgTime` (a background-only frame, best " +
            "for gradients), also returns the foreground's bbox, center, size, area and mean color, optionally inside `region`. Give several " +
            "times to track an element's position/size over time and copy its timing and easing from data.",
            Obj(("comp", Comp(), false), ("time", Num("One time (s)."), false), ("times", Arr(Num("s"), "Several times."), false),
                ("start", Num("Range start (with end + count or step)."), false), ("end", Num("Range end."), false),
                ("count", Num("Samples in the range."), false), ("step", Num("Or: sample spacing (s)."), false),
                ("points", Arr(Arr(Num("px"), "[x, y]"), "Points to read colors at (comp pixels)."), false),
                ("region", Arr(Num("px"), "[x, y, width, height] to restrict foreground detection."), false),
                ("bg", Str("Background color #RRGGBB."), false),
                ("bgTime", Num("Time of a frame that shows only the background."), false),
                ("bgComp", Ref("Take the bgTime plate from this comp instead (e.g. a background-only comp)."), false),
                ("match", Str("Instead of 'differs from background': foreground = pixels within `tolerance` of this #RRGGBB."), false),
                ("blobs", Num("Also return up to this many separate foreground blobs (connected components), largest first: per-element tracking."), false),
                ("minBlob", Num("Ignore blobs smaller than this many px (default 20)."), false),
                ("open", Num("Before finding blobs, erase lines thinner than about 2x this many px (morphological opening), e.g. 2."), false),
                ("tolerance", Num("RGB distance that still counts as background (default 30)."), false)),
            Probe);

        Add("ae_compare",
            "Render two comps at matching times side by side with a difference image and a numeric difference per row: the way to check a " +
            "recreation against its reference. `offset` shifts comp B's times (B time = A time + offset).",
            Obj(("a", Ref("Comp A (e.g. the reference)."), true), ("b", Ref("Comp B (e.g. the recreation)."), true),
                ("times", Arr(Num("s"), "Times in comp A."), false),
                ("start", Num("Range start (s)."), false), ("end", Num("Range end (s)."), false), ("count", Num("Rows (default 6)."), false),
                ("offset", Num("Added to times for comp B."), false),
                ("diff", Bool("Show the difference image (default true)."), false),
                ("cellWidth", Num("Cell width px (default 480)."), false)),
            Compare);

        Add("ae_trace",
            "Vectorize artwork from a rendered frame (e.g. a reference video) into an editable shape layer in `into`: sub-pixel outlines, " +
            "simplified and smoothed into Beziers, holes kept (oriented loops, non-zero fill). The layer's anchor is the artwork's bbox center and it sits at the " +
            "same comp coordinates, so it lands exactly on the reference. Trace each element at the time it is largest and cleanest, isolated by `region`.",
            Obj(("comp", Comp(), false), ("time", Num("Seconds."), true),
                ("region", Arr(Num("px"), "[x, y, width, height] around the artwork."), true),
                ("image", Str("Trace this PNG (same size as the comp) instead of rendering `comp` at `time`; use frames exported in order, since a single seek into long-GOP video can decode with block errors."), false),
                ("mode", Enum("What counts as artwork (default dark). color = within `tolerance` of `color`; far = at least `tolerance` away from it; " +
                    "nearest = closer to `color` than to every `palette` colour (edges land on the 50% mix, so a posterized palette traces soft art band by band).",
                    "dark", "light", "color", "far", "nearest"), false),
                ("palette", Arr(Str("#RRGGBB"), "For mode=nearest: the competing colours, background included."), false),
                ("colors", Arr(Str("#RRGGBB"), "Trace flat multi-colour art in one call, bottom to top: one group per colour, stacked so shared " +
                    "edges have no seams. `palette` then lists only extra rivals such as background shades; `fill` and `mode` are ignored."), false),
                ("threshold", Num("Luminance 0-255 splitting dark/light (default 128)."), false),
                ("color", Str("Target #RRGGBB for mode=color."), false),
                ("tolerance", Num("RGB distance for mode=color (default 40)."), false),
                ("epsilon", Num("Simplification in px (default 0.7; smaller = more points)."), false),
                ("minArea", Num("Drop loops smaller than this many px^2 (default 12)."), false),
                ("smooth", Bool("Bezier smoothing (default true)."), false),
                ("cornerAngle", Num("Turns sharper than this many degrees stay corners (default 55)."), false),
                ("into", Ref("Comp to create the shape layer in."), true),
                ("name", Str("Layer name."), false),
                ("fill", Str("Fill #RRGGBB (default: the traced pixels' mean color), or \"auto\" to give each separate piece its own core color."), false),
                ("bg", Str("Background #RRGGBB that fill=auto measures color against (default #E8E8E8)."), false),
                ("bgModel", Arr(Num("value"), "[cx, cy, grey at centre, grey at radius, radius]: a radial grey background that also competes per pixel in mode=nearest / colors."), false),
                ("edgeSmooth", Num("Corner-preserving contour smoothing, Gaussian sigma in px of arc: straight, even edges and square corners from compressed references (try 6)."), false),
                ("minHole", Num("Fill holes smaller than this many px² (compression specks inside shapes)."), false),
                ("gradients", Bool("With `colors`: colours that are really linear gradients also get a Gradient Ramp solid matted to their exact region."), false),
                ("gradientMin", Num("Smallest end-to-end colour difference (RGB distance) that counts as a gradient (default 14)."), false),
                ("denoise", Bool("3x3 median before tracing, for compressed video: cleaner, continuous outlines."), false),
                ("bandMax", Num("With fill=auto: colour each piece from its pixels closer than this to `bg` (the visible band when stronger levels are stacked on top)."), false)),
            TraceTool, mutates: true);

        Add("ae_render_video",
            "Render a comp through the render queue (blocks AE until done). Uses an H.264 output template when available.",
            Obj(("comp", Comp(), false), ("path", Str("Output file path."), true),
                ("template", Str("Output module template name (see ae_list {what:'templates'})."), false),
                ("workArea", Bool("Render only the work area."), false)),
            Fwd("render_video", TimeSpan.FromHours(4)));

        Add("ae_list",
            "List installed effects (with matchNames), fonts (PostScript names, AE 24+), output module templates, eases, or presets. Filter with `query`.",
            Obj(("what", Enum("What to list.", "effects", "fonts", "templates", "eases", "presets"), true),
                ("query", Str("Case-insensitive filter."), false)),
            Fwd("list"));

        Add("ae_style_guide",
            "Read the style guide (palette, fonts, type scale, safe margin, motion timing). Follow it unless the user asks otherwise.",
            Obj(("name", Str("Style file in style/ (default \"default\")."), false)),
            a => Task.FromResult(new JsonArray(Text(_style.Load(a["name"]?.GetValue<string>()).ToJsonString(Out)))));

        Add("ae_save",
            "Save the project (to `path` if given, required when never saved).",
            Obj(("path", Str("Save-as path (.aep)."), false)),
            Fwd("save"));

        Add("ae_open_comp",
            "Open a comp in the AE viewer so the user sees it, optionally at a time.",
            Obj(("comp", Comp(), false), ("time", Num("Seconds."), false)),
            Fwd("open_comp"));

        Add("ae_undo",
            "Run Edit > Undo in After Effects. Every mutating claude-ae call is exactly one undo step (and a failed call has already rolled " +
            "itself back), so this reverts your own last changes, e.g. after an experiment looked worse in a render. Use it only right after " +
            "your own changes: if the user edited the project since, it would undo their work instead.",
            Obj(("steps", Num("How many steps (default 1, max 20)."), false)),
            Fwd("undo"));

        Add("ae_reload_bridge",
            "Reload ae/ClaudeBridge.jsx inside After Effects after it was edited (keeps the docked panel). Only needed when changing the bridge itself.",
            Obj(), Fwd("reload"));

        Add("ae_eval",
            "Run ExtendScript (ES3) inside After Effects as one undo step. Use only when no other tool fits. A body containing `return` runs as a " +
            "function, otherwise the last expression's value is returned. Helpers on `CB`: findComp(ref), findLayer(comp, ref), resolveProp(layer, path), " +
            "setValue(prop, value, time), easeSegment(prop, keyA, keyB, ease), toColor(hex), layerSummary(layer, comp).",
            Obj(("script", Str("ExtendScript source."), true)),
            Fwd("eval", TimeSpan.FromMinutes(5)), mutates: true);
    }

    // ------------------------------------------------------------- handlers

    Func<JsonObject, Task<JsonArray>> Fwd(string op, TimeSpan? timeout = null) =>
        async a => new JsonArray(Text(Json(await _bridge.Call(op, a, timeout ?? Normal))));

    async Task<JsonArray> Status(JsonObject a)
    {
        var hb = _bridge.Heartbeat();
        if (!Bridge.IsAlive(hb))
        {
            var why = hb == null
                ? "The bridge has never run on this machine."
                : $"Last heartbeat {Bridge.AgeSeconds(hb):0}s ago{(hb["busy"]?.GetValue<bool>() == true ? $" while running `{hb["op"]}`" : "")}.";
            return new JsonArray(Text(
                $"After Effects bridge: OFFLINE. {why}\n" +
                "Ask the user to open After Effects, then Window > Claude Bridge.jsx (the panel should say Running). " +
                "If it says it cannot write files: Edit > Preferences > Scripting & Expressions > Allow Scripts to Write Files and Access Network.\n" +
                $"Spool folder: {_bridge.Spool}"));
        }
        return new JsonArray(Text(Json(await _bridge.Call("status", new JsonObject(), TimeSpan.FromSeconds(20)))));
    }

    async Task<JsonArray> AddLayer(JsonObject a)
    {
        if (a["kind"]?.GetValue<string>() == "text")
        {
            var st = _style.Load();
            var text = a["text"]?.GetValue<string>() ?? "";
            a["font"] ??= st["fonts"]?[HasCjk(text) ? "cjk" : "display"]?.DeepClone();
            a["fontSize"] ??= st["type"]?["title"]?.DeepClone();
            a["fillColor"] ??= st["palette"]?["fg"]?.DeepClone();
        }
        return new JsonArray(Text(Json(await _bridge.Call("add_layer", a, Normal))));
    }

    async Task<JsonArray> Animate(JsonObject a)
    {
        var dir = a["direction"]?.GetValue<string>() == "out" ? "out" : "in";
        a["ease"] ??= _style.Load()["motion"]?[dir]?["ease"]?.DeepClone();
        return new JsonArray(Text(Json(await _bridge.Call("animate", a, Normal))));
    }

    async Task<JsonArray> RenderFrame(JsonObject a)
    {
        var req = new JsonObject { ["comp"] = a["comp"]?.DeepClone(), ["tag"] = Tag() };
        if (a["time"] is JsonNode t) req["times"] = new JsonArray(t.DeepClone());
        else req["current"] = true;
        var r = await _bridge.Call("render_frames", req, TimeSpan.FromSeconds(120));
        var frames = await Frames(r);
        try
        {
            var region = a["region"] is JsonArray reg && reg.Count == 4
                ? reg.Select(x => (int)Math.Round(x!.GetValue<double>())).ToArray()
                : null;
            var png = Imaging.Fit(frames[0].Path, Int(a, "maxWidth", 1280), region);
            var zoom = region != null ? $", region [{string.Join(", ", region)}]" : "";
            return new JsonArray(Image(png),
                Text($"{r?["comp"]} at {frames[0].Time:0.###}s (frame {frames[0].Index}), comp {r?["width"]}x{r?["height"]}{zoom}"));
        }
        finally
        {
            Cleanup(frames);
        }
    }

    async Task<JsonArray> ContactSheet(JsonObject a)
    {
        var req = new JsonObject { ["comp"] = a["comp"]?.DeepClone(), ["tag"] = Tag() };
        foreach (var k in new[] { "times", "count", "start", "end" })
            if (a[k] != null) req[k] = a[k]!.DeepClone();
        var r = await _bridge.Call("render_frames", req, TimeSpan.FromMinutes(5));
        var frames = await Frames(r);
        try
        {
            var n = frames.Count;
            var cols = Int(a, "columns", n <= 4 ? 2 : n <= 9 ? 3 : 4);
            var cell = Int(a, "cellWidth", 1560 / Math.Min(cols, n));
            var png = Imaging.Sheet(frames, cols, cell, $"{r?["comp"]}   {r?["width"]}x{r?["height"]}");
            var times = string.Join(", ", frames.Select((f, i) => $"#{i + 1} {f.Time:0.00}s"));
            return new JsonArray(Image(png), Text($"{r?["comp"]}: {times}"));
        }
        finally
        {
            Cleanup(frames);
        }
    }

    async Task<JsonArray> MotionCurve(JsonObject a)
    {
        var r = await _bridge.Call("sample_motion", a, Normal) as JsonObject
                ?? throw new BridgeException("No samples returned");
        var times = r["times"]!.AsArray().Select(x => x!.GetValue<double>()).ToArray();
        var values = r["values"]!.AsArray().Select(x => x is JsonArray arr
            ? arr.Select(y => y!.GetValue<double>()).ToArray()
            : new[] { x!.GetValue<double>() }).ToArray();
        var keys = r["keys"]!.AsArray().Select(x => x!.GetValue<double>()).ToArray();
        var title = $"{r["layer"]} / {r["prop"]}";
        var fps = r["fps"]?.GetValue<double>() ?? 0;
        var png = Imaging.MotionChart(times, values, keys, title, out var summary, fps);
        return new JsonArray(Image(png), Text(summary));
    }

    // Times from `time`, `times`, or a range with `count` or `step`.
    static List<double> TimesFrom(JsonObject a, int defaultCount)
    {
        if (a["times"] is JsonArray ts && ts.Count > 0) return ts.Select(x => x!.GetValue<double>()).ToList();
        if (a["time"] is JsonValue tv && tv.TryGetValue<double>(out var one)) return new List<double> { one };
        var start = a["start"]?.GetValue<double>() ?? 0;
        var end = a["end"]?.GetValue<double>() ?? start;
        if (a["step"] is JsonValue sv && sv.TryGetValue<double>(out var step) && step > 0)
        {
            var list = new List<double>();
            for (var t = start; t <= end + 1e-9 && list.Count < 2000; t += step) list.Add(Math.Round(t, 4));
            return list;
        }
        var n = Math.Max(1, Int(a, "count", defaultCount));
        return Enumerable.Range(0, n).Select(i => n == 1 ? start : start + (end - start) * i / (n - 1)).ToList();
    }

    async Task<List<RenderedFrame>> RenderAt(JsonNode? comp, IEnumerable<double> times, TimeSpan timeout)
    {
        var req = new JsonObject { ["comp"] = comp?.DeepClone(), ["tag"] = Tag(), ["times"] = new JsonArray(times.Select(t => (JsonNode)t).ToArray()) };
        return await Frames(await _bridge.Call("render_frames", req, timeout));
    }

    async Task<JsonArray> Scenes(JsonObject a)
    {
        var info = await _bridge.Call("comp_info", new JsonObject { ["comp"] = a["comp"]?.DeepClone() }, Normal);
        var dur = info?["duration"]?.GetValue<double>() ?? 0;
        var step = a["step"]?.GetValue<double>() ?? 0.25;
        var start = a["start"]?.GetValue<double>() ?? 0;
        var end = Math.Min(a["end"]?.GetValue<double>() ?? dur, dur - 1.0 / 60);
        var times = new List<double>();
        for (var t = start; t <= end + 1e-9 && times.Count < 2000; t += step) times.Add(Math.Round(t, 4));
        var frames = await RenderAt(a["comp"], times, TimeSpan.FromMinutes(20));
        try
        {
            return new JsonArray(Text(Analysis.Scenes(frames, a["hold"]?.GetValue<double>() ?? 0.15, a["jump"]?.GetValue<double>() ?? 6)));
        }
        finally
        {
            Cleanup(frames);
        }
    }

    async Task<JsonArray> Probe(JsonObject a)
    {
        var times = TimesFrom(a, 1);
        var frames = await RenderAt(a["comp"], times, TimeSpan.FromMinutes(10));
        List<RenderedFrame>? plate = null;
        try
        {
            Analysis.Px? platePx = null;
            if (a["bgTime"] is JsonValue bt && bt.TryGetValue<double>(out var bgTime))
            {
                plate = await RenderAt(a["bgComp"] ?? a["comp"], new[] { bgTime }, Normal);
                platePx = Analysis.Pixels(plate[0].Path);
            }
            int[][]? points = (a["points"] as JsonArray)?.Select(p => p!.AsArray().Select(v => (int)Math.Round(v!.GetValue<double>())).ToArray()).ToArray();
            int[]? region = (a["region"] as JsonArray)?.Select(v => (int)Math.Round(v!.GetValue<double>())).ToArray();
            var tol = a["tolerance"]?.GetValue<double>() ?? 30;
            var rows = new JsonArray();
            foreach (var f in frames)
                rows.Add(Analysis.Probe(f, points, region, a["bg"]?.GetValue<string>(), platePx, tol, a["match"]?.GetValue<string>(),
                    Int(a, "blobs", 0), Int(a, "minBlob", 20), Int(a, "open", 0)));
            // One line per sample keeps long tracks readable.
            return new JsonArray(Text(string.Join("\n", rows.Select(r => r!.ToJsonString(Out)))));
        }
        finally
        {
            Cleanup(frames);
            if (plate != null) Cleanup(plate);
        }
    }

    async Task<JsonArray> Compare(JsonObject a)
    {
        var times = TimesFrom(a, 6);
        var offset = a["offset"]?.GetValue<double>() ?? 0;
        var fa = await RenderAt(a["a"], times, TimeSpan.FromMinutes(10));
        var fb = await RenderAt(a["b"], times.Select(t => t + offset), TimeSpan.FromMinutes(10));
        try
        {
            var png = Analysis.Compare(fa, fb, $"A: {a["a"]}", $"B: {a["b"]}", a["diff"]?.GetValue<bool>() != false,
                Int(a, "cellWidth", 480), out var diffs);
            var lines = string.Join(", ", times.Select((t, i) => $"{t:0.00}s {diffs[i]:0.0}%"));
            return new JsonArray(Image(png), Text($"Mean difference per row: {lines}. Average {diffs.Average():0.0}%."));
        }
        finally
        {
            Cleanup(fa);
            Cleanup(fb);
        }
    }

    async Task<JsonArray> TraceTool(JsonObject a)
    {
        var region = (a["region"] as JsonArray)?.Select(v => (int)Math.Round(v!.GetValue<double>())).ToArray();
        if (region is not { Length: 4 }) throw new BridgeException("`region` must be [x, y, width, height]");
        // `image`: trace a frame already on disk. Seeking a long-GOP video to a single frame can make After Effects
        // decode it with block errors; frames exported in order are clean.
        var image = a["image"]?.GetValue<string>();
        if (image != null && !File.Exists(image)) throw new BridgeException($"No such image: {image}");
        var frames = image != null ? new List<RenderedFrame>() : await RenderAt(a["comp"], new[] { a["time"]!.GetValue<double>() }, Normal);
        try
        {
            var px = Analysis.Pixels(image ?? frames[0].Path);
            if (a["denoise"]?.GetValue<bool>() == true) px = Analysis.Median3(px);
            var opts = new Trace.Options(
                a["mode"]?.GetValue<string>() ?? "dark",
                a["threshold"]?.GetValue<double>() ?? 128,
                a["color"]?.GetValue<string>(),
                a["tolerance"]?.GetValue<double>() ?? 40,
                a["epsilon"]?.GetValue<double>() ?? 0.7,
                a["minArea"]?.GetValue<double>() ?? 12,
                a["smooth"]?.GetValue<bool>() ?? true,
                a["cornerAngle"]?.GetValue<double>() ?? 55,
                (a["palette"] as JsonArray)?.Select(x => Trace.ParseHex(x!.GetValue<string>())).ToList());
            if (a["edgeSmooth"]?.GetValue<double>() is double es && es > 0) opts = opts with { EdgeSmooth = es };
            if (a["minHole"]?.GetValue<double>() is double mh && mh > 0) opts = opts with { MinHole = mh };
            if (a["bgModel"] is JsonArray bm && bm.Count == 5)
                opts = opts with { BgModel = (bm[0]!.GetValue<double>(), bm[1]!.GetValue<double>(), bm[2]!.GetValue<double>(), bm[3]!.GetValue<double>(), bm[4]!.GetValue<double>()) };
            if (a["colors"] is JsonArray colorList && colorList.Count > 0)
                return await TraceStack(a, px, region, opts, colorList.Select(x => x!.GetValue<string>()).ToList());
            var (paths, mean, bbox) = Trace.Run(px, region, opts);
            if (paths.Count == 0) throw new BridgeException("Nothing traced in that region; check mode/threshold.");
            var origin = ((bbox[0] + bbox[2]) / 2.0, (bbox[1] + bbox[3]) / 2.0);
            var fill = a["fill"]?.GetValue<string>() ?? mean;
            var shapes = fill == "auto"
                ? Trace.ToShapesAuto(paths, origin, px, opts, a["bg"]?.GetValue<string>() ?? "#E8E8E8", a["bandMax"]?.GetValue<double>() ?? 0)
                : new JsonArray(Trace.ToShape(paths, origin, fill));
            var req = new JsonObject
            {
                ["comp"] = a["into"]!.DeepClone(),
                ["kind"] = "shape",
                ["name"] = a["name"]?.GetValue<string>() ?? "Traced",
                ["shapes"] = shapes,
                ["position"] = new JsonArray(Math.Round(origin.Item1, 2), Math.Round(origin.Item2, 2)),
                ["anchorCenter"] = false,
            };
            var r = await _bridge.Call("add_layer", req, TimeSpan.FromMinutes(2));
            var points = paths.Sum(p => p.Points.Count);
            return new JsonArray(Text($"Traced {paths.Count} loops / {points} points, bbox [{string.Join(", ", bbox)}], mean color {mean}.\n{Json(r)}"));
        }
        finally
        {
            Cleanup(frames);
        }
    }

    // Flat multi-colour artwork without seams or fringes, as two layers. The colour layer stacks colour i (bottom to top)
    // as everything nearer to colours i..n-1 than to the colours below it, so each colour also covers the ones above it
    // and inner edges are anti-aliased once. The background takes no part there; instead a silhouette layer (all
    // colours against the `palette` extras, i.e. background shades) is the colour layer's alpha matte, so the outer
    // edge is anti-aliased once too, rather than every stacked colour fringing at the same edge.
    async Task<JsonArray> TraceStack(JsonObject a, Analysis.Px px, int[] region, Trace.Options opts, List<string> colors)
    {
        var rgb = colors.Select(Trace.ParseHex).ToList();
        var extras = opts.Palette ?? new();
        var name = a["name"]?.GetValue<string>() ?? "Traced";
        var (outline, _, obox) = Trace.Run(px, region, opts with { Mode = "nearest", Targets = rgb, Palette = extras });
        if (outline.Count == 0) throw new BridgeException("Nothing traced in that region; check the colours.");
        // colour layers only where the silhouette is, plus a margin for the anti-aliasing
        var inner = new[] { obox[0] - 4, obox[1] - 4, obox[2] - obox[0] + 8, obox[3] - obox[1] + 8 };
        var traced = Trace.StackLayers(px, inner, opts, rgb, a["extend"]?.GetValue<double>() ?? 2.0)
            .Select(t => (Hex: colors[t.Index], t.Paths)).ToList();
        if (traced.Count == 0) throw new BridgeException("Nothing traced in that region; check the colours.");
        var origin = ((obox[0] + obox[2]) / 2.0, (obox[1] + obox[3]) / 2.0);
        var mOrigin = ((obox[0] + obox[2]) / 2.0, (obox[1] + obox[3]) / 2.0);
        JsonObject Layer(string n, JsonArray shapes, (double X, double Y) at) => new()
        {
            ["comp"] = a["into"]!.DeepClone(),
            ["kind"] = "shape",
            ["name"] = n,
            ["shapes"] = shapes,
            ["position"] = new JsonArray(Math.Round(at.X, 2), Math.Round(at.Y, 2)),
            ["anchorCenter"] = false,
        };
        // add_layer puts the first group in front, so the top of the stack goes first.
        var stack = new JsonArray(traced.AsEnumerable().Reverse().Select(t => (JsonNode)Trace.ToShape(t.Paths, origin, t.Hex)).ToArray());
        await _bridge.Call("add_layer", Layer(name + " M", new JsonArray(Trace.ToShape(outline, mOrigin, "#FFFFFF")), mOrigin), TimeSpan.FromMinutes(2));
        await _bridge.Call("add_layer", Layer(name, stack, origin), TimeSpan.FromMinutes(2));
        await _bridge.Call("edit_layer", new JsonObject
        {
            ["comp"] = a["into"]!.DeepClone(),
            ["layer"] = name,
            ["trackMatte"] = new JsonObject { ["layer"] = name + " M", ["type"] = "alpha" },
        }, TimeSpan.FromMinutes(1));
        // colours that are really linear gradients get a Gradient Ramp solid over them, matted by their exact region
        var grads = new List<Trace.GradFit>();
        if (a["gradients"]?.GetValue<bool>() == true)
        {
            grads = Trace.Gradients(px, inner, opts, opts with { Mode = "nearest", Targets = rgb, Palette = extras }, rgb, a["gradientMin"]?.GetValue<double>() ?? 14);
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            string N(double v) => Math.Round(v, 3).ToString(ic);
            string Col((double R, double G, double B) c) => $"[{N(c.R / 255)}, {N(c.G / 255)}, {N(c.B / 255)}, 1]";
            foreach (var g in grads)
            {
                var gname = $"{name} g{g.Index}";
                await _bridge.Call("add_layer", Layer(gname + " M", new JsonArray(Trace.ToShape(g.Paths, origin, "#FFFFFF")), origin), TimeSpan.FromMinutes(2));
                var script =
                    $"var c = CB.findComp({a["into"]!.ToJsonString()}); var L = c.layers.addSolid([1, 1, 1], {JsonValue.Create(gname)!.ToJsonString()}, c.width, c.height, 1, c.duration); " +
                    "L.motionBlur = false; var fx = L.property('ADBE Effect Parade').addProperty('ADBE Ramp'); " +
                    $"fx.property('ADBE Ramp-0001').setValue([{N(g.P0.X)}, {N(g.P0.Y)}]); fx.property('ADBE Ramp-0002').setValue({Col(g.C0)}); " +
                    $"fx.property('ADBE Ramp-0003').setValue([{N(g.P1.X)}, {N(g.P1.Y)}]); fx.property('ADBE Ramp-0004').setValue({Col(g.C1)}); " +
                    "fx.property('ADBE Ramp-0005').setValue(1); return 'ok';";
                await _bridge.Call("eval", new JsonObject { ["script"] = script }, TimeSpan.FromMinutes(1));
                await _bridge.Call("edit_layer", new JsonObject
                {
                    ["comp"] = a["into"]!.DeepClone(),
                    ["layer"] = gname,
                    ["trackMatte"] = new JsonObject { ["layer"] = gname + " M", ["type"] = "alpha" },
                }, TimeSpan.FromMinutes(1));
            }
        }
        var points = traced.Sum(t => t.Paths.Sum(p => p.Points.Count));
        var gtext = grads.Count == 0 ? "" : $" Gradients on {string.Join(", ", grads.Select(g => colors[g.Index]))}.";
        return new JsonArray(Text($"Traced {traced.Count} colours / {points} points plus a {outline.Count}-loop silhouette matte (\"{name} M\"), bbox [{obox[0]}, {obox[1]}, {obox[2]}, {obox[3]}].{gtext}"));
    }

    async Task<List<RenderedFrame>> Frames(JsonNode? r)
    {
        var list = r?["frames"]?.AsArray().Select(f => new RenderedFrame(
            f!["path"]!.GetValue<string>(), f["t"]!.GetValue<double>(), f["frame"]!.GetValue<int>())).ToList() ?? new();
        if (list.Count == 0) throw new BridgeException("After Effects rendered no frames");
        foreach (var f in list) await Bridge.WaitForPng(f.Path, TimeSpan.FromSeconds(90));
        return list;
    }

    static void Cleanup(IEnumerable<RenderedFrame> frames)
    {
        foreach (var f in frames) Bridge.TryDelete(f.Path);
    }

    async Task<string?> BackupOnce()
    {
        if (_backedUp) return null;
        await _backupLock.WaitAsync();
        try
        {
            if (_backedUp) return null;
            var hb = _bridge.Heartbeat();
            if (!Bridge.IsAlive(hb)) return null; // the call itself will report the bridge state
            _backedUp = true;
            var proj = hb!["project"]?.GetValue<string>();
            if (string.IsNullOrEmpty(proj) || !File.Exists(proj))
                return "Note: this project has never been saved, so no backup was made. Suggest ae_save before big changes.";
            var dir = Path.Combine(Path.GetDirectoryName(proj)!, "claude-backups");
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(proj)}.{DateTime.Now:yyyyMMdd-HHmmss}.aep");
            File.Copy(proj, dest);
            return $"Backed up the last saved project to {dest} before the first change of this session.";
        }
        finally
        {
            _backupLock.Release();
        }
    }

    // --------------------------------------------------------------- helpers

    void Add(string name, string description, JsonObject schema, Func<JsonObject, Task<JsonArray>> run, bool mutates = false) =>
        _tools[name] = new Tool(name, description, schema, run, mutates);

    static string Tag() => $"f{DateTime.Now:HHmmssfff}{Random.Shared.Next(1000):D3}";

    static int Int(JsonObject a, string key, int fallback) =>
        a[key] is JsonValue v && v.TryGetValue<double>(out var d) ? (int)Math.Round(d) : fallback;

    // CJK symbols/ideographs, Hangul and full-width forms.
    static bool HasCjk(string s) =>
        s.Any(c => (c >= 0x3000 && c <= 0x9FFF) || (c >= 0xAC00 && c <= 0xD7AF) || (c >= 0xFF00 && c <= 0xFFEF));

    static string Json(JsonNode? n) => n?.ToJsonString(Out) ?? "null";

    public static JsonObject Text(string s) => new() { ["type"] = "text", ["text"] = s };

    static JsonObject Image(byte[] png) => new() { ["type"] = "image", ["data"] = Convert.ToBase64String(png), ["mimeType"] = "image/png" };

    static JsonObject Failure(string message) => new()
    {
        ["content"] = new JsonArray(Text(message)),
        ["isError"] = true,
    };

    static JsonObject Obj(params (string Name, JsonObject Schema, bool Required)[] props)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, schema, req) in props)
        {
            properties[name] = schema;
            if (req) required.Add(name);
        }
        var o = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0) o["required"] = required;
        return o;
    }

    static JsonObject Typed(string type, string description)
    {
        var o = new JsonObject { ["type"] = type };
        if (description.Length > 0) o["description"] = description;
        return o;
    }

    static JsonObject Str(string d) => Typed("string", d);
    static JsonObject Num(string d) => Typed("number", d);
    static JsonObject Bool(string d) => Typed("boolean", d);
    static JsonObject Any(string d) => new() { ["description"] = d };
    static JsonObject Ref(string d) => new() { ["type"] = new JsonArray("integer", "string", "null"), ["description"] = d };
    static JsonObject Comp() => Ref("Comp name or project item id. Omit for the active comp.");
    static JsonObject Layer() => Ref("Layer index (1 = top, preferred) or exact name.");
    static JsonObject Layers() => Any("Several layers: list of indexes/names. Omit both layer and layers to use the selected layers.");

    static JsonObject Arr(JsonObject items, string d) => new() { ["type"] = "array", ["items"] = items, ["description"] = d };

    static JsonObject Enum(string d, params string[] values) => new()
    {
        ["type"] = "string",
        ["enum"] = new JsonArray(values.Select(v => (JsonNode)v).ToArray()),
        ["description"] = d,
    };

    static JsonObject ShapeSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["type"] = Enum("Default rect.", "rect", "ellipse", "path"),
            ["name"] = Str("Group name."),
            ["size"] = Arr(Num("px"), "[w, h] for rect/ellipse."),
            ["roundness"] = Num("Rect corner radius px."),
            ["points"] = Arr(Arr(Num("px"), "[x, y]"), "Path vertices, relative to the layer."),
            ["inTangents"] = Arr(Arr(Num("px"), "[x, y]"), "Path in tangents."),
            ["outTangents"] = Arr(Arr(Num("px"), "[x, y]"), "Path out tangents."),
            ["closed"] = Bool("Path closed (default true)."),
            ["fill"] = Str("Fill #RRGGBB (default white when no stroke)."),
            ["stroke"] = Str("Stroke #RRGGBB."),
            ["strokeWidth"] = Num("Default 4."),
            ["lineCap"] = Enum("Stroke cap.", "butt", "round", "square"),
            ["trim"] = new JsonObject { ["type"] = "object", ["description"] = "Trim paths {start, end, offset} in %/degrees." },
            ["offset"] = Arr(Num("px"), "Group position [x, y]."),
        },
    };
}
