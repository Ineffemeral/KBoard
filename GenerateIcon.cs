// GenerateIcon.cs — Produces Kboard.ico (multi-resolution, PNG-in-ICO)
// Compile: csc /target:exe /r:System.Drawing.dll GenerateIcon.cs
// Run:     GenerateIcon.exe

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;

class GenerateIcon
{
    static void Main(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : "Kboard.ico";

        int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
        byte[][] pngBlobs = new byte[sizes.Length][];

        for (int i = 0; i < sizes.Length; i++)
            pngBlobs[i] = RenderToPng(sizes[i]);

        WriteIco(outPath, sizes, pngBlobs);
        Console.WriteLine("Written: " + outPath);
    }

    static byte[] RenderToPng(int size)
    {
        using (Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode      = SmoothingMode.AntiAlias;
            g.InterpolationMode  = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode    = PixelOffsetMode.HighQuality;
            g.TextRenderingHint  = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            float radius = size * 0.18f;  // rounded corner radius

            // ── Background: radial gradient from lighter-purple center to deeper-purple edge ──
            using (GraphicsPath bg = RoundedRect(0, 0, size, size, radius))
            {
                // Fake radial: two-pass. Solid base first, then an elliptical highlight overlay.
                Color deepPurple  = Color.FromArgb(255, 74,  58, 180);  // #4A3AB4
                Color midPurple   = Color.FromArgb(255, 106, 90, 205);  // #6A5ACD
                Color lightPurple = Color.FromArgb(255, 138,122, 232);  // #8A7AE8

                using (LinearGradientBrush fill = new LinearGradientBrush(
                    new PointF(0, 0), new PointF(size, size),
                    lightPurple, deepPurple))
                {
                    fill.SetSigmaBellShape(0.5f);
                    g.FillPath(fill, bg);
                }

                // Soft inner highlight (top-left quadrant glow)
                if (size >= 32)
                {
                    using (GraphicsPath oval = new GraphicsPath())
                    {
                        float hw = size * 0.7f;
                        float hh = size * 0.6f;
                        oval.AddEllipse(-size * 0.1f, -size * 0.2f, hw, hh);
                        using (PathGradientBrush glow = new PathGradientBrush(oval))
                        {
                            glow.CenterColor    = Color.FromArgb(60, 255, 255, 255);
                            glow.SurroundColors = new[] { Color.Transparent };
                            g.FillPath(glow, bg);
                        }
                    }
                }

                // Subtle 1px inner border (white, low alpha) for polish
                if (size >= 24)
                {
                    using (Pen border = new Pen(Color.FromArgb(55, 255, 255, 255), size >= 64 ? 1.5f : 1f))
                    {
                        g.DrawPath(border, bg);
                    }
                }
            }

            // ── Glyph: "中" centered ──
            // Pick the best available Chinese font
            string fontName = BestChineseFont();

            // Scale the glyph so it occupies ~62% of the icon height
            float emSize = size * 0.62f;

            // Very small sizes: bump up slightly for legibility
            if (size <= 16) emSize = size * 0.72f;
            else if (size <= 24) emSize = size * 0.66f;

            using (Font font = new Font(fontName, emSize, FontStyle.Bold, GraphicsUnit.Pixel))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment     = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;

                RectangleF rc = new RectangleF(0, size * 0.04f, size, size);  // tiny nudge down

                // Drop shadow at larger sizes
                if (size >= 48)
                {
                    using (Brush shadow = new SolidBrush(Color.FromArgb(70, 30, 10, 80)))
                    {
                        float d = size * 0.025f;
                        g.DrawString("中", font, shadow,
                            new RectangleF(d, size * 0.04f + d, size, size), sf);
                    }
                }

                using (Brush white = new SolidBrush(Color.White))
                    g.DrawString("中", font, white, rc, sf);
            }

            using (MemoryStream ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }
    }

    static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        GraphicsPath p = new GraphicsPath();
        p.AddArc(x, y, r * 2, r * 2, 180, 90);
        p.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
        p.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
        p.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
        p.CloseFigure();
        return p;
    }

    static string BestChineseFont()
    {
        string[] candidates = { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "NSimSun", "Segoe UI" };
        using (InstalledFontCollection ifc = new InstalledFontCollection())
        {
            var installed = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ff in ifc.Families) installed.Add(ff.Name);
            foreach (var c in candidates)
                if (installed.Contains(c)) return c;
        }
        return "Segoe UI";
    }

    // Writes a PNG-in-ICO file (Vista+ format — no BMP re-encoding, full alpha, crisp)
    static void WriteIco(string path, int[] sizes, byte[][] pngs)
    {
        const int HEADER = 6;
        const int DIR_ENTRY = 16;
        int n = sizes.Length;

        using (BinaryWriter w = new BinaryWriter(File.Create(path)))
        {
            // ICO header
            w.Write((ushort)0);  // reserved
            w.Write((ushort)1);  // type: icon
            w.Write((ushort)n);  // count

            // Calculate offsets
            int dataOffset = HEADER + n * DIR_ENTRY;
            int[] offsets = new int[n];
            offsets[0] = dataOffset;
            for (int i = 1; i < n; i++)
                offsets[i] = offsets[i - 1] + pngs[i - 1].Length;

            // Directory entries
            for (int i = 0; i < n; i++)
            {
                int s = sizes[i];
                w.Write((byte)(s >= 256 ? 0 : s));  // width  (0 = 256)
                w.Write((byte)(s >= 256 ? 0 : s));  // height (0 = 256)
                w.Write((byte)0);   // color count (0 = truecolor)
                w.Write((byte)0);   // reserved
                w.Write((ushort)1); // planes
                w.Write((ushort)32);// bit depth
                w.Write((uint)pngs[i].Length);
                w.Write((uint)offsets[i]);
            }

            // Image data
            foreach (byte[] blob in pngs)
                w.Write(blob);
        }
    }
}
