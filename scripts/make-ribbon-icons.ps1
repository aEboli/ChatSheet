# Generate 64x64 PNG icons for the ChatSheet ribbon shortcut buttons.
#
# Same size and near-black ink as PanelLogo.png. Stroke width is lucide's 2/24
# scaled to 64px. Office downscales large-button images to 32px (64 at higher
# DPI), so the source is drawn at 64. Geometry lives only in this script.
#
# Run with Windows PowerShell 5.1 (System.Drawing). pwsh 7 has no GDI+ by default:
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\make-ribbon-icons.ps1

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$outDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\ChatSheet.AddIn\Resources'
if (-not (Test-Path -LiteralPath $outDir)) {
    throw "missing resource dir: $outDir"
}

# ASCII-only C#: Windows PowerShell 5.1 compiles Add-Type as ANSI, so Chinese
# comments inside the C# source become invalid tokens.
$source = @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

public static class RibbonIcons
{
    static readonly Color Ink = Color.FromArgb(255, 17, 24, 23);
    const int Size = 64;
    const float Stroke = 5.3f;

    public static void WriteAll(string dir)
    {
        Write(dir, "RibbonSettings.png", DrawSettings);
        Write(dir, "RibbonDiagnose.png", DrawDiagnose);
        Write(dir, "RibbonFit.png", DrawFit);
        Write(dir, "RibbonUndo.png", DrawUndo);
    }

    static void Write(string dir, string name, Action<Graphics> draw)
    {
        using (var bmp = new Bitmap(Size, Size, PixelFormat.Format32bppArgb))
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.Clear(Color.Transparent);
            draw(g);
            bmp.Save(System.IO.Path.Combine(dir, name), ImageFormat.Png);
        }
    }

    static Pen MakePen()
    {
        var pen = new Pen(Ink, Stroke);
        pen.StartCap = LineCap.Round;
        pen.EndCap = LineCap.Round;
        pen.LineJoin = LineJoin.Round;
        return pen;
    }

    // Gear: eight teeth plus inner ring. Matches the settings tab glyph.
    static void DrawSettings(Graphics g)
    {
        const float cx = 32f, cy = 32f;
        const int teeth = 8;
        const float outer = 24f, inner = 15.6f, hole = 8.2f;
        double step = Math.PI * 2.0 / teeth;
        double tooth = step * 0.42;

        var pts = new PointF[teeth * 4];
        for (int i = 0; i < teeth; i++)
        {
            double mid = -Math.PI / 2.0 + i * step;
            pts[i * 4 + 0] = Polar(cx, cy, inner, mid - step / 2.0 + (step - tooth) / 2.0);
            pts[i * 4 + 1] = Polar(cx, cy, outer, mid - tooth / 2.0);
            pts[i * 4 + 2] = Polar(cx, cy, outer, mid + tooth / 2.0);
            pts[i * 4 + 3] = Polar(cx, cy, inner, mid + step / 2.0 - (step - tooth) / 2.0);
        }

        using (var pen = MakePen())
        using (var path = new GraphicsPath())
        {
            path.AddPolygon(pts);
            g.DrawPath(pen, path);
            g.DrawEllipse(pen, cx - hole, cy - hole, hole * 2f, hole * 2f);
        }
    }

    // Pulse line: "is the environment healthy" rather than a clipboard.
    static void DrawDiagnose(Graphics g)
    {
        var pts = new[]
        {
            new PointF(7f, 36f),
            new PointF(18f, 36f),
            new PointF(24f, 14f),
            new PointF(32f, 50f),
            new PointF(40f, 22f),
            new PointF(46f, 36f),
            new PointF(57f, 36f),
        };

        using (var pen = MakePen())
        {
            g.DrawLines(pen, pts);
        }
    }

    // Four corners stretching out: same metaphor as the panel fit button.
    static void DrawFit(Graphics g)
    {
        const float inset = 11f;
        const float arm = 14f;
        const float radius = 6f;

        using (var pen = MakePen())
        {
            DrawCorner(g, pen, inset, inset, arm, radius, 180);
            DrawCorner(g, pen, Size - inset, inset, arm, radius, 270);
            DrawCorner(g, pen, inset, Size - inset, arm, radius, 90);
            DrawCorner(g, pen, Size - inset, Size - inset, arm, radius, 0);
        }
    }

    static void DrawCorner(Graphics g, Pen pen, float x, float y, float arm, float radius, int startDeg)
    {
        using (var path = new GraphicsPath())
        {
            if (startDeg == 180)
            {
                path.AddLine(x + arm, y, x + radius, y);
                path.AddArc(x, y, radius * 2f, radius * 2f, 270, -90);
                path.AddLine(x, y + radius, x, y + arm);
            }
            else if (startDeg == 270)
            {
                path.AddLine(x - arm, y, x - radius, y);
                path.AddArc(x - radius * 2f, y, radius * 2f, radius * 2f, 270, 90);
                path.AddLine(x, y + radius, x, y + arm);
            }
            else if (startDeg == 90)
            {
                path.AddLine(x + arm, y, x + radius, y);
                path.AddArc(x, y - radius * 2f, radius * 2f, radius * 2f, 90, 90);
                path.AddLine(x, y - radius, x, y - arm);
            }
            else
            {
                path.AddLine(x - arm, y, x - radius, y);
                path.AddArc(x - radius * 2f, y - radius * 2f, radius * 2f, radius * 2f, 90, -90);
                path.AddLine(x, y - radius, x, y - arm);
            }

            g.DrawPath(pen, path);
        }
    }

    // Undo: arrow curving back to the left, same idea as lucide undo-2.
    // Points left so it reads as "go back", opposite of a redo glyph.
    static void DrawUndo(Graphics g)
    {
        using (var pen = MakePen())
        {
            // Shaft: left tip, right along the top, half circle down, back left.
            // Sized to fill the canvas like the other three; a smaller glyph
            // reads as visually lighter than its neighbours at 32px.
            using (var path = new GraphicsPath())
            {
                path.AddLine(9f, 24f, 36f, 24f);
                // Right half circle: top (36,24) -> right (51,39) -> bottom (36,54).
                path.AddArc(21f, 24f, 30f, 30f, 270f, 180f);
                path.AddLine(36f, 54f, 26f, 54f);
                g.DrawPath(pen, path);
            }

            // Arrowhead: a V opening right, apex at the shaft's left tip,
            // so the arrow points left and reads as "go back".
            using (var head = new GraphicsPath())
            {
                head.AddLine(22f, 10f, 9f, 24f);
                head.AddLine(9f, 24f, 22f, 38f);
                g.DrawPath(pen, head);
            }
        }
    }

    static PointF Polar(float cx, float cy, float r, double a)
    {
        return new PointF(cx + r * (float)Math.Cos(a), cy + r * (float)Math.Sin(a));
    }
}
'@

Add-Type -TypeDefinition $source -ReferencedAssemblies System.Drawing

[RibbonIcons]::WriteAll($outDir)

Add-Type -AssemblyName System.Drawing
Get-ChildItem -LiteralPath $outDir -Filter 'Ribbon*.png' | ForEach-Object {
    $img = [System.Drawing.Image]::FromFile($_.FullName)
    try {
        '{0}  {1}x{2}  {3}' -f $_.Name, $img.Width, $img.Height, $img.PixelFormat
    }
    finally {
        $img.Dispose()
    }
}
