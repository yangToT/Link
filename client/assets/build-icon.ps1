$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
# Original Link mark: a rounded L and a separate endpoint, drawn as vectors at every size.
$frames = foreach ($size in @(16,20,24,32,40,48,64,128,256)) {
    $bitmap = New-Object System.Drawing.Bitmap($size,$size)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'
    $g.ScaleTransform(($size / 256.0),($size / 256.0))
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(8,8,88,88,180,90); $path.AddArc(160,8,88,88,270,90)
    $path.AddArc(160,160,88,88,0,90); $path.AddArc(8,160,88,88,90,90); $path.CloseFigure()
    $background = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml('#226D5B'))
    $g.FillPath($background,$path)
    $pen = New-Object System.Drawing.Pen([System.Drawing.ColorTranslator]::FromHtml('#F6FAF8'),27)
    $pen.StartCap='Round'; $pen.EndCap='Round'; $pen.LineJoin='Round'
    $mark = New-Object System.Drawing.Drawing2D.GraphicsPath
    $mark.AddLine(80,70,80,168); $mark.AddArc(80,148,40,40,180,-90); $mark.AddLine(100,188,175,188)
    $g.DrawPath($pen,$mark)
    $dot = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml('#A7DCC5'))
    $g.FillEllipse($dot,152,52,42,42)
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream,[System.Drawing.Imaging.ImageFormat]::Png)
    [pscustomobject]@{ Size=$size; Bytes=$stream.ToArray() }
    $stream.Dispose(); $dot.Dispose(); $mark.Dispose(); $pen.Dispose(); $background.Dispose(); $path.Dispose(); $g.Dispose(); $bitmap.Dispose()
}
$file = [System.IO.File]::Create((Join-Path $PSScriptRoot 'Link.ico'))
$writer = New-Object System.IO.BinaryWriter($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([uint16]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
} finally { $writer.Dispose(); $file.Dispose() }
