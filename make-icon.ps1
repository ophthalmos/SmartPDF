# Erzeugt MozillaPDF.ico aus dem PDF.js-Logo (pdfjs-logo.svg, aus dem PDF.js-Projekt, Apache-Lizenz 2.0).
# Jede Größe wird einzeln von Edge im Headless-Modus aus dem SVG gerendert (transparent) – so bleibt das Logo auch in 16 px scharf,
# statt aus einem großen Bild heruntergerechnet zu werden. Einträge: 256 px als PNG, kleinere als 32-Bit-DIB, weil System.Drawing.Icon
# und damit WinForms PNG-Einträge unter 256 px nicht zuverlässig lesen.
# Danach das Icon auch in Forms\MainForm.resx ($this.Icon) erneuern.
param(
    [string]$Svg = (Join-Path $PSScriptRoot "pdfjs-logo.svg"),
    [string]$Out = (Join-Path $PSScriptRoot "MozillaPDF.ico"),
    [string]$Preview = ""
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw "Microsoft Edge nicht gefunden – er rendert das SVG." }
$work = Join-Path $env:TEMP "MozillaPDF-icon"
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory $work | Out-Null
Copy-Item $Svg (Join-Path $work "logo.svg")

function Render([int]$s) {
    # Größerer Viewport als das Bild (Headless-Fenster haben eine Mindestgröße), danach links oben auf s×s zuschneiden
    $page = Join-Path $work "page-$s.html"; $png = Join-Path $work "shot-$s.png"
    Set-Content $page "<!doctype html><html><head><style>html,body{margin:0;background:transparent}img{display:block;width:${s}px;height:${s}px}</style></head><body><img src='logo.svg'></body></html>" -Encoding UTF8
    $args = @("--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check", "--user-data-dir=`"$work\profile`"",
              "--hide-scrollbars", "--force-device-scale-factor=1", "--default-background-color=00000000", "--window-size=400,400",
              "--screenshot=`"$png`"", "`"file:///$($page.Replace('\', '/'))`"")
    Start-Process $edge -ArgumentList $args -Wait -WindowStyle Hidden
    if (-not (Test-Path $png)) { throw "Edge hat für $s px kein Bild geliefert." }
    $shot = [System.Drawing.Image]::FromFile($png)
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.DrawImage($shot, (New-Object System.Drawing.Rectangle 0, 0, $s, $s), (New-Object System.Drawing.Rectangle 0, 0, $s, $s), [System.Drawing.GraphicsUnit]::Pixel); $g.Dispose()
    $shot.Dispose()
    $bmp
}

function Entry($bmp, [int]$s) {
    $ms = New-Object System.IO.MemoryStream
    if ($s -ge 256) { $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); return ,$ms.ToArray() }
    $bw = New-Object System.IO.BinaryWriter $ms
    $bw.Write([uint32]40); $bw.Write([int32]$s); $bw.Write([int32](2 * $s)); $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]0); $bw.Write([uint32]0); $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)
    for ($y = $s - 1; $y -ge 0; $y--) { for ($x = 0; $x -lt $s; $x++) { $c = $bmp.GetPixel($x, $y); $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A) } }
    $maskRow = [int]([Math]::Ceiling($s / 32.0) * 4)
    for ($y = $s - 1; $y -ge 0; $y--) { $row = New-Object byte[] $maskRow; for ($x = 0; $x -lt $s; $x++) { if ($bmp.GetPixel($x, $y).A -eq 0) { $row[[int][Math]::Floor($x / 8)] = $row[[int][Math]::Floor($x / 8)] -bor (0x80 -shr ($x % 8)) } }; $bw.Write($row) }
    $bw.Flush(); ,$ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$entries = foreach ($s in $sizes) { $bmp = Render $s; if ($Preview -and $s -eq 256) { $bmp.Save($Preview) }; ,(Entry $bmp $s); $bmp.Dispose() }
$fs = [System.IO.File]::Create($Out); $w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$entries[$i].Length); $w.Write([uint32]$offset)
    $offset += $entries[$i].Length
}
foreach ($e in $entries) { $w.Write($e) }
$w.Dispose(); $fs.Dispose()
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
"Icon geschrieben: $Out ($((Get-Item $Out).Length) Byte, Größen: $($sizes -join ', '))"
