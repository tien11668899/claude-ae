# Dumps the structure of an After Effects .aep (RIFX) without After Effects:
# items, layers, property/effect tree (matchNames), static values, keyframe counts, expressions.
param([string]$Path, [string]$Out)

$b = [IO.File]::ReadAllBytes($Path)
$sb = New-Object System.Text.StringBuilder
$utf8 = New-Object System.Text.UTF8Encoding $false
$skipGroups = @('ADBE Material Options Group', 'ADBE Audio Group', 'ADBE Plane Options Group', 'ADBE Extrsn Options Group',
                'ADBE Layer Styles', 'ADBE Layer Sets', 'ADBE Envir Appear in Reflect')

function U32([int]$o) { ([uint32]$b[$o] -shl 24) -bor ([uint32]$b[$o + 1] -shl 16) -bor ([uint32]$b[$o + 2] -shl 8) -bor [uint32]$b[$o + 3] }
function Id([int]$o) { [Text.Encoding]::ASCII.GetString($b, $o, 4) }
function Dbl([int]$o) { $t = New-Object byte[] 8; [Array]::Copy($b, $o, $t, 0, 8); [Array]::Reverse($t); [BitConverter]::ToDouble($t, 0) }
function Str([int]$o, [int]$n) { $utf8.GetString($b, $o, $n).TrimEnd([char]0) }
function Line([int]$d, [string]$s) { [void]$sb.AppendLine(('  ' * $d) + $s) }

# Walks the chunks in [start, end). $label names the LIST we are inside (from the preceding tdmn).
function Walk([int]$start, [int]$end, [int]$depth) {
    $o = $start; $pending = $null
    while ($o + 8 -le $end) {
        $id = Id $o; $size = [int](U32 ($o + 4)); $data = $o + 8
        if ($data + $size -gt $end) { break }
        switch ($id) {
            'LIST' {
                $type = Id $data
                $name = $pending; $pending = $null
                if ($type -eq 'btdk') { break }
                if ($name -and ($skipGroups -contains $name)) { break }
                switch ($type) {
                    'Item' { Line $depth ('ITEM ' + (ItemName ($data + 4) ($data + $size))); Walk ($data + 4) ($data + $size) ($depth + 1) }
                    'Layr' { Line $depth ('LAYER "' + (FirstUtf8 ($data + 4) ($data + $size)) + '"'); Walk ($data + 4) ($data + $size) ($depth + 1) }
                    'tdgp' { if ($name) { Line $depth ('[' + $name + ']') }; Walk ($data + 4) ($data + $size) ($depth + 1) }
                    'sspc' { Line $depth ('EFFECT ' + $name + ' "' + (FirstUtf8 ($data + 4) ($data + $size)) + '"'); Walk ($data + 4) ($data + $size) ($depth + 1) }
                    'tdbs' { Prop $name ($data + 4) ($data + $size) $depth }
                    'parT' { }
                    'list' { }
                    default { Walk ($data + 4) ($data + $size) $depth }
                }
            }
            'tdmn' { $pending = Str $data 40; if ($pending -eq 'ADBE Group End') { $pending = $null } }
            default { }
        }
        $o = $data + $size + ($size % 2)
    }
}

function FirstUtf8([int]$start, [int]$end) {
    $o = $start
    while ($o + 8 -le $end) {
        $id = Id $o; $size = [int](U32 ($o + 4))
        if ($id -eq 'Utf8') { return (Str ($o + 8) $size) }
        if ($id -eq 'LIST' -and (Id ($o + 8)) -eq 'fnam') { return (FirstUtf8 ($o + 12) ($o + 8 + $size)) }
        $o = $o + 8 + $size + ($size % 2)
    }
    return ''
}

function ItemName([int]$start, [int]$end) {
    $name = FirstUtf8 $start $end; $type = '?'
    $o = $start
    while ($o + 8 -le $end) {
        $id = Id $o; $size = [int](U32 ($o + 4))
        if ($id -eq 'idta') { $t = ([int]$b[$o + 8] -shl 8) -bor $b[$o + 9]; $type = @{ 1 = 'folder'; 4 = 'comp'; 7 = 'footage' }[$t]; if (-not $type) { $type = "type$t" } }
        if ($id -eq 'cdta' -and $size -ge 160) { $w = ([int]$b[$o + 8 + 140] -shl 8) -bor $b[$o + 8 + 141]; $h = ([int]$b[$o + 8 + 142] -shl 8) -bor $b[$o + 8 + 143]; $type += " ${w}x${h}" }
        $o = $o + 8 + $size + ($size % 2)
    }
    return "`"$name`" ($type)"
}

function Prop([string]$name, [int]$start, [int]$end, [int]$depth) {
    $vals = $null; $expr = $null; $keys = 0; $label = $null
    $o = $start
    while ($o + 8 -le $end) {
        $id = Id $o; $size = [int](U32 ($o + 4)); $d = $o + 8
        switch ($id) {
            'cdat' { $n = [Math]::Min([Math]::Floor($size / 8), 4); $vals = @(for ($i = 0; $i -lt $n; $i++) { [Math]::Round((Dbl ($d + 8 * $i)), 3) }) -join ', ' }
            'Utf8' { if ($size -gt 0) { $expr = Str $d $size } }
            'LIST' {
                $lt = Id $d
                if ($lt -eq 'list') {
                    $q = $d + 4
                    while ($q + 8 -le $d + $size) { if ((Id $q) -eq 'lhd3') { $keys = ([int]$b[$q + 8 + 10] -shl 8) -bor $b[$q + 8 + 11] }; $s2 = [int](U32 ($q + 4)); $q = $q + 8 + $s2 + ($s2 % 2) }
                }
                if ($lt -eq 'tdsn') { $label = FirstUtf8 ($d + 4) ($d + $size) }
            }
        }
        $o = $d + $size + ($size % 2)
    }
    $s = $name
    if ($label -and $label -ne '-_0_/-') { $s += " `"$label`"" }
    if ($keys -gt 1) { $s += " [$keys keys]" } elseif ($vals) { $s += " = $vals" }
    if ($expr) { $s += "`n" + ('  ' * ($depth + 2)) + 'EXPR: ' + ($expr -replace "`r?`n", ' | ') }
    Line $depth $s
}

Walk 12 $b.Length 0
[IO.File]::WriteAllText($Out, $sb.ToString(), $utf8)
"lines: " + ($sb.ToString() -split "`n").Count
