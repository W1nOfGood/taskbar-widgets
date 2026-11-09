using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace TaskbarWidgets
{
    /// <summary>Build-time tool: renders the app icons into multi-size PNG .ico files.</summary>
    internal static class IconGen
    {
        static void Main(string[] args)
        {
            string dir = args.Length > 0 ? args[0] : ".";
            Write(Path.Combine(dir, "battery.ico"), Glyph.Headphones, true);
            Write(Path.Combine(dir, "media.ico"), Glyph.Volume, false);
        }

        static void Write(string path, Glyph glyph, bool battery)
        {
            var sizes = new[] { 16, 24, 32, 48, 256 };
            var pngs = new List<byte[]>();
            foreach (int s in sizes)
            {
                using (var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.Clear(Color.Transparent);
                        float m = s * 0.04f;
                        Gfx.Fill(g, new RectangleF(m, m, s - 2 * m, s - 2 * m), s * 0.22f, Color.FromArgb(168, 108, 62));
                        if (battery)
                        {
                            Gfx.Icon(g, glyph, new RectangleF(s * 0.16f, s * 0.1f, s * 0.68f, s * 0.5f), Color.White, 1);
                            Gfx.Battery(g, new RectangleF(s * 0.22f, s * 0.64f, s * 0.56f, s * 0.22f), 70, Color.FromArgb(34, 197, 140), Color.White, false, false);
                        }
                        else Gfx.Icon(g, glyph, new RectangleF(s * 0.18f, s * 0.18f, s * 0.64f, s * 0.64f), Color.White, 1);
                    }
                    using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); pngs.Add(ms.ToArray()); }
                    if (s == 256) bmp.Save(Path.ChangeExtension(path, ".png"), ImageFormat.Png); // for the README
                }
            }
            using (var fs = File.Create(path))
            using (var w = new BinaryWriter(fs))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
                    w.Write(pngs[i].Length); w.Write(offset);
                    offset += pngs[i].Length;
                }
                foreach (var p in pngs) w.Write(p);
            }
        }
    }
}
