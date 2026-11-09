using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace TaskbarWidgets
{
    internal enum Glyph { Generic, Mouse, Keyboard, Headphones, Earbuds, Speaker, Phone, Gamepad, Laptop, Watch, Pen,
                          Prev, Next, Play, Pause, Volume, Mute, Refresh, Close, Note, Bolt, Chevron, Eye, EyeOff }

    internal static class Gfx
    {
        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            rad = Math.Max(0.1f, Math.Min(rad, Math.Min(r.Width, r.Height) / 2f));
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void Fill(Graphics g, RectangleF r, float rad, Color c)
        {
            using (var p = Round(r, rad)) using (var b = new SolidBrush(c)) g.FillPath(b, p);
        }

        public static void Stroke(Graphics g, RectangleF r, float rad, Color c, float w)
        {
            using (var p = Round(r, rad)) using (var pen = new Pen(c, w)) g.DrawPath(pen, p);
        }

        public static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public static Color Alpha(Color c, int a) { return Color.FromArgb(Math.Max(0, Math.Min(255, a)), c); }

        /// <summary>Battery glyph. level -1 = unknown (empty outline).</summary>
        public static void Battery(Graphics g, RectangleF r, int level, Color fill, Color outline, bool vertical, bool charging)
        {
            float w = Math.Max(1f, r.Height / 11f);
            RectangleF body, cap;
            if (!vertical)
            {
                float capW = Math.Max(1.5f, r.Width * 0.08f);
                body = new RectangleF(r.X, r.Y, r.Width - capW - w * 0.5f, r.Height);
                cap = new RectangleF(body.Right + w * 0.5f, r.Y + r.Height * 0.3f, capW, r.Height * 0.4f);
            }
            else
            {
                float capH = Math.Max(1.5f, r.Height * 0.08f);
                body = new RectangleF(r.X, r.Y + capH + w * 0.5f, r.Width, r.Height - capH - w * 0.5f);
                cap = new RectangleF(r.X + r.Width * 0.3f, r.Y, r.Width * 0.4f, capH);
            }
            float rad = Math.Min(body.Width, body.Height) * 0.22f;
            Stroke(g, Inflate(body, -w / 2), rad, outline, w);
            Fill(g, cap, cap.Width * 0.3f, outline);
            if (level > 0)
            {
                var inner = Inflate(body, -w * 2f);
                float f = Math.Min(100, level) / 100f;
                RectangleF lv = vertical
                    ? new RectangleF(inner.X, inner.Bottom - inner.Height * f, inner.Width, inner.Height * f)
                    : new RectangleF(inner.X, inner.Y, Math.Max(w, inner.Width * f), inner.Height);
                Fill(g, lv, rad * 0.5f, fill);
            }
            if (charging)
            {
                var c = new PointF(body.X + body.Width / 2, body.Y + body.Height / 2);
                float s = Math.Min(body.Width, body.Height) * 0.42f;
                var bolt = new[] { new PointF(c.X + s * 0.15f, c.Y - s), new PointF(c.X - s * 0.55f, c.Y + s * 0.1f), new PointF(c.X, c.Y + s * 0.1f),
                                   new PointF(c.X - s * 0.15f, c.Y + s), new PointF(c.X + s * 0.55f, c.Y - s * 0.1f), new PointF(c.X, c.Y - s * 0.1f) };
                using (var b = new SolidBrush(Color.White)) g.FillPolygon(b, bolt);
                using (var p = new Pen(Color.FromArgb(120, 0, 0, 0), w * 0.5f)) g.DrawPolygon(p, bolt);
            }
        }

        static RectangleF Inflate(RectangleF r, float d) { r.Inflate(d, d); return r; }

        /// <summary>Draws a simple vector icon inside r. style: 0 outline, 1 solid, 2 minimal.</summary>
        public static void Icon(Graphics g, Glyph kind, RectangleF r, Color c, int style)
        {
            float s = Math.Min(r.Width, r.Height);
            var box = new RectangleF(r.X + (r.Width - s) / 2, r.Y + (r.Height - s) / 2, s, s);
            float w = Math.Max(1f, s / (style == 2 ? 14f : 11f));
            bool solid = style == 1;
            var oldSmooth = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(c, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            using (var br = new SolidBrush(c))
            {
                Func<float, float, float, float, RectangleF> R = (x, y, ww, hh) => new RectangleF(box.X + x * s, box.Y + y * s, ww * s, hh * s);
                Func<float, float, PointF> P = (x, y) => new PointF(box.X + x * s, box.Y + y * s);
                switch (kind)
                {
                    case Glyph.Mouse:
                    {
                        var m = R(0.27f, 0.08f, 0.46f, 0.84f);
                        if (solid) { Fill(g, m, m.Width / 2, c); using (var p2 = new Pen(Color.FromArgb(160, 0, 0, 0), w)) g.DrawLine(p2, P(0.5f, 0.18f), P(0.5f, 0.36f)); }
                        else { Stroke(g, m, m.Width / 2, c, w); g.DrawLine(pen, P(0.5f, 0.2f), P(0.5f, 0.36f)); }
                        break;
                    }
                    case Glyph.Keyboard:
                    {
                        var k = R(0.06f, 0.26f, 0.88f, 0.5f);
                        if (solid) Fill(g, k, s * 0.08f, c); else Stroke(g, k, s * 0.08f, c, w);
                        Color kc = solid ? Color.FromArgb(160, 0, 0, 0) : c;
                        using (var kb = new SolidBrush(kc))
                        {
                            for (int i = 0; i < 4; i++) g.FillRectangle(kb, R(0.18f + i * 0.18f, 0.37f, 0.08f, 0.08f));
                            g.FillRectangle(kb, R(0.28f, 0.57f, 0.44f, 0.07f));
                        }
                        break;
                    }
                    case Glyph.Headphones:
                    {
                        g.DrawArc(pen, R(0.14f, 0.12f, 0.72f, 0.72f), 180, 180);
                        var l = R(0.1f, 0.48f, 0.2f, 0.36f); var rr = R(0.7f, 0.48f, 0.2f, 0.36f);
                        if (solid || style == 2) { Fill(g, l, s * 0.06f, c); Fill(g, rr, s * 0.06f, c); }
                        else { Stroke(g, l, s * 0.06f, c, w); Stroke(g, rr, s * 0.06f, c, w); }
                        break;
                    }
                    case Glyph.Earbuds:
                    {
                        var a = R(0.12f, 0.22f, 0.3f, 0.3f); var b = R(0.58f, 0.22f, 0.3f, 0.3f);
                        if (solid) { g.FillEllipse(br, a); g.FillEllipse(br, b); } else { g.DrawEllipse(pen, a); g.DrawEllipse(pen, b); }
                        g.DrawLine(pen, P(0.27f, 0.52f), P(0.27f, 0.84f)); g.DrawLine(pen, P(0.73f, 0.52f), P(0.73f, 0.84f));
                        break;
                    }
                    case Glyph.Speaker:
                    {
                        var b = R(0.24f, 0.06f, 0.52f, 0.88f);
                        if (solid) Fill(g, b, s * 0.1f, c); else Stroke(g, b, s * 0.1f, c, w);
                        Color ic = solid ? Color.FromArgb(160, 0, 0, 0) : c;
                        using (var ip = new Pen(ic, w)) { g.DrawEllipse(ip, R(0.36f, 0.42f, 0.28f, 0.28f)); g.DrawEllipse(ip, R(0.46f, 0.18f, 0.08f, 0.08f)); }
                        break;
                    }
                    case Glyph.Phone:
                    {
                        var b = R(0.28f, 0.06f, 0.44f, 0.88f);
                        if (solid) Fill(g, b, s * 0.09f, c); else Stroke(g, b, s * 0.09f, c, w);
                        using (var ip = new Pen(solid ? Color.FromArgb(160, 0, 0, 0) : c, w) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                            g.DrawLine(ip, P(0.44f, 0.82f), P(0.56f, 0.82f));
                        break;
                    }
                    case Glyph.Gamepad:
                    {
                        var b = R(0.06f, 0.26f, 0.88f, 0.5f);
                        if (solid) Fill(g, b, s * 0.22f, c); else Stroke(g, b, s * 0.22f, c, w);
                        using (var ip = new Pen(solid ? Color.FromArgb(160, 0, 0, 0) : c, w))
                        {
                            ip.StartCap = ip.EndCap = LineCap.Round;
                            g.DrawLine(ip, P(0.22f, 0.51f), P(0.38f, 0.51f)); g.DrawLine(ip, P(0.3f, 0.43f), P(0.3f, 0.59f));
                            g.DrawEllipse(ip, R(0.64f, 0.42f, 0.06f, 0.06f)); g.DrawEllipse(ip, R(0.72f, 0.52f, 0.06f, 0.06f));
                        }
                        break;
                    }
                    case Glyph.Laptop:
                    {
                        var b = R(0.16f, 0.2f, 0.68f, 0.46f);
                        if (solid) Fill(g, b, s * 0.05f, c); else Stroke(g, b, s * 0.05f, c, w);
                        g.DrawLine(pen, P(0.06f, 0.78f), P(0.94f, 0.78f));
                        break;
                    }
                    case Glyph.Watch:
                    {
                        var b = R(0.26f, 0.24f, 0.48f, 0.52f);
                        if (solid) Fill(g, b, s * 0.12f, c); else Stroke(g, b, s * 0.12f, c, w);
                        g.DrawLine(pen, P(0.38f, 0.08f), P(0.62f, 0.08f)); g.DrawLine(pen, P(0.38f, 0.92f), P(0.62f, 0.92f));
                        break;
                    }
                    case Glyph.Pen:
                        g.DrawLine(pen, P(0.22f, 0.78f), P(0.74f, 0.26f));
                        g.DrawLine(pen, P(0.66f, 0.18f), P(0.82f, 0.34f));
                        break;
                    case Glyph.Prev:
                        g.FillPolygon(br, new[] { P(0.78f, 0.22f), P(0.78f, 0.78f), P(0.36f, 0.5f) });
                        g.FillRectangle(br, R(0.22f, 0.22f, 0.1f, 0.56f));
                        break;
                    case Glyph.Next:
                        g.FillPolygon(br, new[] { P(0.22f, 0.22f), P(0.22f, 0.78f), P(0.64f, 0.5f) });
                        g.FillRectangle(br, R(0.68f, 0.22f, 0.1f, 0.56f));
                        break;
                    case Glyph.Play:
                        g.FillPolygon(br, new[] { P(0.3f, 0.18f), P(0.3f, 0.82f), P(0.82f, 0.5f) });
                        break;
                    case Glyph.Pause:
                        Fill(g, R(0.26f, 0.2f, 0.16f, 0.6f), s * 0.03f, c); Fill(g, R(0.58f, 0.2f, 0.16f, 0.6f), s * 0.03f, c);
                        break;
                    case Glyph.Volume:
                    case Glyph.Mute:
                    {
                        var poly = new[] { P(0.1f, 0.38f), P(0.26f, 0.38f), P(0.48f, 0.18f), P(0.48f, 0.82f), P(0.26f, 0.62f), P(0.1f, 0.62f) };
                        if (solid || style == 2) g.FillPolygon(br, poly); else g.DrawPolygon(pen, poly);
                        if (kind == Glyph.Volume)
                        {
                            g.DrawArc(pen, R(0.42f, 0.32f, 0.22f, 0.36f), -55, 110);
                            g.DrawArc(pen, R(0.42f, 0.18f, 0.42f, 0.64f), -55, 110);
                        }
                        else
                        {
                            g.DrawLine(pen, P(0.62f, 0.36f), P(0.88f, 0.64f)); g.DrawLine(pen, P(0.88f, 0.36f), P(0.62f, 0.64f));
                        }
                        break;
                    }
                    case Glyph.Refresh:
                        g.DrawArc(pen, R(0.2f, 0.2f, 0.6f, 0.6f), -60, 300);
                        g.FillPolygon(br, new[] { P(0.62f, 0.08f), P(0.84f, 0.26f), P(0.58f, 0.34f) });
                        break;
                    case Glyph.Close:
                        g.DrawLine(pen, P(0.25f, 0.25f), P(0.75f, 0.75f)); g.DrawLine(pen, P(0.75f, 0.25f), P(0.25f, 0.75f));
                        break;
                    case Glyph.Chevron:
                        g.DrawLines(pen, new[] { P(0.28f, 0.4f), P(0.5f, 0.62f), P(0.72f, 0.4f) });
                        break;
                    case Glyph.Note:
                        g.FillEllipse(br, R(0.18f, 0.6f, 0.26f, 0.22f)); g.FillEllipse(br, R(0.56f, 0.52f, 0.26f, 0.22f));
                        g.DrawLine(pen, P(0.42f, 0.7f), P(0.42f, 0.2f)); g.DrawLine(pen, P(0.8f, 0.62f), P(0.8f, 0.12f));
                        g.DrawLine(pen, P(0.42f, 0.2f), P(0.8f, 0.12f));
                        break;
                    case Glyph.Bolt:
                        g.FillPolygon(br, new[] { P(0.58f, 0.06f), P(0.2f, 0.56f), P(0.48f, 0.56f), P(0.42f, 0.94f), P(0.8f, 0.42f), P(0.52f, 0.42f) });
                        break;
                    case Glyph.Eye:
                    case Glyph.EyeOff:
                        g.DrawArc(pen, R(0.08f, 0.3f, 0.84f, 0.6f), 200, 140);
                        g.DrawArc(pen, R(0.08f, 0.1f, 0.84f, 0.6f), 20, 140);
                        g.DrawEllipse(pen, R(0.38f, 0.38f, 0.24f, 0.24f));
                        if (kind == Glyph.EyeOff) g.DrawLine(pen, P(0.15f, 0.85f), P(0.85f, 0.15f));
                        break;
                    default: // Bluetooth rune
                        g.DrawLines(pen, new[] { P(0.3f, 0.32f), P(0.68f, 0.66f), P(0.5f, 0.84f), P(0.5f, 0.16f), P(0.68f, 0.34f), P(0.3f, 0.68f) });
                        break;
                }
            }
            g.SmoothingMode = oldSmooth;
        }
    }
}
