// Runs ae/ClaudeBridge.jsx under Windows Script Host (JScript is ES3, like
// ExtendScript) with stubbed host objects, and checks its pure helpers.
//   cscript //nologo //E:jscript tests\jsx-test.js ae\ClaudeBridge.jsx

var G = this;
var $ = { global: G, fileName: "", writeln: function () {} };
function Folder(p) { this.fsName = p; this.exists = true; }
Folder.prototype.create = function () { return true; };
function File(p) { this.fsName = p; }
var app = { version: "stub", project: { file: null }, scheduleTask: function () { return 1; }, cancelTask: function () {} };
G.__ClaudeBridgeRoot = "C:/stub";
G.__ClaudeBridgeNoUI = true;
G.__ClaudeBridgeNoStart = true;

function readUtf8(path) {
    var s = new ActiveXObject("ADODB.Stream");
    s.Type = 2;
    s.Charset = "utf-8";
    s.Open();
    s.LoadFromFile(path);
    var t = s.ReadText();
    s.Close();
    return t;
}

var fails = 0;
function check(ok, what) {
    WScript.Echo((ok ? "PASS  " : "FAIL  ") + what);
    if (!ok) fails++;
}
function near(a, b) { return Math.abs(a - b) < 1e-6; }

eval(readUtf8(WScript.Arguments(0)));
var T = ClaudeBridge._test;

// JSON
var src = { a: 1.23456789, s: "line\n\"q\" \u52d5\u614b", arr: [1, [2, 3]], n: null, b: true, big: 1700000000123 };
var text = T.ser(src, 0);
check(/^[\x20-\x7e]*$/.test(text), "serialized JSON is pure ASCII");
var back = T.parse(text);
check(back.s === src.s, "strings survive a round trip");
check(back.a === 1.2346, "numbers are rounded to 4 decimals");
check(back.big === 1700000000123, "large integers (timestamps) keep full precision");
check(back.arr[1][1] === 3 && back.n === null && back.b === true, "arrays, null and booleans survive");

// Easing: out_expo over a 0 -> 800 px move taking 1 s.
var e = T.bezierToAE(T.easeSpec("out_expo"), [800], 1, false);
check(near(e.outInf, 16) && near(e.inInf, 70), "out_expo influences are 16% / 70%");
check(near(e.outSpeed[0], 5000) && near(e.inSpeed[0], 0), "out_expo leaves at 5000 px/s and lands at 0");
var d = T.bezierToAE(T.easeSpec("in_out"), [-100], 0.5, false);
check(near(d.outSpeed[0], 0) && near(d.inSpeed[0], 0), "in_out starts and ends at rest");
var b = T.bezierToAE(T.easeSpec("out_back"), [100], 1, false);
check(b.outSpeed[0] > 100, "out_back leaves faster than average (overshoot)");
var s = T.bezierToAE(T.easeSpec("in_back"), [100], 1, true);
check(s.outSpeed[0] >= 0 && s.inSpeed[0] >= 0, "spatial speeds are never negative");
var neg = T.bezierToAE(T.easeSpec("out"), [-50, 30], 1, false);
check(neg.outSpeed[0] < 0 && neg.outSpeed[1] > 0, "non-spatial speeds keep their sign per dimension");
var threw = false;
try { T.easeSpec("bouncy"); } catch (x) { threw = /Unknown ease/.test(x.message); }
check(threw, "unknown ease names are rejected with a helpful message");

// Colors
var c = T.toColor("#FF8000");
check(near(c[0], 1) && near(c[1], 128 / 255) && near(c[2], 0) && c[3] === 1, "hex colors parse to 0-1 RGBA");
var c255 = T.toColor([255, 0, 0]);
check(near(c255[0], 1), "0-255 arrays are normalized");
check(T.rgbHex([1, 0.5, 0]) === "#ff8000", "colors format back to hex");

WScript.Echo(fails ? fails + " FAILED" : "ALL PASS");
WScript.Quit(fails ? 1 : 0);
