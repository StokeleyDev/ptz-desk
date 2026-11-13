using System.Drawing;
using System.Drawing.Drawing2D;

namespace AGLPTZ;

// All relief follows the same light direction: upper left highlight, lower right shadow.
internal static class Relief
{
    private static GraphicsPath Shape(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        float half = Math.Min(bounds.Width, bounds.Height) / 2;
        if (bounds.Width == bounds.Height && radius >= half)
            path.AddEllipse(bounds);
        else
            path.AddRoundedRectangle(bounds, new SizeF(Math.Min(radius, half), Math.Min(radius, half)));
        return path;
    }

    private static void Shadow(Graphics g, RectangleF bounds, float radius, Color ink, float depth, bool upper)
    {
        float sign = upper ? -1 : 1;
        bounds.Offset(sign * depth * .62f, sign * depth * .7f);
        for (int pass = 6; pass >= 0; pass--)
        {
            float blur = pass * depth * .19f;
            var spread = bounds;
            spread.Inflate(blur, blur);
            using var path = Shape(spread, radius + blur);
            using var brush = new SolidBrush(Color.FromArgb(upper ? 8 : 17, ink));
            g.FillPath(brush, path);
        }
    }

    internal static void Raised(Graphics g, RectangleF bounds, float radius, Color face, float depth, bool flatFace = false)
    {
        if (bounds.Width < 2 || bounds.Height < 2) return;
        bool light = face.GetBrightness() > .45f;
        Color highlight = ConsoleTheme.Mix(face, Color.White, light ? .62f : .37f);
        Color low = ConsoleTheme.Mix(face, Color.Black, light ? .32f : .61f);
        if (depth > .4f)
        {
            Shadow(g, bounds, radius, highlight, depth, true);
            Shadow(g, bounds, radius, low, depth, false);
        }

        using var path = Shape(bounds, radius);
        using var body = new LinearGradientBrush(bounds,
            flatFace ? face : ConsoleTheme.Mix(face, highlight, .23f),
            flatFace ? face : ConsoleTheme.Mix(face, low, .16f), 49f);
        g.FillPath(body, path);
        Bevel(g, bounds, radius, highlight, low, Math.Clamp(depth * .8f, 1.3f, 5f));
    }

    internal static void Well(Graphics g, RectangleF bounds, float radius, Color face, float depth)
    {
        if (bounds.Width < 2 || bounds.Height < 2) return;
        bool light = face.GetBrightness() > .45f;
        Color shadow = ConsoleTheme.Mix(face, Color.Black, light ? .44f : .61f);
        Color lip = ConsoleTheme.Mix(face, Color.White, light ? .56f : .37f);
        using var path = Shape(bounds, radius);
        using var fill = new LinearGradientBrush(bounds, ConsoleTheme.Mix(face, shadow, .61f), face, 48f);
        g.FillPath(fill, path);
        Bevel(g, bounds, radius, shadow, lip, Math.Max(1.3f, depth * .86f));
    }

    private static void Bevel(Graphics g, RectangleF bounds, float radius, Color top, Color bottom, float width)
    {
        // Fade the directional bevel towards the centre, keeping the edge antialiased.
        width = Math.Min(width, Math.Min(bounds.Width, bounds.Height) / 3);
        const int layers = 4;
        float stride = width / layers;
        for (int i = 0; i < layers; i++)
        {
            float inset = (i + .5f) * stride;
            var inner = bounds;
            inner.Inflate(-inset, -inset);
            using var path = Shape(inner, Math.Max(.1f, radius - inset));
            int opacity = 117 - i * 28;
            using var gradient = new LinearGradientBrush(bounds, Color.FromArgb(opacity, top), Color.FromArgb(opacity, bottom), 48f);
            using var pen = new Pen(gradient, stride * 1.25f);
            g.DrawPath(pen, path);
        }
    }

    internal static void Outline(Graphics g, RectangleF bounds, float radius, Color color, float thickness)
    {
        using var path = Shape(bounds, radius);
        using var pen = new Pen(color, thickness);
        g.DrawPath(pen, path);
    }

    internal static void Progress(Graphics g, RectangleF groove, float end, Color lime)
    {
        if (end <= groove.Left || groove.Width < 2) return;
        var fill = new RectangleF(groove.Left + 1, groove.Top + 1,
            Math.Min(end, groove.Right) - groove.Left - 1, groove.Height - 2);
        if (fill.Width < 2) return;
        using var segment = Shape(fill, fill.Height / 2);
        using var pigment = new LinearGradientBrush(groove, lime, ConsoleTheme.Mix(lime, ConsoleTheme.Orbit, .36f), 90f);
        g.FillPath(pigment, segment);
    }
}
