# Aurora static first-frame values (same algorithm as AuroraTokenDictionary.cs). ASCII only.
function ToHsl([double]$r, [double]$g, [double]$b) {
    $r = $r / 255.0; $g = $g / 255.0; $b = $b / 255.0
    $max = [Math]::Max($r, [Math]::Max($g, $b))
    $min = [Math]::Min($r, [Math]::Min($g, $b))
    $l = ($max + $min) / 2.0
    $d = $max - $min
    if ($d -le 0) { return @(-1.0, 0.0, $l) }
    if ($l -gt 0.5) { $s = $d / (2.0 - $max - $min) } else { $s = $d / ($max + $min) }
    if ($max -eq $r) { $h = ($g - $b) / $d } elseif ($g -lt $b) { $h = ($g - $b) / $d + 6.0 } else { $h = ($g - $b) / $d }
    if ($max -eq $g) { $h = ($b - $r) / $d + 2.0 }
    if ($max -eq $b) { $h = ($r - $g) / $d + 4.0 }
    return @(($h * 60.0), $s, $l)
}

function HueToRgb([double]$p, [double]$q, [double]$t) {
    if ($t -lt 0) { $t += 1.0 }
    if ($t -gt 1) { $t -= 1.0 }
    if ($t -lt (1.0 / 6.0)) { return $p + ($q - $p) * 6.0 * $t }
    if ($t -lt 0.5) { return $q }
    if ($t -lt (2.0 / 3.0)) { return $p + ($q - $p) * (2.0 / 3.0 - $t) * 6.0 }
    return $p
}

function FromHsl([double]$h, [double]$s, [double]$l) {
    $h = (($h % 360) + 360) % 360
    if ($s -le 0) {
        $v = [int][Math]::Round($l * 255.0)
        return @($v, $v, $v)
    }
    if ($l -lt 0.5) { $q = $l * (1.0 + $s) } else { $q = $l + $s - $l * $s }
    $p = 2.0 * $l - $q
    $r = [int][Math]::Round((HueToRgb $p $q ($h / 360.0 + 1.0 / 3.0)) * 255.0)
    $g = [int][Math]::Round((HueToRgb $p $q ($h / 360.0)) * 255.0)
    $b = [int][Math]::Round((HueToRgb $p $q ($h / 360.0 - 1.0 / 3.0)) * 255.0)
    return @($r, $g, $b)
}

function Mix([object]$a, [object]$b, [double]$t) {
    $r = [int][Math]::Round($a[0] + ($b[0] - $a[0]) * $t)
    $g = [int][Math]::Round($a[1] + ($b[1] - $a[1]) * $t)
    $b2 = [int][Math]::Round($a[2] + ($b[2] - $a[2]) * $t)
    return @($r, $g, $b2)
}

function ToRgb([object]$c) { return ('#{0:X2}{1:X2}{2:X2}' -f $c[0], $c[1], $c[2]) }
function ToArgb([object]$c, [int]$a) { return ('#{0:X2}{1:X2}{2:X2}{3:X2}' -f $a, $c[0], $c[1], $c[2]) }

function ParseHex([string]$hex) {
    $h = $hex.TrimStart('#')
    return @([Convert]::ToInt32($h.Substring(2, 2), 16), [Convert]::ToInt32($h.Substring(4, 2), 16), [Convert]::ToInt32($h.Substring(6, 2), 16))
}

$white = @(255, 255, 255)
foreach ($mode in @('dark', 'light')) {
    if ($mode -eq 'dark') {
        $accentHex = '#FF2FD4B4'; $glassBase = @(0x1E, 0x20, 0x27); $k = 0.10
        $topA = 0xD9; $midA = 0xC2; $botA = 0xA6
    } else {
        $accentHex = '#FF1FA88C'; $glassBase = @(0xE9, 0xED, 0xF3); $k = 0.06
        $topA = 0xE8; $midA = 0xDC; $botA = 0xCC
    }
    $accent = ParseHex $accentHex

    $base = Mix $glassBase $accent $k
    $hsl = ToHsl $base[0] $base[1] $base[2]
    $top = FromHsl $hsl[0] $hsl[1] ([Math]::Min(1.0, $hsl[2] + 0.08))
    $bot = FromHsl $hsl[0] $hsl[1] ([Math]::Max(0.0, $hsl[2] - 0.07))
    Write-Host ("{0}: Glass {1}(0) -> {2}{3:X2}(0.55) -> {4}{5:X2}(1)" -f $mode, (ToArgb $top $topA), (ToRgb $base), $midA, (ToRgb $bot), $botA)

    $ah = ToHsl $accent[0] $accent[1] $accent[2]
    $s0 = FromHsl $ah[0] $ah[1] $ah[2]
    $s1 = FromHsl ($ah[0] + 38.0) $ah[1] $ah[2]
    $s2 = FromHsl ($ah[0] + 95.0) $ah[1] $ah[2]
    Write-Host ("{0}: Hairline {1} -> {2} -> {3}  (H={4:F1} S={5:F3} L={6:F3})" -f $mode, (ToRgb $s0), (ToRgb $s1), (ToRgb $s2), $ah[0], $ah[1], $ah[2])

    $glow = Mix $white $accent 0.18
    Write-Host ("{0}: InnerGlow {1}" -f $mode, (ToArgb $glow 0x66))

    $soft = FromHsl $ah[0] $ah[1] ([Math]::Min(1.0, $ah[2] + 0.08))
    $textOn = '#FFFFFFFF'
    if ($ah[2] -gt 0.55) { $textOn = '#FF0F1214' }
    Write-Host ("{0}: AurAccentSoft {1}  AurAccentText {2}  (L={3:F3})" -f $mode, (ToArgb $soft 0xFF), $textOn, $ah[2])
}
