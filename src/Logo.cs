using System.Drawing;
using System.Drawing.Drawing2D;

enum Mode { Screen1, Screen2, Extend, Duplicate }

static class Logo
{
    static readonly Color Blue = Color.FromArgb(190, 50, 28), Sky = Color.FromArgb(240, 120, 56);
    static readonly Color Orange = Color.FromArgb(190, 118, 32), Amber = Color.FromArgb(247, 196, 92);
    static readonly Color Teal = Color.FromArgb(28, 110, 96), Mint = Color.FromArgb(86, 190, 150);
    static readonly Color Purple = Color.FromArgb(136, 46, 78), Pink = Color.FromArgb(226, 104, 110);

    // Main app logo: two overlapping monitors.
    public static void Draw(Graphics g, float s)
    {
        Background(g, s, Color.FromArgb(200, 52, 30), Color.FromArgb(243, 166, 60));

        using (GraphicsPath back = Rounded(s * 0.42f, s * 0.20f, s * 0.44f, s * 0.32f, s * 0.05f))
        using (SolidBrush brush = new SolidBrush(Color.FromArgb(150, 255, 255, 255)))
        {
            g.FillPath(brush, back);
        }

        using (GraphicsPath front = Rounded(s * 0.14f, s * 0.32f, s * 0.54f, s * 0.38f, s * 0.05f))
        using (Pen gap = new Pen(Color.FromArgb(214, 96, 44), s * 0.05f))
        {
            g.DrawPath(gap, front);
            g.FillPath(Brushes.White, front);
        }

        Stand(g, s, s * 0.41f, 0.70f, 0.28f);
    }

    // One logo per option, each showing what that option does.
    public static void Draw(Graphics g, float s, Mode mode)
    {
        switch (mode)
        {
            case Mode.Screen1:
                Background(g, s, Blue, Sky);
                SingleScreen(g, s, "1", Blue);
                break;
            case Mode.Screen2:
                Background(g, s, Orange, Amber);
                SingleScreen(g, s, "2", Orange);
                break;
            case Mode.Extend:
                Background(g, s, Teal, Mint);
                ExtendedScreens(g, s);
                break;
            default:
                Background(g, s, Purple, Pink);
                DuplicatedScreens(g, s);
                break;
        }
    }

    // One big monitor with its number on it.
    static void SingleScreen(Graphics g, float s, string number, Color ink)
    {
        Screen(g, s * 0.13f, s * 0.17f, s * 0.74f, s * 0.53f, s * 0.05f);
        Stand(g, s, s * 0.5f, 0.70f, 0.34f);
        using (GraphicsPath text = new GraphicsPath())
        using (StringFormat center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        using (SolidBrush brush = new SolidBrush(ink))
        using (FontFamily family = new FontFamily("Segoe UI"))
        {
            text.AddString(number, family, (int)FontStyle.Bold, s * 0.44f,
                new RectangleF(s * 0.13f, s * 0.17f, s * 0.74f, s * 0.53f), center);
            g.FillPath(brush, text);
        }
    }

    // Two monitors touching, with an arrow stretching across both.
    static void ExtendedScreens(Graphics g, float s)
    {
        float y = s * 0.26f, w = s * 0.42f, h = s * 0.38f;
        Screen(g, s * 0.08f, y, w, h, s * 0.04f);
        Screen(g, s * 0.50f, y, w, h, s * 0.04f);
        Stand(g, s, s * 0.29f, 0.64f, 0.22f);
        Stand(g, s, s * 0.71f, 0.64f, 0.22f);

        using (SolidBrush ink = new SolidBrush(Teal))
        using (Pen divider = new Pen(Teal, s * 0.015f))
        {
            float mid = y + h / 2, thick = s * 0.03f, head = s * 0.09f;
            g.DrawLine(divider, s * 0.5f, y, s * 0.5f, y + h);
            g.FillRectangle(ink, s * 0.20f, mid - thick, s * 0.60f, thick * 2);
            g.FillPolygon(ink, new[] { new PointF(s * 0.13f, mid), new PointF(s * 0.13f + head, mid - head), new PointF(s * 0.13f + head, mid + head) });
            g.FillPolygon(ink, new[] { new PointF(s * 0.87f, mid), new PointF(s * 0.87f - head, mid - head), new PointF(s * 0.87f - head, mid + head) });
        }
    }

    // Two separate monitors showing the identical picture.
    static void DuplicatedScreens(Graphics g, float s)
    {
        float y = s * 0.27f, w = s * 0.39f, h = s * 0.36f;
        foreach (float x in new[] { s * 0.07f, s * 0.54f })
        {
            Screen(g, x, y, w, h, s * 0.04f);
            Stand(g, s, x + w / 2, 0.63f, 0.20f);
            using (SolidBrush ink = new SolidBrush(Purple))
            {
                g.FillPolygon(ink, new[] { new PointF(x + w * 0.5f, y + h * 0.18f), new PointF(x + w * 0.82f, y + h * 0.82f), new PointF(x + w * 0.18f, y + h * 0.82f) });
            }
        }
    }

    static void Background(Graphics g, float s, Color from, Color to)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (GraphicsPath bg = Rounded(0, 0, s, s, s * 0.22f))
        using (LinearGradientBrush fill = new LinearGradientBrush(new RectangleF(0, 0, s, s), from, to, 45f))
        {
            g.FillPath(fill, bg);
        }
    }

    static void Screen(Graphics g, float x, float y, float w, float h, float r)
    {
        using (GraphicsPath screen = Rounded(x, y, w, h, r)) g.FillPath(Brushes.White, screen);
    }

    static void Stand(Graphics g, float s, float centerX, float top, float baseWidth)
    {
        g.FillRectangle(Brushes.White, centerX - s * 0.05f, s * top, s * 0.10f, s * 0.08f);
        g.FillRectangle(Brushes.White, centerX - s * baseWidth / 2, s * (top + 0.07f), s * baseWidth, s * 0.04f);
    }

    public static GraphicsPath Rounded(float x, float y, float w, float h, float r)
    {
        float d = r * 2;
        GraphicsPath p = new GraphicsPath();
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
