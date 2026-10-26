// Claude Bridge - the After Effects side of claude-ae.
//
// Polls <root>/.spool for <id>.cmd.json files written by the claude-ae MCP server,
// runs each command (one undo group per mutating command) and writes <id>.res.json.
//
// ExtendScript is ES3: no let/const/arrows/JSON/Array#indexOf, and reserved words
// such as `in`, `delete` and `import` cannot be used as dotted property names.
// Keep this file ASCII-only: ExtendScript may read it in the system code page.

(function (host) {
    var G = $.global;
    var VERSION = "0.1.0";
    var TICK_MS = 80;
    var BEAT_MS = 1000;

    G.__ClaudeBridgeHost = undefined;

    var root = G.__ClaudeBridgeRoot ? new Folder(G.__ClaudeBridgeRoot) : new File($.fileName).parent.parent;
    var spool = new Folder(root.fsName + "/.spool");
    var framesDir = new Folder(spool.fsName + "/frames");

    // Published as $.global.ClaudeBridge only at the end of this file (see the bottom).
    var CB = {
        version: VERSION,
        root: root,
        spool: spool,
        taskId: null,
        busy: false,
        lastBeat: 0,
        handled: 0,
        lines: [],
        ui: null
    };
    var OPS = {};

    // ------------------------------------------------------------------ JSON

    // Everything outside printable ASCII is escaped, so files are code-page proof.
    function quote(s) {
        s = String(s);
        var out = '"';
        for (var i = 0; i < s.length; i++) {
            var c = s.charAt(i), n = s.charCodeAt(i);
            if (c === '"') out += '\\"';
            else if (c === "\\") out += "\\\\";
            else if (n < 32 || n > 126) out += "\\u" + ("0000" + n.toString(16)).slice(-4);
            else out += c;
        }
        return out + '"';
    }

    function ser(v, depth) {
        if (v === null || v === undefined) return "null";
        var t = typeof v;
        if (t === "number") return isFinite(v) ? String(roundNum(v)) : "null";
        if (t === "boolean") return v ? "true" : "false";
        if (t === "string") return quote(v);
        if (t === "function") return "null";
        if (depth > 40) return '"[too deep]"';
        var parts = [], i;
        if (v instanceof Array) {
            for (i = 0; i < v.length; i++) parts.push(ser(v[i], depth + 1));
            return "[" + parts.join(",") + "]";
        }
        // Host objects (layers, comps, ...) are not walkable; describe them instead.
        if (v.constructor !== Object) return quote(String(v));
        for (var k in v) {
            if (!v.hasOwnProperty(k)) continue;
            var x = v[k];
            if (x === undefined || typeof x === "function") continue;
            parts.push(quote(k) + ":" + ser(x, depth + 1));
        }
        return "{" + parts.join(",") + "}";
    }

    function roundNum(n) {
        return Math.abs(n) < 1e9 ? Math.round(n * 10000) / 10000 : n;
    }

    // Commands come from our own MCP server, which can already run arbitrary
    // script through the eval op, so eval-based parsing adds no exposure.
    function parse(s) {
        return eval("(" + s + ")");
    }

    // ----------------------------------------------------------------- utils

    function has(o, k) { return o !== null && o !== undefined && o[k] !== undefined && o[k] !== null; }
    function isArr(v) { return v instanceof Array; }
    function num(v, d) { return (typeof v === "number" && !isNaN(v)) ? v : d; }
    function clamp(v, lo, hi) { return v < lo ? lo : (v > hi ? hi : v); }
    // Our own validation errors; their line numbers point into this file, not the caller's input.
    function fail(msg) {
        var e = new Error(msg);
        e.bridge = true;
        throw e;
    }
    function now() { return new Date().getTime(); }

    function r4(v) {
        if (typeof v === "number") return roundNum(v);
        if (isArr(v)) {
            var o = [];
            for (var i = 0; i < v.length; i++) o.push(r4(v[i]));
            return o;
        }
        return v;
    }

    function keysOf(o) {
        var a = [];
        for (var k in o) if (o.hasOwnProperty(k)) a.push(k);
        return a;
    }

    function readText(f) {
        f.encoding = "UTF-8";
        if (!f.open("r")) fail("Cannot read " + f.fsName);
        var s = f.read();
        f.close();
        return s;
    }

    function writeText(path, s) {
        var tmp = new File(path + ".tmp");
        tmp.encoding = "UTF-8";
        tmp.lineFeed = "Unix";
        if (!tmp.open("w")) {
            fail("Cannot write " + tmp.fsName + ". Enable Preferences > Scripting & Expressions > " +
                "'Allow Scripts to Write Files and Access Network'.");
        }
        tmp.write(s);
        tmp.close();
        var dest = new File(path);
        if (dest.exists) dest.remove();
        if (!tmp.rename(dest.name)) fail("Cannot rename " + tmp.fsName);
    }

    function errText(e) {
        var s = (e && e.message) ? e.message : String(e);
        if (e && e.line && !e.bridge) s += " (line " + e.line + ")";
        return s;
    }

    // --------------------------------------------------------------- lookups

    function findItem(ref) {
        var it = null;
        if (typeof ref === "number") {
            try { it = app.project.itemByID(ref); } catch (e) {}
            if (!it) fail("No project item with id " + ref);
            return it;
        }
        for (var i = 1; i <= app.project.numItems; i++) {
            if (app.project.item(i).name === ref) return app.project.item(i);
        }
        fail("No project item named \"" + ref + "\"");
    }

    function findComp(ref) {
        if (ref === undefined || ref === null || ref === "") {
            var a = app.project.activeItem;
            if (a instanceof CompItem) return a;
            fail("No active comp. Pass `comp` (name or id).");
        }
        var it = findItem(ref);
        if (!(it instanceof CompItem)) fail("\"" + ref + "\" is not a comp");
        return it;
    }

    function findLayer(comp, ref) {
        if (typeof ref === "number") {
            if (ref < 1 || ref > comp.numLayers) {
                fail("Layer index " + ref + " is out of range 1.." + comp.numLayers + " in \"" + comp.name + "\"");
            }
            return comp.layer(ref);
        }
        if (typeof ref === "string") {
            var l = null;
            try { l = comp.layer(ref); } catch (e) {}
            if (l) return l;
            fail("No layer named \"" + ref + "\" in \"" + comp.name + "\"");
        }
        fail("Pass `layer` as an index (1 = top) or a name");
    }

    // A list of refs, one ref, or nothing (= the selected layers).
    function findLayers(comp, refs) {
        var out = [], i;
        if (refs === undefined || refs === null) {
            var sel = comp.selectedLayers;
            for (i = 0; i < sel.length; i++) out.push(sel[i]);
            if (!out.length) fail("No layers given and none selected in \"" + comp.name + "\"");
            return out;
        }
        if (!isArr(refs)) refs = [refs];
        for (i = 0; i < refs.length; i++) out.push(findLayer(comp, refs[i]));
        return out;
    }

    var PROP_ALIASES = {
        "anchor": "ADBE Transform Group/ADBE Anchor Point",
        "anchor point": "ADBE Transform Group/ADBE Anchor Point",
        "position": "ADBE Transform Group/ADBE Position",
        "x position": "ADBE Transform Group/ADBE Position_0",
        "y position": "ADBE Transform Group/ADBE Position_1",
        "z position": "ADBE Transform Group/ADBE Position_2",
        "scale": "ADBE Transform Group/ADBE Scale",
        "rotation": "ADBE Transform Group/ADBE Rotate Z",
        "z rotation": "ADBE Transform Group/ADBE Rotate Z",
        "x rotation": "ADBE Transform Group/ADBE Rotate X",
        "y rotation": "ADBE Transform Group/ADBE Rotate Y",
        "orientation": "ADBE Transform Group/ADBE Orientation",
        "opacity": "ADBE Transform Group/ADBE Opacity",
        "text": "ADBE Text Properties/ADBE Text Document",
        "source text": "ADBE Text Properties/ADBE Text Document"
    };

    var SEG_ALIASES = {
        "transform": "ADBE Transform Group",
        "effects": "ADBE Effect Parade",
        "masks": "ADBE Mask Parade",
        "contents": "ADBE Root Vectors Group",
        "text": "ADBE Text Properties",
        "animators": "ADBE Text Animators",
        "material options": "ADBE Material Options Group"
    };

    // Path segments are separated by "/" and may be display names, matchNames or
    // 1-based indexes (needed inside indexed groups such as effects and masks).
    function resolveProp(layer, path) {
        if (!path && path !== 0) fail("Missing property path");
        path = String(path);
        if (PROP_ALIASES.hasOwnProperty(path.toLowerCase())) path = PROP_ALIASES[path.toLowerCase()];
        var segs = path.split("/");
        var cur = layer;
        for (var i = 0; i < segs.length; i++) {
            var s = segs[i], next = null;
            if (/^\d+$/.test(s)) {
                if (parseInt(s, 10) <= cur.numProperties) next = cur.property(parseInt(s, 10));
            } else {
                try { next = cur.property(s); } catch (e1) {}
                if (!next && SEG_ALIASES.hasOwnProperty(s.toLowerCase())) {
                    try { next = cur.property(SEG_ALIASES[s.toLowerCase()]); } catch (e2) {}
                }
            }
            if (!next) {
                fail("Property \"" + s + "\" not found in path \"" + path + "\" (under \"" + cur.name +
                    "\"). Use ae_layer_props to see valid paths.");
            }
            cur = next;
        }
        return cur;
    }

    function tprop(layer, match) {
        return layer.property("ADBE Transform Group").property(match);
    }

    // ---------------------------------------------------------------- values

    function hexToRgb(s) {
        var m = /^#?([0-9a-f]{6})([0-9a-f]{2})?$/i.exec(s);
        if (!m) fail("Bad color \"" + s + "\" (use #RRGGBB)");
        var n = parseInt(m[1], 16);
        return [((n >> 16) & 255) / 255, ((n >> 8) & 255) / 255, (n & 255) / 255];
    }

    function toColor(v) {
        if (typeof v === "string") {
            var rgba = hexToRgb(v);
            rgba.push(1);
            return rgba;
        }
        if (isArr(v)) {
            var big = false, out = [], i;
            for (i = 0; i < v.length; i++) if (v[i] > 1) big = true;
            for (i = 0; i < v.length && i < 4; i++) out.push(big ? v[i] / 255 : v[i]);
            if (out.length === 3) out.push(1);
            return out;
        }
        fail("Bad color " + v);
    }

    function rgbHex(c) {
        var s = "#";
        for (var i = 0; i < 3; i++) s += ("0" + Math.round(clamp(c[i], 0, 1) * 255).toString(16)).slice(-2);
        return s;
    }

    var JUSTIFY = {};
    var BLEND = {};
    var LIGHTS = {};
    var MATTES = {};
    function initEnums() {
        if (typeof ParagraphJustification === "undefined") return;
        JUSTIFY = {
            left: ParagraphJustification.LEFT_JUSTIFY,
            center: ParagraphJustification.CENTER_JUSTIFY,
            right: ParagraphJustification.RIGHT_JUSTIFY
        };
        BLEND = {
            normal: BlendingMode.NORMAL, add: BlendingMode.ADD, screen: BlendingMode.SCREEN,
            multiply: BlendingMode.MULTIPLY, overlay: BlendingMode.OVERLAY,
            soft_light: BlendingMode.SOFT_LIGHT, hard_light: BlendingMode.HARD_LIGHT,
            color_dodge: BlendingMode.COLOR_DODGE, lighten: BlendingMode.LIGHTEN,
            darken: BlendingMode.DARKEN, difference: BlendingMode.DIFFERENCE
        };
        LIGHTS = { point: LightType.POINT, spot: LightType.SPOT, ambient: LightType.AMBIENT, parallel: LightType.PARALLEL };
        MATTES = {
            alpha: TrackMatteType.ALPHA, alpha_inverted: TrackMatteType.ALPHA_INVERTED,
            luma: TrackMatteType.LUMA, luma_inverted: TrackMatteType.LUMA_INVERTED
        };
    }
    initEnums();

    function enumName(table, v) {
        for (var k in table) if (table.hasOwnProperty(k) && table[k] === v) return k;
        return String(v);
    }

    function justName(j) { return enumName(JUSTIFY, j); }

    function applyTextDoc(doc, v) {
        if (typeof v === "string") v = { text: v };
        if (has(v, "text")) doc.text = v.text;
        if (has(v, "font")) doc.font = v.font;
        if (has(v, "fontSize")) doc.fontSize = v.fontSize;
        if (has(v, "fillColor")) { doc.applyFill = true; doc.fillColor = toColor(v.fillColor).slice(0, 3); }
        if (has(v, "strokeColor")) { doc.applyStroke = true; doc.strokeColor = toColor(v.strokeColor).slice(0, 3); }
        if (has(v, "strokeWidth")) doc.strokeWidth = v.strokeWidth;
        if (has(v, "tracking")) doc.tracking = v.tracking;
        if (has(v, "leading")) { doc.autoLeading = false; doc.leading = v.leading; }
        if (has(v, "justification")) {
            if (!JUSTIFY.hasOwnProperty(v.justification)) fail("justification must be left, center or right");
            doc.justification = JUSTIFY[v.justification];
        }
        if (has(v, "allCaps")) doc.allCaps = !!v.allCaps;
        return doc;
    }

    function textDocOut(d) {
        var o = {}, ks = ["text", "font", "fontSize", "tracking", "leading", "strokeWidth", "allCaps"];
        for (var i = 0; i < ks.length; i++) { try { o[ks[i]] = r4(d[ks[i]]); } catch (e) {} }
        try { if (d.applyFill) o.fillColor = rgbHex(d.fillColor); } catch (e1) {}
        try { if (d.applyStroke) o.strokeColor = rgbHex(d.strokeColor); } catch (e2) {}
        try { o.justification = justName(d.justification); } catch (e3) {}
        return o;
    }

    function toShape(v) {
        if (!v || !isArr(v.vertices || v.points)) fail("Path needs `points` (or `vertices`): [[x,y], ...]");
        var verts = v.vertices || v.points, zeros = [], i;
        for (i = 0; i < verts.length; i++) zeros.push([0, 0]);
        var s = new Shape();
        s.vertices = verts;
        s.inTangents = v.inTangents || zeros;
        s.outTangents = v.outTangents || zeros;
        s.closed = v.closed !== false;
        return s;
    }

    function toAE(p, v, base) {
        var t = p.propertyValueType;
        if (t === PropertyValueType.COLOR) return toColor(v);
        if (t === PropertyValueType.TEXT_DOCUMENT) return applyTextDoc(base, v);
        if (t === PropertyValueType.SHAPE) return toShape(v);
        // Layer pickers (e.g. an effect's map layer) also accept a layer name.
        if (t === PropertyValueType.LAYER_INDEX && typeof v === "string") {
            return findLayer(p.propertyGroup(p.propertyDepth).containingComp, v).index;
        }
        if (typeof v === "boolean") return v ? 1 : 0;
        return v;
    }

    function valueOut(p, v) {
        var t = p.propertyValueType;
        if (t === PropertyValueType.NO_VALUE || t === PropertyValueType.CUSTOM_VALUE) return undefined;
        if (t === PropertyValueType.TEXT_DOCUMENT) return textDocOut(v);
        if (t === PropertyValueType.COLOR) return rgbHex(v);
        if (t === PropertyValueType.SHAPE) {
            return { points: r4(v.vertices), inTangents: r4(v.inTangents), outTangents: r4(v.outTangents), closed: v.closed };
        }
        if (t === PropertyValueType.MARKER) return { comment: v.comment, duration: r4(v.duration) };
        return r4(v);
    }

    function setValue(p, v, time) {
        if (p.propertyType !== PropertyType.PROPERTY) fail("\"" + p.name + "\" is a group, not a property");
        var keyed = p.numKeys > 0;
        var base = keyed ? p.valueAtTime(time, true) : p.value;
        var val = toAE(p, v, base);
        if (keyed) p.setValueAtTime(time, val); else p.setValue(val);
    }

    // ---------------------------------------------------------------- easing

    // Named eases are CSS-style cubic-beziers, converted exactly into AE's
    // speed/influence keyframe handles, so "out_expo" here looks like
    // easeOutExpo on the web instead of AE's mushy default Easy Ease.
    var EASES = {
        "linear": "linear",
        "hold": "hold",
        "ease": [0.25, 0.1, 0.25, 1],
        "in_out": [0.65, 0, 0.35, 1],
        "in_out_expo": [0.87, 0, 0.13, 1],
        "out": [0.25, 1, 0.5, 1],
        "out_quint": [0.22, 1, 0.36, 1],
        "out_expo": [0.16, 1, 0.3, 1],
        "out_back": [0.34, 1.56, 0.64, 1],
        "in": [0.5, 0, 0.75, 0],
        "in_expo": [0.7, 0, 0.84, 0],
        "in_back": [0.36, 0, 0.66, -0.56]
    };

    function easeSpec(e) {
        if (isArr(e)) {
            if (e.length !== 4) fail("A custom ease is [x1, y1, x2, y2]");
            return e;
        }
        if (typeof e === "string" && EASES.hasOwnProperty(e)) return EASES[e];
        fail("Unknown ease \"" + e + "\". Use one of " + keysOf(EASES).join(", ") + " or [x1,y1,x2,y2].");
    }

    // One segment's cubic-bezier -> AE influence (%) and per-dimension speeds.
    // AE's out handle of key A sits at (influence * dt, speed * influence * dt)
    // from the key, so speed = y1 * delta / (x1 * dt); likewise for key B's in handle.
    function bezierToAE(spec, deltas, dt, spatial) {
        var x1 = spec[0], y1 = spec[1], x2 = spec[2], y2 = spec[3];
        var r = { outInf: clamp(x1 * 100, 0.1, 100), inInf: clamp((1 - x2) * 100, 0.1, 100), outSpeed: [], inSpeed: [] };
        for (var d = 0; d < deltas.length; d++) {
            var so = (x1 > 0.001 && dt > 0) ? y1 * deltas[d] / (x1 * dt) : 0;
            var si = (x2 < 0.999 && dt > 0) ? (1 - y2) * deltas[d] / ((1 - x2) * dt) : 0;
            if (spatial) { so = Math.max(0, so); si = Math.max(0, si); }
            r.outSpeed.push(so);
            r.inSpeed.push(si);
        }
        return r;
    }

    function easeDims(p) {
        var t = p.propertyValueType;
        if (t === PropertyValueType.TwoD) return 2;
        if (t === PropertyValueType.ThreeD) return 3;
        return 1;
    }

    function valueDeltas(p, a, b, dims) {
        var out = [], i;
        if (typeof a === "number") {
            out.push(b - a);
        } else if (isArr(a) && p.isSpatial) {
            var s = 0;
            for (i = 0; i < a.length; i++) s += (b[i] - a[i]) * (b[i] - a[i]);
            out.push(Math.sqrt(s));
        } else if (isArr(a) && p.propertyValueType !== PropertyValueType.COLOR) {
            for (i = 0; i < a.length; i++) out.push(b[i] - a[i]);
        }
        while (out.length < dims) out.push(0);
        return out.slice(0, dims);
    }

    function easeSegment(p, ka, kb, ease) {
        var KIT = KeyframeInterpolationType;
        var spec = easeSpec(ease);
        if (spec === "hold") {
            p.setInterpolationTypeAtKey(ka, p.keyInInterpolationType(ka), KIT.HOLD);
            return;
        }
        if (spec === "linear") {
            p.setInterpolationTypeAtKey(ka, p.keyInInterpolationType(ka), KIT.LINEAR);
            p.setInterpolationTypeAtKey(kb, KIT.LINEAR, p.keyOutInterpolationType(kb));
            return;
        }
        var dims = easeDims(p);
        var dt = p.keyTime(kb) - p.keyTime(ka);
        var e = bezierToAE(spec, valueDeltas(p, p.keyValue(ka), p.keyValue(kb), dims), dt, p.isSpatial);
        var outE = [], inE = [];
        for (var d = 0; d < dims; d++) {
            outE.push(new KeyframeEase(e.outSpeed[d], e.outInf));
            inE.push(new KeyframeEase(e.inSpeed[d], e.inInf));
        }
        p.setInterpolationTypeAtKey(ka, p.keyInInterpolationType(ka), KIT.BEZIER);
        p.setInterpolationTypeAtKey(kb, KIT.BEZIER, p.keyOutInterpolationType(kb));
        try {
            p.setTemporalContinuousAtKey(ka, false);
            p.setTemporalContinuousAtKey(kb, false);
            p.setTemporalAutoBezierAtKey(ka, false);
            p.setTemporalAutoBezierAtKey(kb, false);
        } catch (e1) {}
        p.setTemporalEaseAtKey(ka, p.keyInTemporalEase(ka), outE);
        p.setTemporalEaseAtKey(kb, inE, p.keyOutTemporalEase(kb));
    }

    function interpName(t) {
        var KIT = KeyframeInterpolationType;
        return t === KIT.LINEAR ? "linear" : (t === KIT.HOLD ? "hold" : "bezier");
    }

    function easesOut(arr) {
        var o = [];
        for (var i = 0; i < arr.length; i++) o.push([r4(arr[i].speed), r4(arr[i].influence)]);
        return o;
    }

    function keysOut(p, limit) {
        var ks = [];
        for (var k = 1; k <= p.numKeys && k <= limit; k++) {
            var kk = { t: r4(p.keyTime(k)), v: valueOut(p, p.keyValue(k)) };
            try {
                kk.interp = [interpName(p.keyInInterpolationType(k)), interpName(p.keyOutInterpolationType(k))];
                kk.easeIn = easesOut(p.keyInTemporalEase(k));
                kk.easeOut = easesOut(p.keyOutTemporalEase(k));
            } catch (e) {}
            ks.push(kk);
        }
        return ks;
    }

    // ------------------------------------------------------------ inspection

    function layerType(l) {
        if (l instanceof TextLayer) return "text";
        if (l instanceof ShapeLayer) return "shape";
        if (l instanceof CameraLayer) return "camera";
        if (l instanceof LightLayer) return "light";
        if (l.nullLayer) return "null";
        if (l.adjustmentLayer) return "adjustment";
        var src = l.source;
        if (src instanceof CompItem) return "precomp";
        try { if (src.mainSource instanceof SolidSource) return "solid"; } catch (e) {}
        return "footage";
    }

    function itemType(it) {
        if (it instanceof CompItem) return "comp";
        if (it instanceof FolderItem) return "folder";
        if (it instanceof FootageItem) {
            try { if (it.mainSource instanceof SolidSource) return "solid"; } catch (e) {}
            return "footage";
        }
        return "item";
    }

    function compSummary(c) {
        return { id: c.id, name: c.name, width: c.width, height: c.height, fps: r4(c.frameRate),
            duration: r4(c.duration), layers: c.numLayers };
    }

    function segOf(group, p, i) {
        return group.propertyType === PropertyType.INDEXED_GROUP ? String(i) : p.matchName;
    }

    function collectAnimated(group, prefix, depth, out) {
        for (var i = 1; i <= group.numProperties && out.length < 100; i++) {
            try {
                var p = group.property(i);
                var path = (prefix ? prefix + "/" : "") + segOf(group, p, i);
                if (p.propertyType === PropertyType.PROPERTY) {
                    var expr = p.canSetExpression && p.expressionEnabled;
                    if (p.numKeys > 0 || expr) {
                        var o = { path: path, name: p.name, keys: p.numKeys };
                        if (expr) o.expression = true;
                        out.push(o);
                    }
                } else if (depth < 6) {
                    collectAnimated(p, path, depth + 1, out);
                }
            } catch (e) {}
        }
    }

    var TF_NAMES = {
        "ADBE Anchor Point": "anchor", "ADBE Position": "position", "ADBE Scale": "scale",
        "ADBE Rotate Z": "rotation", "ADBE Opacity": "opacity", "ADBE Orientation": "orientation",
        "ADBE Rotate X": "xRotation", "ADBE Rotate Y": "yRotation"
    };

    function layerSummary(l, comp) {
        var o = { index: l.index, name: l.name, type: layerType(l) }, i;
        o["in"] = r4(l.inPoint);
        o.out = r4(l.outPoint);
        if (l.startTime !== 0) o.start = r4(l.startTime);
        if (l.parent) o.parent = l.parent.index;
        if (!l.enabled) o.enabled = false;
        try { if (l.threeDLayer) o.threeD = true; } catch (e1) {}
        try { if (l.motionBlur) o.motionBlur = true; } catch (e2) {}
        try {
            // trackMatteLayer is AE 23+; before that the matte is always the layer above.
            if (l.hasTrackMatte) o.trackMatte = { layer: l.trackMatteLayer ? l.trackMatteLayer.index : l.index - 1, type: enumName(MATTES, l.trackMatteType) };
        } catch (e7) {}
        if (o.type === "text") {
            try {
                var d = l.property("ADBE Text Properties").property("ADBE Text Document").value;
                o.text = d.text; o.font = d.font; o.fontSize = r4(d.fontSize);
            } catch (e3) {}
        }
        if (o.type === "footage" || o.type === "precomp") {
            try { o.source = l.source.name; o.sourceId = l.source.id; } catch (e4) {}
        }
        var tg = l.property("ADBE Transform Group"), tf = {};
        for (var m in TF_NAMES) {
            if (!TF_NAMES.hasOwnProperty(m)) continue;
            if (!o.threeD && (m === "ADBE Orientation" || m === "ADBE Rotate X" || m === "ADBE Rotate Y")) continue;
            try {
                var p = tg.property(m);
                if (p) tf[TF_NAMES[m]] = valueOut(p, p.valueAtTime(comp.time, false));
            } catch (e5) {}
        }
        o.transform = tf;
        var fx = null;
        try { fx = l.property("ADBE Effect Parade"); } catch (e6) {}
        if (fx && fx.numProperties) {
            o.effects = [];
            for (i = 1; i <= fx.numProperties; i++) {
                o.effects.push(i + ": " + fx.property(i).name + " [" + fx.property(i).matchName + "]");
            }
        }
        var anim = [];
        collectAnimated(l, "", 0, anim);
        if (anim.length) o.animated = anim;
        return o;
    }

    function walkProps(group, prefix, depth, opts, out) {
        for (var i = 1; i <= group.numProperties; i++) {
            var p;
            try { p = group.property(i); } catch (e) { continue; }
            var node = { path: (prefix ? prefix + "/" : "") + segOf(group, p, i), name: p.name, match: p.matchName };
            try {
                if (p.propertyType === PropertyType.PROPERTY) {
                    var expr = p.canSetExpression && p.expressionEnabled;
                    if (!opts.all && !p.isModified && p.numKeys === 0 && !expr) continue;
                    var v = valueOut(p, p.valueAtTime(opts.time, false));
                    if (v !== undefined) node.value = v;
                    if (p.numKeys > 0) node.keys = keysOut(p, 50);
                    if (expr) node.expression = p.expression;
                } else {
                    if (depth >= opts.depth) {
                        if (!opts.all && !p.isModified) continue;
                        node.more = p.numProperties;
                    } else {
                        node.children = [];
                        walkProps(p, node.path, depth + 1, opts, node.children);
                        if (!opts.all && !node.children.length) continue;
                    }
                }
            } catch (e2) {
                node.error = errText(e2);
            }
            out.push(node);
        }
    }

    // ---------------------------------------------------------------- layers

    // AE silently substitutes unknown fonts (Times New Roman on Windows), so read the
    // font back. A longer name is fine: "Bahnschrift" resolves to "Bahnschrift-Regular".
    function fontNote(requested, textProp) {
        if (!requested) return null;
        var got = textProp.value.font;
        if (String(got).toLowerCase().indexOf(String(requested).toLowerCase()) === 0) return null;
        return "Font \"" + requested + "\" is not available in After Effects, which fell back to \"" + got +
            "\". Use another PostScript name (see the fonts in ae_style_guide).";
    }

    function hasPath(shapes) {
        for (var i = 0; shapes && i < shapes.length; i++) if (shapes[i].type === "path") return true;
        return false;
    }

    function centerAnchor(l) {
        var r = l.sourceRectAtTime(l.inPoint, false);
        var ap = tprop(l, "ADBE Anchor Point");
        if (ap.numKeys > 0) return;
        var c = [r.left + r.width / 2, r.top + r.height / 2];
        if (ap.value.length > 2) c.push(ap.value[2]);
        ap.setValue(c);
    }

    function vecGroup(layer, gi) {
        return layer.property("ADBE Root Vectors Group").property(gi).property("ADBE Vectors Group");
    }

    function buildShapes(layer, shapes) {
        for (var s = 0; s < shapes.length; s++) {
            var sh = shapes[s];
            var grp = layer.property("ADBE Root Vectors Group").addProperty("ADBE Vector Group");
            var gi = grp.propertyIndex;
            if (sh.name) grp.name = sh.name;
            var type = sh.type || "rect";
            if (type === "rect") {
                var r = vecGroup(layer, gi).addProperty("ADBE Vector Shape - Rect");
                r.property("ADBE Vector Rect Size").setValue(sh.size || [400, 400]);
                if (has(sh, "roundness")) r.property("ADBE Vector Rect Roundness").setValue(sh.roundness);
            } else if (type === "ellipse") {
                var el = vecGroup(layer, gi).addProperty("ADBE Vector Shape - Ellipse");
                el.property("ADBE Vector Ellipse Size").setValue(sh.size || [400, 400]);
            } else if (type === "path") {
                var pa = vecGroup(layer, gi).addProperty("ADBE Vector Shape - Group");
                pa.property("ADBE Vector Shape").setValue(toShape(sh));
            } else if (type === "compound") {
                // Several paths sharing one fill, e.g. traced outlines with holes (fillRule evenodd).
                if (!isArr(sh.paths) || !sh.paths.length) fail("A compound shape needs `paths`: [{points, inTangents, outTangents}]");
                for (var pi = 0; pi < sh.paths.length; pi++) {
                    vecGroup(layer, gi).addProperty("ADBE Vector Shape - Group").property("ADBE Vector Shape").setValue(toShape(sh.paths[pi]));
                }
            } else {
                fail("Shape type must be rect, ellipse, path or compound");
            }
            // Trim must sit above the stroke/fill it trims; stroke above fill draws on top.
            if (sh.trim) {
                var tr = vecGroup(layer, gi).addProperty("ADBE Vector Filter - Trim");
                if (has(sh.trim, "start")) tr.property("ADBE Vector Trim Start").setValue(sh.trim.start);
                if (has(sh.trim, "end")) tr.property("ADBE Vector Trim End").setValue(sh.trim.end);
                if (has(sh.trim, "offset")) tr.property("ADBE Vector Trim Offset").setValue(sh.trim.offset);
            }
            if (has(sh, "stroke")) {
                var st = vecGroup(layer, gi).addProperty("ADBE Vector Graphic - Stroke");
                st.property("ADBE Vector Stroke Color").setValue(toColor(sh.stroke));
                st.property("ADBE Vector Stroke Width").setValue(num(sh.strokeWidth, 4));
                if (sh.lineCap) st.property("ADBE Vector Stroke Line Cap").setValue(sh.lineCap === "round" ? 2 : (sh.lineCap === "square" ? 3 : 1));
            }
            if (has(sh, "fill") || !has(sh, "stroke")) {
                var f = vecGroup(layer, gi).addProperty("ADBE Vector Graphic - Fill");
                f.property("ADBE Vector Fill Color").setValue(toColor(has(sh, "fill") ? sh.fill : "#ffffff"));
                if (sh.fillRule === "evenodd") f.property("ADBE Vector Fill Rule").setValue(2);
            }
            if (sh.offset) {
                layer.property("ADBE Root Vectors Group").property(gi).property("ADBE Vector Transform Group")
                    .property("ADBE Vector Position").setValue(sh.offset);
            }
        }
    }

    function importFile(path, sequence) {
        var f = new File(path);
        if (!f.exists) fail("File not found: " + path);
        var io = new ImportOptions(f);
        if (sequence) io.sequence = true;
        return app.project.importFile(io);
    }

    function scaleValue(p, s) {
        var cur = p.value, out = [];
        for (var i = 0; i < cur.length; i++) out.push(i < 2 ? s : (cur.length > 2 ? cur[i] : s));
        return out;
    }

    function applyCommon(l, a, comp) {
        if (has(a, "name")) l.name = a.name;
        if (has(a, "threeD")) l.threeDLayer = !!a.threeD;
        if (has(a, "position")) tprop(l, "ADBE Position").setValue(a.position);
        if (has(a, "scale")) tprop(l, "ADBE Scale").setValue(typeof a.scale === "number" ? scaleValue(tprop(l, "ADBE Scale"), a.scale) : a.scale);
        if (has(a, "rotation")) tprop(l, "ADBE Rotate Z").setValue(a.rotation);
        if (has(a, "opacity")) tprop(l, "ADBE Opacity").setValue(a.opacity);
        if (has(a, "start")) l.startTime = a.start;
        if (has(a, "in")) l.inPoint = a["in"];
        if (has(a, "out")) l.outPoint = a.out;
        if (a.parent !== undefined) l.parent = (a.parent === null || a.parent === 0 || a.parent === "") ? null : findLayer(comp, a.parent);
        if (has(a, "blend")) {
            if (!BLEND.hasOwnProperty(a.blend)) fail("blend must be one of " + keysOf(BLEND).join(", "));
            l.blendingMode = BLEND[a.blend];
        }
        if (has(a, "label")) l.label = a.label;
        if (has(a, "motionBlur")) { try { l.motionBlur = !!a.motionBlur; if (a.motionBlur) comp.motionBlur = true; } catch (e) {} }
    }

    // --------------------------------------------------------------- motion

    var MOTION_DEFAULTS = { "in": { duration: 0.7, ease: "out_expo" }, out: { duration: 0.45, ease: "in_expo" } };

    function restValue(p, c) {
        if (p.numKeys === 0) return p.value;
        return p.valueAtTime(c.dir === "out" ? c.t0 : c.t1, true);
    }

    // `off` is the hidden/away state, `rest` the settled one; direction picks the order.
    function animProp(p, off, rest, c, ta, tb, ease) {
        var from = c.dir === "out" ? rest : off;
        var to = c.dir === "out" ? off : rest;
        p.setValueAtTime(ta, from);
        p.setValueAtTime(tb, to);
        easeSegment(p, p.nearestKeyIndex(ta), p.nearestKeyIndex(tb), ease);
    }

    function mOpacity(c, frac) {
        if (c.didOpacity) return;
        c.didOpacity = true;
        frac = frac || 0.6;
        var p = tprop(c.l, "ADBE Opacity");
        var r = restValue(p, c);
        if (r === 0) r = 100;
        var ta = c.dir === "out" ? c.t1 - c.dur * frac : c.t0;
        var tb = c.dir === "out" ? c.t1 : c.t0 + c.dur * frac;
        animProp(p, 0, r, c, ta, tb, c.dir === "out" ? "in" : "out");
    }

    function mOffset(c, dx, dy) {
        var dist = num(c.a.distance, Math.round(c.comp.height * 0.06));
        var s = c.dir === "out" ? -1 : 1;
        var pos = tprop(c.l, "ADBE Position");
        if (pos.dimensionsSeparated) {
            if (dx) { var px = tprop(c.l, "ADBE Position_0"), rx = restValue(px, c); animProp(px, rx + s * dx * dist, rx, c, c.t0, c.t1, c.ease); }
            if (dy) { var py = tprop(c.l, "ADBE Position_1"), ry = restValue(py, c); animProp(py, ry + s * dy * dist, ry, c, c.t0, c.t1, c.ease); }
            return;
        }
        var r = restValue(pos, c), o = r.slice(0);
        o[0] += s * dx * dist;
        o[1] += s * dy * dist;
        animProp(pos, o, r, c, c.t0, c.t1, c.ease);
    }

    function mScale(c, pct, ease) {
        var p = tprop(c.l, "ADBE Scale"), r = restValue(p, c), o = [];
        for (var i = 0; i < r.length; i++) o.push(i < 2 ? r[i] * pct / 100 : r[i]);
        animProp(p, o, r, c, c.t0, c.t1, ease || c.ease);
    }

    function addEffect(l, match, name) {
        var fx = l.property("ADBE Effect Parade").addProperty(match);
        if (name) fx.name = name;
        return fx.propertyIndex;
    }

    function fxParam(l, fi, param) {
        return l.property("ADBE Effect Parade").property(fi).property(param);
    }

    function mBlur(c) {
        var fi = addEffect(c.l, "ADBE Gaussian Blur 2", "Claude Blur");
        // AE 2023+ defaults Repeat Edge Pixels on, which smears the layer's bounding box.
        try { fxParam(c.l, fi, 3).setValue(0); } catch (e) {}
        animProp(fxParam(c.l, fi, 1), num(c.a.blur, 30), 0, c, c.t0, c.t1, c.ease);
    }

    function mWipe(c) {
        var fi = addEffect(c.l, "ADBE Linear Wipe", "Claude Wipe");
        fxParam(c.l, fi, 2).setValue(num(c.a.angle, 90));
        fxParam(c.l, fi, 3).setValue(num(c.a.feather, 0));
        animProp(fxParam(c.l, fi, 1), 100, 0, c, c.t0, c.t1, c.ease);
    }

    function requireText(l) {
        if (!(l instanceof TextLayer)) fail("Layer \"" + l.name + "\" is not a text layer");
    }

    function animAt(l, ai) {
        return l.property("ADBE Text Properties").property("ADBE Text Animators").property(ai);
    }

    function addAnimator(l, name) {
        var an = l.property("ADBE Text Properties").property("ADBE Text Animators").addProperty("ADBE Text Animator");
        an.name = name;
        return an.propertyIndex;
    }

    // Every animatable property already exists in a new animator as a hidden slot, so
    // property(match) is never null; it has to be activated with addProperty.
    function addAnimProp(l, ai, match, value) {
        var g = animAt(l, ai).property("ADBE Text Animator Properties");
        if (g.canAddProperty(match)) g.addProperty(match);
        var p = animAt(l, ai).property("ADBE Text Animator Properties").property(match);
        if (value === undefined) return p;
        try {
            p.setValue(value);
        } catch (e) {
            if (isArr(value) && value.length === 3) p.setValue([value[0], value[1]]); else throw e;
        }
        return animAt(l, ai).property("ADBE Text Animator Properties").property(match);
    }

    // New animators may or may not come with a range selector; reuse it if present.
    function selector(l, ai) {
        var g = animAt(l, ai).property("ADBE Text Selectors");
        if (g.numProperties === 0) g.addProperty("ADBE Text Selector");
        return animAt(l, ai).property("ADBE Text Selectors").property(1);
    }

    function mTypeOn(c) {
        requireText(c.l);
        var ai = addAnimator(c.l, "Claude Type On");
        addAnimProp(c.l, ai, "ADBE Text Opacity", 0);
        try { selector(c.l, ai).property("ADBE Text Range Advanced").property("ADBE Text Selector Smoothness").setValue(0); } catch (e) {}
        animProp(selector(c.l, ai).property("ADBE Text Percent Start"), 0, 100, c, c.t0, c.t1, "linear");
    }

    // Per-character rise: a Ramp Up range selector of width `spread` sweeps across the text.
    function mCascade(c) {
        requireText(c.l);
        var dist = num(c.a.distance, Math.round(c.comp.height * 0.04));
        var spread = clamp(num(c.a.spread, 40), 1, 100);
        var ai = addAnimator(c.l, "Claude Cascade");
        addAnimProp(c.l, ai, "ADBE Text Position 3D", [0, dist, 0]);
        addAnimProp(c.l, ai, "ADBE Text Opacity", 0);
        if (has(c.a, "blur")) addAnimProp(c.l, ai, "ADBE Text Blur", [c.a.blur, c.a.blur]);
        if (has(c.a, "from")) addAnimProp(c.l, ai, "ADBE Text Scale 3D", [c.a.from, c.a.from, 100]);
        selector(c.l, ai).property("ADBE Text Range Advanced").property("ADBE Text Range Shape").setValue(2);
        try {
            selector(c.l, ai).property("ADBE Text Range Advanced").property("ADBE Text Levels Max Ease").setValue(50);
            selector(c.l, ai).property("ADBE Text Range Advanced").property("ADBE Text Levels Min Ease").setValue(50);
        } catch (e) {}
        selector(c.l, ai).property("ADBE Text Percent Start").setValue(0);
        selector(c.l, ai).property("ADBE Text Percent End").setValue(spread);
        animProp(selector(c.l, ai).property("ADBE Text Percent Offset"), -spread, 100, c, c.t0, c.t1, c.ease);
    }

    function mTracking(c) {
        requireText(c.l);
        var ai = addAnimator(c.l, "Claude Tracking");
        addAnimProp(c.l, ai, "ADBE Text Tracking Amount");
        selector(c.l, ai);
        var p = animAt(c.l, ai).property("ADBE Text Animator Properties").property("ADBE Text Tracking Amount");
        animProp(p, num(c.a.tracking, 200), 0, c, c.t0, c.t1, c.ease);
        mOpacity(c);
    }

    var PRESETS = {
        fade: function (c) { mOpacity(c, 1); },
        slide_up: function (c) { mOffset(c, 0, 1); mOpacity(c); },
        slide_down: function (c) { mOffset(c, 0, -1); mOpacity(c); },
        slide_left: function (c) { mOffset(c, 1, 0); mOpacity(c); },
        slide_right: function (c) { mOffset(c, -1, 0); mOpacity(c); },
        scale: function (c) { mScale(c, num(c.a.from, 85)); mOpacity(c); },
        pop: function (c) { mScale(c, num(c.a.from, 0), c.dir === "out" ? "in_back" : "out_back"); mOpacity(c, 0.3); },
        blur: function (c) { mBlur(c); mOpacity(c); },
        wipe: function (c) { mWipe(c); },
        type_on: function (c) { mTypeOn(c); },
        cascade: function (c) { mCascade(c); },
        tracking: function (c) { mTracking(c); }
    };

    function defaultDuration(preset, l, dir) {
        if (preset === "type_on") {
            var n = 20;
            try { n = l.property("ADBE Text Properties").property("ADBE Text Document").value.text.length; } catch (e) {}
            return clamp(n * 0.035, 0.3, 4);
        }
        if (preset === "cascade") return dir === "out" ? 0.7 : 1.0;
        return MOTION_DEFAULTS[dir].duration;
    }

    // ----------------------------------------------------------- expressions

    // Each keyframe change is a step input to the spring, starting at that key's time;
    // the responses add up, so a bounce keeps ringing out after the next key passes.
    var SPRING_BODY = [
        "var n = numKeys, out = value;",
        "if (n >= 2) {",
        "  var w = 2 * Math.PI * f, wd = w * Math.sqrt(1 - z * z);",
        "  var arr = key(1).value instanceof Array;",
        "  out = arr ? key(1).value.slice(0) : key(1).value;",
        "  for (var j = 1; j < n && key(j).time <= time; j++) {",
        "    var t = time - key(j).time;",
        "    var p = 1 - Math.exp(-z * w * t) * (Math.cos(wd * t) + (z * w / wd) * Math.sin(wd * t));",
        "    var a = key(j).value, b = key(j + 1).value;",
        "    if (arr) { for (var k = 0; k < a.length; k++) out[k] += (b[k] - a[k]) * p; }",
        "    else out += (b - a) * p;",
        "  }",
        "  if (lo !== null) { if (arr) { for (var m = 0; m < out.length; m++) out[m] = Math.max(lo, out[m]); } else out = Math.max(lo, out); }",
        "}",
        "out;"
    ].join("\n");

    var EXPRESSIONS = {
        // Physical spring: keyframes only set targets and start times, the spring decides
        // the motion. freq in Hz, damping 0.05 (bouncy) .. 0.95 (stiff), optional min clamp.
        spring: function (o) {
            return "// claude:spring\nvar f = " + num(o.freq, 2.2) + ", z = " + clamp(num(o.damping, 0.45), 0.05, 0.95) +
                ", lo = " + (typeof o.min === "number" ? o.min : "null") + ";\n" + SPRING_BODY;
        },
        wiggle: function (o) { return "wiggle(" + num(o.freq, 2) + ", " + num(o.amp, 10) + ")"; },
        loop: function (o) { return "loopOut(\"" + (o.type || "cycle") + "\")"; },
        time: function (o) { return "value + time * " + num(o.speed, 90); }
    };

    // ------------------------------------------------------------------- ops

    OPS.status = { fn: function () {
        var a = app.project.activeItem, comps = [];
        for (var i = 1; i <= app.project.numItems && comps.length < 50; i++) {
            if (app.project.item(i) instanceof CompItem) comps.push(compSummary(app.project.item(i)));
        }
        var dirty = null;
        try { dirty = app.project.dirty; } catch (e) {}
        return {
            bridge: VERSION, ae: app.version,
            project: app.project.file ? app.project.file.fsName : null, unsavedChanges: dirty,
            activeComp: (a instanceof CompItem) ? compSummary(a) : null,
            comps: comps, fontsApi: !!app.fonts
        };
    } };

    OPS.project = { fn: function () {
        var items = [];
        for (var i = 1; i <= app.project.numItems; i++) {
            var it = app.project.item(i);
            var o = { id: it.id, name: it.name, type: itemType(it) };
            if (it.parentFolder && it.parentFolder !== app.project.rootFolder) o.folder = it.parentFolder.name;
            if (it instanceof CompItem) {
                var s = compSummary(it);
                o.width = s.width; o.height = s.height; o.fps = s.fps; o.duration = s.duration; o.layers = s.layers;
            } else if (it instanceof FootageItem) {
                try { o.width = it.width; o.height = it.height; o.duration = r4(it.duration); o.file = it.file ? it.file.fsName : undefined; } catch (e) {}
            }
            items.push(o);
        }
        return { project: app.project.file ? app.project.file.fsName : null, items: items };
    } };

    OPS.comp_info = { fn: function (a) {
        var c = findComp(a.comp);
        if (has(a, "time")) c.time = a.time;
        var o = compSummary(c);
        o.bg = rgbHex(c.bgColor);
        o.time = r4(c.time);
        o.motionBlur = c.motionBlur;
        o.workArea = [r4(c.workAreaStart), r4(c.workAreaStart + c.workAreaDuration)];
        o.layerList = [];
        for (var i = 1; i <= c.numLayers; i++) o.layerList.push(layerSummary(c.layer(i), c));
        return o;
    } };

    OPS.layer_props = { fn: function (a) {
        var c = findComp(a.comp), l = findLayer(c, a.layer);
        var start = has(a, "path") ? resolveProp(l, a.path) : l;
        var opts = { all: !!a.all, depth: num(a.depth, 4), time: num(a.time, c.time) };
        if (start !== l && start.propertyType === PropertyType.PROPERTY) {
            var single = [];
            walkProps(start.parentProperty, "", 0, { all: true, depth: 0, time: opts.time }, single);
            for (var i = 0; i < single.length; i++) if (single[i].match === start.matchName) return single[i];
            return single;
        }
        var out = [];
        walkProps(start, has(a, "path") ? String(a.path) : "", 0, opts, out);
        return { layer: l.index, name: l.name, props: out };
    } };

    OPS.create_comp = { mutates: true, fn: function (a) {
        var c = app.project.items.addComp(a.name || "Comp", num(a.width, 1920), num(a.height, 1080),
            num(a.pixelAspect, 1), num(a.duration, 10), num(a.fps, 30));
        c.bgColor = toColor(a.bg || "#000000").slice(0, 3);
        c.motionBlur = a.motionBlur !== false;
        if (a.open !== false) c.openInViewer();
        return compSummary(c);
    } };

    OPS.add_layer = { mutates: true, fn: function (a) {
        var c = findComp(a.comp), kind = a.kind || "solid", l, warn = [];
        var center = [c.width / 2, c.height / 2];
        if (kind === "solid" || kind === "adjustment") {
            l = c.layers.addSolid(toColor(a.color || (kind === "adjustment" ? "#ffffff" : "#000000")).slice(0, 3),
                a.name || (kind === "adjustment" ? "Adjustment" : "Solid"), num(a.width, c.width), num(a.height, c.height), 1, c.duration);
            if (kind === "adjustment") l.adjustmentLayer = true;
        } else if (kind === "text") {
            l = has(a, "box") ? c.layers.addBoxText(a.box, a.text || "") : c.layers.addText(a.text || "");
            var tp = l.property("ADBE Text Properties").property("ADBE Text Document");
            tp.setValue(applyTextDoc(tp.value, {
                text: a.text || "", font: a.font, fontSize: a.fontSize, fillColor: a.fillColor || "#ffffff",
                tracking: a.tracking, leading: a.leading, justification: a.justification || "center", allCaps: a.allCaps
            }));
            var fw = fontNote(a.font, l.property("ADBE Text Properties").property("ADBE Text Document"));
            if (fw) warn.push(fw);
        } else if (kind === "shape") {
            l = c.layers.addShape();
            buildShapes(l, a.shapes || [{ type: "rect" }]);
        } else if (kind === "null") {
            l = c.layers.addNull(c.duration);
        } else if (kind === "camera") {
            l = c.layers.addCamera(a.name || "Camera", center);
            var zp = l.property("ADBE Camera Options Group").property("ADBE Camera Zoom"), z = has(a, "zoom") ? a.zoom : zp.value;
            if (has(a, "zoom")) zp.setValue(a.zoom);
            // addCamera aims at centerPoint but leaves the camera at [0, 0, -zoom]: put it straight in front of the centre.
            l.property("ADBE Transform Group").property("ADBE Position").setValue([center[0], center[1], -z]);
        } else if (kind === "light") {
            l = c.layers.addLight(a.name || "Light", center);
            if (has(a, "lightType")) l.lightType = LIGHTS[a.lightType];
            if (has(a, "color")) l.property("ADBE Light Options Group").property("ADBE Light Color").setValue(toColor(a.color));
            if (has(a, "intensity")) l.property("ADBE Light Options Group").property("ADBE Light Intensity").setValue(a.intensity);
        } else if (kind === "footage" || kind === "comp") {
            var it = has(a, "item") ? findItem(a.item) : importFile(a.path, a.sequence);
            l = c.layers.add(it);
        } else {
            fail("kind must be solid, adjustment, text, shape, null, camera, light, footage or comp");
        }
        // Rects and ellipses are already centered on the layer origin; bounds-centering them
        // would pull the pivot off-center whenever a stroke is trimmed.
        if (a.anchorCenter === true || (a.anchorCenter !== false && (kind === "text" || (kind === "shape" && hasPath(a.shapes))))) {
            centerAnchor(l);
        }
        if (kind !== "camera" && kind !== "light" && !has(a, "motionBlur")) a.motionBlur = true;
        applyCommon(l, a, c);
        return { layer: layerSummary(l, c), warnings: warn.length ? warn : undefined };
    } };

    OPS.set_props = { mutates: true, fn: function (a) {
        var c = findComp(a.comp), layers = findLayers(c, has(a, "layers") ? a.layers : a.layer);
        var t = num(a.time, c.time), out = [];
        if (!a.props) fail("Pass `props`: {\"<path>\": value}");
        for (var i = 0; i < layers.length; i++) {
            var l = layers[i], set = {}, warn = [];
            for (var k in a.props) {
                if (!a.props.hasOwnProperty(k)) continue;
                var p = resolveProp(l, k);
                setValue(p, a.props[k], t);
                p = resolveProp(l, k);
                set[k] = valueOut(p, p.valueAtTime(t, false));
                if (p.propertyValueType === PropertyValueType.TEXT_DOCUMENT && a.props[k] && a.props[k].font) {
                    var fw = fontNote(a.props[k].font, p);
                    if (fw) warn.push(fw);
                }
            }
            out.push({ layer: l.index, values: set, warnings: warn.length ? warn : undefined });
        }
        return out;
    } };

    OPS.set_keyframes = { mutates: true, fn: function (a) {
        var c = findComp(a.comp), l = findLayer(c, a.layer), p = resolveProp(l, a.prop), i;
        if (!isArr(a.keys) || !a.keys.length) fail("Pass `keys`: [{t, v, ease?}, ...]");
        var keys = a.keys.slice(0).sort(function (x, y) { return x.t - y.t; });
        var tol = c.frameDuration / 2, lo = keys[0].t, hi = keys[keys.length - 1].t, replaced = 0;
        if (a.clear) {
            for (i = p.numKeys; i >= 1; i--) p.removeKey(i);
        } else if (!a.keepBetween) {
            // Old keys inside the new span would split the eased segments.
            for (i = p.numKeys; i >= 1; i--) {
                var kt = p.keyTime(i), onNew = false;
                for (var j = 0; j < keys.length; j++) if (Math.abs(keys[j].t - kt) < tol) onNew = true;
                if (kt > lo + tol && kt < hi - tol && !onNew) { p.removeKey(i); replaced++; }
            }
        }
        for (i = 0; i < keys.length; i++) {
            var base = p.numKeys > 0 ? p.valueAtTime(keys[i].t, true) : p.value;
            p.setValueAtTime(keys[i].t, toAE(p, keys[i].v, base));
        }
        for (i = 0; i + 1 < keys.length; i++) {
            easeSegment(p, p.nearestKeyIndex(keys[i].t), p.nearestKeyIndex(keys[i + 1].t),
                keys[i].ease || a.ease || "in_out");
        }
        return { layer: l.index, prop: p.name, replacedKeys: replaced || undefined, keys: keysOut(p, 50) };
    } };

    OPS.animate = { mutates: true, fn: function (a) {
        var c = findComp(a.comp), layers = findLayers(c, has(a, "layers") ? a.layers : a.layer);
        var presets = isArr(a.preset) ? a.preset : [a.preset];
        var dir = a.direction === "out" ? "out" : "in";
        var t = num(a.time, c.time), stagger = num(a.stagger, 0), out = [], i, j;
        for (j = 0; j < presets.length; j++) {
            if (!PRESETS.hasOwnProperty(presets[j])) fail("Unknown preset \"" + presets[j] + "\". Use one of " + keysOf(PRESETS).join(", "));
        }
        for (i = 0; i < layers.length; i++) {
            var l = layers[i];
            var dur = num(a.duration, defaultDuration(presets[0], l, dir));
            var ctx = { comp: c, l: l, a: a, dir: dir, t0: t + i * stagger, dur: dur, ease: a.ease || MOTION_DEFAULTS[dir].ease, didOpacity: false };
            ctx.t1 = ctx.t0 + dur;
            for (j = 0; j < presets.length; j++) {
                // Each preset fetches fresh property references: adding effects or
                // animators invalidates the ones taken before.
                PRESETS[presets[j]](ctx);
            }
            if (a.motionBlur !== false) { try { l.motionBlur = true; c.motionBlur = true; } catch (e) {} }
            out.push({ layer: l.index, name: l.name, from: r4(ctx.t0), to: r4(ctx.t1) });
        }
        return { preset: presets, direction: dir, layers: out };
    } };

    OPS.effect = { mutates: true, fn: function (a) {
        var c = findComp(a.comp), layers = findLayers(c, has(a, "layers") ? a.layers : a.layer);
        var t = num(a.time, c.time), out = [];
        for (var i = 0; i < layers.length; i++) {
            var l = layers[i], fi;
            if (has(a, "index")) {
                fi = a.index;
            } else {
                var parade = l.property("ADBE Effect Parade");
                if (!parade.canAddProperty(a.effect)) fail("Cannot add effect \"" + a.effect + "\". Use its matchName; see ae_list {what:'effects'}.");
                fi = addEffect(l, a.effect, a.name);
            }
            if (a.params) {
                for (var k in a.params) {
                    if (!a.params.hasOwnProperty(k)) continue;
                    var p = fxParam(l, fi, /^\d+$/.test(k) ? parseInt(k, 10) : k);
                    if (!p) fail("Effect has no parameter \"" + k + "\"");
                    setValue(p, a.params[k], t);
                }
            }
            var fx = l.property("ADBE Effect Parade").property(fi), params = [];
            for (var j = 1; j <= fx.numProperties; j++) {
                var q = fx.property(j);
                if (q.propertyType !== PropertyType.PROPERTY) continue;
                var v;
                try { v = valueOut(q, q.valueAtTime(t, false)); } catch (e) {}
                if (v !== undefined) params.push({ i: j, name: q.name, value: v });
            }
            out.push({ layer: l.index, index: fi, name: fx.name, match: fx.matchName, params: params });
        }
        return out;
    } };

    OPS.expression = { mutates: true, fn: function (a) {
        var c = findComp(a.comp), layers = findLayers(c, has(a, "layers") ? a.layers : a.layer), out = [];
        var expr = a.expression || "";
        if (has(a, "template")) {
            if (!EXPRESSIONS.hasOwnProperty(a.template)) fail("Unknown template. Use one of " + keysOf(EXPRESSIONS).join(", "));
            expr = EXPRESSIONS[a.template](a.options || {});
        }
        for (var i = 0; i < layers.length; i++) {
            var p = resolveProp(layers[i], a.prop);
            if (!p.canSetExpression) fail("\"" + p.name + "\" cannot take an expression");
            p.expression = expr;
            var err = "";
            if (expr) {
                try { p.valueAtTime(c.time, false); } catch (e1) {}
                try { err = p.expressionError; } catch (e2) {}
            }
            out.push({ layer: layers[i].index, prop: p.name, enabled: p.expressionEnabled, error: err || undefined });
        }
        return out;
    } };

    OPS.edit_layer = { mutates: true, fn: function (a) {
        var c = findComp(a.comp), l = findLayer(c, a.layer), res = {};
        applyCommon(l, a, c);
        if (has(a, "enabled")) l.enabled = !!a.enabled;
        if (has(a, "shy")) l.shy = !!a.shy;
        if (has(a, "solo")) l.solo = !!a.solo;
        if (has(a, "adjustment")) l.adjustmentLayer = !!a.adjustment;
        if (has(a, "guide")) l.guideLayer = !!a.guide;
        if (has(a, "collapse")) l.collapseTransformation = !!a.collapse;
        if (has(a, "stretch")) l.stretch = a.stretch;
        if (has(a, "color")) {
            if (layerType(l) !== "solid" && layerType(l) !== "adjustment") fail("`color` applies to solid layers");
            l.source.mainSource.color = toColor(a.color).slice(0, 3);
        }
        if (a.trackMatte !== undefined) {
            if (a.trackMatte === null || a.trackMatte === "none") {
                if (l.removeTrackMatte) l.removeTrackMatte(); else l.trackMatteType = TrackMatteType.NO_TRACK_MATTE;
            } else {
                var ml = findLayer(c, a.trackMatte.layer), tt = MATTES[a.trackMatte.type || "alpha"];
                if (tt === undefined) fail("trackMatte.type must be alpha, alpha_inverted, luma or luma_inverted");
                if (l.setTrackMatte) {
                    l.setTrackMatte(ml, tt);
                } else {
                    ml.moveBefore(l);
                    l.trackMatteType = tt;
                }
            }
        }
        if (has(a, "moveTo")) {
            if (a.moveTo <= 1) l.moveToBeginning();
            else if (a.moveTo >= c.numLayers) l.moveToEnd();
            else if (a.moveTo < l.index) l.moveBefore(c.layer(a.moveTo));
            else if (a.moveTo > l.index) l.moveAfter(c.layer(a.moveTo));
        }
        if (a.duplicate) res.duplicate = layerSummary(l.duplicate(), c);
        if (a.precompose) {
            // The layer object is invalid after precomposing; its replacement sits at the same index.
            var at = l.index;
            var pc = c.layers.precompose([at], a.precompose.name || (l.name + " comp"), true);
            res.precomp = compSummary(pc);
            l = c.layer(at);
        }
        if (a["delete"]) {
            var gone = l.name;
            l.remove();
            res.deleted = gone;
            return res;
        }
        res.layer = layerSummary(l, c);
        return res;
    } };

    OPS.import_file = { mutates: true, fn: function (a) {
        var it = importFile(a.path, a.sequence), res = { item: { id: it.id, name: it.name, type: itemType(it) } };
        if (has(a, "comp")) {
            var c = findComp(a.comp), l = c.layers.add(it);
            applyCommon(l, a, c);
            res.layer = layerSummary(l, c);
        }
        return res;
    } };

    OPS.render_frames = { fn: function (a) {
        var c = findComp(a.comp), fd = c.frameDuration, last = c.duration - fd, times = [], i;
        if (isArr(a.times) && a.times.length) {
            times = a.times;
        } else if (a.current) {
            times = [c.time];
        } else {
            var n = clamp(num(a.count, 9), 1, 48);
            var s = clamp(num(a.start, 0), 0, last), e = clamp(num(a.end, last), 0, last);
            for (i = 0; i < n; i++) times.push(n === 1 ? s : s + (e - s) * i / (n - 1));
        }
        if (!framesDir.exists) framesDir.create();
        var tag = a.tag || String(now()), out = [];
        for (i = 0; i < times.length; i++) {
            var t = clamp(Math.round(times[i] / fd) * fd, 0, last);
            var f = new File(framesDir.fsName + "/" + tag + "_" + i + ".png");
            if (f.exists) f.remove();
            c.saveFrameToPng(t, f);
            out.push({ t: r4(t), frame: Math.round(t / fd), path: f.fsName });
        }
        return { comp: c.name, width: c.width, height: c.height, frames: out };
    } };

    OPS.sample_motion = { fn: function (a) {
        var c = findComp(a.comp), l = findLayer(c, a.layer), p = resolveProp(l, a.prop);
        var t = p.propertyValueType;
        if (t === PropertyValueType.TEXT_DOCUMENT || t === PropertyValueType.SHAPE || t === PropertyValueType.NO_VALUE ||
            t === PropertyValueType.CUSTOM_VALUE || t === PropertyValueType.MARKER) {
            fail("\"" + p.name + "\" is not numeric");
        }
        var fd = c.frameDuration, t0 = num(a.start, NaN), t1 = num(a.end, NaN);
        if (isNaN(t0)) t0 = p.numKeys ? Math.max(0, p.keyTime(1) - 0.25) : 0;
        if (isNaN(t1)) t1 = p.numKeys ? Math.min(c.duration, p.keyTime(p.numKeys) + 0.75) : c.duration;
        var n = clamp(Math.round((t1 - t0) / fd) + 1, 2, 2000), times = [], values = [], keys = [];
        for (var i = 0; i < n; i++) {
            var tt = t0 + i * fd;
            times.push(r4(tt));
            var v = p.valueAtTime(tt, false);
            values.push(t === PropertyValueType.COLOR ? v : r4(v));
        }
        for (var k = 1; k <= p.numKeys; k++) keys.push(r4(p.keyTime(k)));
        return { layer: l.name, prop: p.name, fps: r4(c.frameRate), times: times, values: values, keys: keys, spatial: p.isSpatial };
    } };

    OPS.render_video = { fn: function (a) {
        var c = findComp(a.comp), rq = app.project.renderQueue, paused = [], i;
        if (!a.path) fail("Pass `path` for the output file");
        for (i = 1; i <= rq.numItems; i++) {
            if (rq.item(i).status === RQItemStatus.QUEUED) { rq.item(i).render = false; paused.push(rq.item(i)); }
        }
        var item = rq.items.add(c), om = item.outputModule(1), tpl = null, names = om.templates;
        if (a.template) {
            for (i = 0; i < names.length; i++) if (names[i] === a.template) tpl = names[i];
            if (!tpl) fail("No output module template \"" + a.template + "\". Available: " + names.join(", "));
        } else {
            // Highest-bitrate H.264: low bitrates band gradients and smear fine motion.
            var best = -1;
            for (i = 0; i < names.length; i++) {
                if (names[i].indexOf("H.264") < 0) continue;
                var m = /(\d+)\s*Mbps/.exec(names[i]), rate = m ? parseInt(m[1], 10) : 0;
                if (rate > best) { best = rate; tpl = names[i]; }
            }
        }
        if (tpl) om.applyTemplate(tpl);
        om.file = new File(a.path);
        if (!a.workArea) { item.timeSpanStart = 0; item.timeSpanDuration = c.duration; }
        try {
            rq.render();
        } finally {
            for (i = 0; i < paused.length; i++) paused[i].render = true;
        }
        // The status can lag right after render() returns (it read USER_STOPPED once for a complete render).
        for (i = 0; i < 20 && item.status !== RQItemStatus.DONE; i++) $.sleep(250);
        var st = item.status;
        var status = st === RQItemStatus.DONE ? "done" : st === RQItemStatus.ERR_STOPPED ? "failed" :
            st === RQItemStatus.USER_STOPPED ? "stopped by user" : String(st);
        var out = new File(om.file.fsName);
        // While a script is still running, AE often reports USER_STOPPED for a finished render
        // (the queue only updates once AE is idle), so a written file is the better signal.
        if (status !== "done" && out.exists && out.length > 0 && st !== RQItemStatus.ERR_STOPPED) {
            status = "done (output written; AE reported \"" + status + "\" while the script was running)";
        }
        return { path: out.fsName, template: tpl, status: status, fileBytes: out.exists ? out.length : 0, seconds: item.elapsedSeconds };
    } };

    OPS.list = { fn: function (a) {
        var q = (a.query || "").toLowerCase(), out = [], i, j;
        function hit(s) { return !q || String(s).toLowerCase().indexOf(q) >= 0; }
        if (a.what === "effects") {
            for (i = 0; i < app.effects.length && out.length < 300; i++) {
                var e = app.effects[i];
                if (hit(e.displayName + " " + e.matchName + " " + e.category)) out.push({ name: e.displayName, match: e.matchName, category: e.category });
            }
        } else if (a.what === "fonts") {
            if (!app.fonts) fail("Font listing needs After Effects 24.0 or later");
            var fams = app.fonts.allFonts;
            for (i = 0; i < fams.length && out.length < 300; i++) {
                for (j = 0; j < fams[i].length && out.length < 300; j++) {
                    var f = fams[i][j];
                    if (hit(f.familyName + " " + f.styleName + " " + f.postScriptName)) out.push({ family: f.familyName, style: f.styleName, postScript: f.postScriptName });
                }
            }
        } else if (a.what === "templates") {
            var rq = app.project.renderQueue, tmpItem = null, tmpComp = null, item;
            if (rq.numItems > 0) {
                item = rq.item(1);
            } else {
                tmpComp = app.project.items.addComp("__claude_tmp", 16, 16, 1, 1, 30);
                item = tmpItem = rq.items.add(tmpComp);
            }
            out = item.outputModule(1).templates;
            if (tmpItem) tmpItem.remove();
            if (tmpComp) tmpComp.remove();
        } else if (a.what === "eases") {
            out = keysOf(EASES);
        } else if (a.what === "presets") {
            out = { presets: keysOf(PRESETS), expressionTemplates: keysOf(EXPRESSIONS) };
        } else {
            fail("what must be effects, fonts, templates, eases or presets");
        }
        return out;
    } };

    OPS.eval = { mutates: true, fn: function (a) {
        var script = String(a.script || "");
        // `return` style bodies run as a function; bare expressions return their value.
        if (/\breturn\b/.test(script)) return (new Function("CB", script))(CB);
        return eval(script);
    } };

    OPS.save = { fn: function (a) {
        if (has(a, "path")) app.project.save(new File(a.path));
        else if (app.project.file) app.project.save();
        else fail("Project was never saved; pass `path`.");
        return { project: app.project.file.fsName };
    } };

    OPS.undo = { fn: function (a) {
        var n = clamp(num(a.steps, 1), 1, 20);
        for (var i = 0; i < n; i++) app.executeCommand(UNDO_COMMAND);
        return { undone: n };
    } };

    // Re-reads this file after it was edited, keeping the docked panel. The new copy
    // stops this one first; the result is still written by this (old) instance.
    OPS.reload = { fn: function () {
        G.__ClaudeBridgeRoot = root.fsName;
        G.__ClaudeBridgeHost = CB.ui ? CB.ui.win : undefined;
        $.evalFile(new File(root.fsName + "/ae/ClaudeBridge.jsx"));
        return { reloaded: G.ClaudeBridge.version, running: !!G.ClaudeBridge.taskId };
    } };

    OPS.open_comp = { fn: function (a) {
        var c = findComp(a.comp);
        c.openInViewer();
        if (has(a, "time")) c.time = a.time;
        return compSummary(c);
    } };

    // -------------------------------------------------------------- the loop

    function log(msg) {
        var d = new Date();
        var ts = ("0" + d.getHours()).slice(-2) + ":" + ("0" + d.getMinutes()).slice(-2) + ":" + ("0" + d.getSeconds()).slice(-2);
        CB.lines.push(ts + "  " + msg);
        if (CB.lines.length > 40) CB.lines.shift();
        refreshUI();
    }

    function refreshUI() {
        if (!CB.ui) return;
        try {
            CB.ui.status.text = (CB.taskId ? "Running" : "Stopped") + "  |  " + CB.handled + " commands";
            CB.ui.log.text = CB.lines.join("\n");
        } catch (e) {}
    }

    function beat(busy, op) {
        CB.lastBeat = now();
        var proj = null;
        try { proj = app.project.file ? app.project.file.fsName : null; } catch (e) {}
        writeText(spool.fsName + "/heartbeat.json", ser({
            time: CB.lastBeat, busy: !!busy, op: op || undefined, bridge: VERSION, ae: app.version, project: proj
        }, 0));
    }

    var UNDO_COMMAND = 16; // Edit > Undo

    // A throwaway change that guarantees the command's undo group is not empty, so a
    // rollback Undo can only ever revert this command, never the user's previous step.
    function markUndoGroup() {
        app.project.items.addFolder("__claude_undo_marker").remove();
    }

    function handle(f) {
        var id = f.name.replace(/\.cmd\.json$/, "");
        var text = readText(f);
        f.remove();
        var res = { id: id }, t0 = now(), opName = "?", grouped = false;
        try {
            var cmd = parse(text);
            opName = cmd.op;
            if (!OPS.hasOwnProperty(cmd.op)) fail("Unknown op: " + cmd.op);
            var op = OPS[cmd.op];
            beat(true, cmd.op);
            if (op.mutates) {
                app.beginUndoGroup("Claude: " + cmd.op);
                grouped = true;
                markUndoGroup();
            }
            app.beginSuppressDialogs();
            try {
                res.result = op.fn(cmd.args || {});
            } finally {
                app.endSuppressDialogs(false);
                if (grouped) app.endUndoGroup();
            }
            res.ok = true;
        } catch (e) {
            res.ok = false;
            res.error = errText(e);
            // All or nothing: drop whatever the failed command had already changed.
            if (grouped) {
                try {
                    app.executeCommand(UNDO_COMMAND);
                    res.error += " [Rolled back: nothing from this command was kept.]";
                } catch (e2) {
                    res.error += " [This command may have partly applied; Edit > Undo \"Claude: " + opName + "\" reverts it.]";
                }
            }
        }
        res.ms = now() - t0;
        writeText(spool.fsName + "/" + id + ".res.json", ser(res, 0));
        CB.handled++;
        log(opName + (res.ok ? "  ok " : "  FAILED: " + res.error) + "  (" + res.ms + " ms)");
    }

    CB.tick = function () {
        if (CB.busy) return;
        CB.busy = true;
        try {
            var files = spool.getFiles("*.cmd.json");
            files.sort(function (x, y) { return x.name < y.name ? -1 : (x.name > y.name ? 1 : 0); });
            for (var i = 0; i < files.length; i++) handle(files[i]);
            if (files.length || now() - CB.lastBeat > BEAT_MS) beat(false);
        } catch (e) {
            log("tick error: " + errText(e));
        }
        CB.busy = false;
    };

    CB.start = function () {
        if (CB.taskId) return;
        if (!spool.exists) spool.create();
        try {
            beat(false);
        } catch (e) {
            alert("Claude Bridge cannot write files.\n\nEnable Edit > Preferences > Scripting & Expressions > " +
                "'Allow Scripts to Write Files and Access Network', then press Start.\n\n" + errText(e));
            refreshUI();
            return;
        }
        CB.taskId = app.scheduleTask("ClaudeBridge.tick();", TICK_MS, true);
        log("started, spool " + spool.fsName);
    };

    CB.stop = function () {
        if (CB.taskId) {
            app.cancelTask(CB.taskId);
            CB.taskId = null;
            log("stopped");
        }
        refreshUI();
    };

    // Reuses the docked panel when it is still valid (after a reload it may not be).
    function buildUI(useHost) {
        if (G.__ClaudeBridgeNoUI) return;
        var w = null;
        if (useHost && host) {
            try {
                if (host instanceof Panel || host instanceof Window) {
                    w = host;
                    while (w.children.length) w.remove(w.children[0]);
                }
            } catch (e) {
                w = null;
            }
        }
        if (!w) w = new Window("palette", "Claude Bridge", undefined, { resizeable: true });
        w.orientation = "column";
        w.alignChildren = ["fill", "top"];
        w.spacing = 6;
        w.margins = 8;
        var status = w.add("statictext", undefined, "");
        status.characters = 36;
        var row = w.add("group");
        var bStart = row.add("button", undefined, "Start");
        var bStop = row.add("button", undefined, "Stop");
        var logBox = w.add("edittext", undefined, "", { multiline: true, readonly: true, scrolling: true });
        logBox.preferredSize = [340, 180];
        logBox.alignment = ["fill", "fill"];
        bStart.onClick = function () { CB.start(); };
        bStop.onClick = function () { CB.stop(); };
        w.onResizing = w.onResize = function () { this.layout.resize(); };
        CB.ui = { win: w, status: status, log: logBox };
        if (w instanceof Window && !w.visible) { w.center(); w.show(); } else { w.layout.layout(true); }
        refreshUI();
    }

    // Helpers for ae_eval scripts.
    CB.ops = OPS;
    CB.json = { stringify: function (v) { return ser(v, 0); }, parse: parse };
    CB.findComp = findComp;
    CB.findLayer = findLayer;
    CB.resolveProp = resolveProp;
    CB.setValue = setValue;
    CB.easeSegment = easeSegment;
    CB.toColor = toColor;
    CB.layerSummary = layerSummary;
    CB._test = { quote: quote, ser: ser, parse: parse, bezierToAE: bezierToAE, easeSpec: easeSpec, toColor: toColor, rgbHex: rgbHex, EASES: EASES };

    // Take over only now that everything above loaded: a broken edit leaves the running bridge alone.
    var previous = G.ClaudeBridge;
    if (previous && previous.stop) {
        try { previous.stop(); } catch (e1) {}
    }
    G.ClaudeBridge = CB;
    if (!G.__ClaudeBridgeNoStart) CB.start();
    try {
        buildUI(true);
    } catch (e2) {
        try { buildUI(false); } catch (e3) {}
    }
})($.global.__ClaudeBridgeHost || this);
