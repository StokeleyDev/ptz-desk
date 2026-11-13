using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AGLPTZ;

internal static class ConsoleTheme
{
    public static readonly Color Background = Color.FromArgb(13, 20, 17);
    public static readonly Color Surface = Color.FromArgb(23, 35, 29);
    public static readonly Color Raised = Color.FromArgb(35, 52, 42);
    public static readonly Color Line = Color.FromArgb(57, 76, 64);
    public static readonly Color Text = Color.FromArgb(245, 249, 240);
    public static readonly Color Muted = Color.FromArgb(177, 193, 182);
    public static readonly Color Lime = Color.FromArgb(202, 240, 159);
    public static readonly Color Ink = Color.FromArgb(20, 43, 29);
    public static readonly Color Orbit = Color.FromArgb(102, 149, 84);

    public static Font Face(float size, bool strong = false) =>
        InterTypography.Face(size, strong);

    public static Color Mix(Color from, Color to, float amount)
    {
        int Channel(int a, int b) => (int)MathF.Round(a + (b - a) * amount);
        return Color.FromArgb(Channel(from.R, to.R), Channel(from.G, to.G), Channel(from.B, to.B));
    }

    public static void DarkFrame(IntPtr window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) || SystemInformation.HighContrast) return;
        int caption = ColorTranslator.ToWin32(Background);
        int text = ColorTranslator.ToWin32(Text);
        DwmSetWindowAttribute(window, 35, ref caption, sizeof(int));
        DwmSetWindowAttribute(window, 36, ref text, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}

internal sealed class SurfacePanel(Color fill, bool recessed = false, bool focusFrame = false) : Panel
{
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Width < 12 || Height < 12) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float scale = DeviceDpi / 96f;
        float inset = 4 * scale;
        var rect = new RectangleF(inset, inset, Width - inset * 2, Height - inset * 2);
        if (recessed) Relief.Well(e.Graphics, rect, 16 * scale, fill, 5 * scale);
        else Relief.Raised(e.Graphics, rect, 22 * scale, fill, 5 * scale, flatFace: true);
        if (focusFrame && ContainsFocus)
            Relief.Outline(e.Graphics, rect, 16 * scale,
                SystemInformation.HighContrast ? SystemColors.Highlight : ConsoleTheme.Lime, 2 * scale);
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }

    protected override void OnEnter(EventArgs e)
    {
        base.OnEnter(e);
        if (focusFrame) Invalidate();
    }

    protected override void OnLeave(EventArgs e)
    {
        base.OnLeave(e);
        if (focusFrame) Invalidate();
    }
}

internal sealed class ConsoleCameraPicker : ComboBox
{
    public ConsoleCameraPicker()
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        FlatStyle = FlatStyle.Flat;
        DrawMode = DrawMode.OwnerDrawFixed;
        AccessibleName = "USB-камера";
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        bool contrast = SystemInformation.HighContrast;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        bool field = (e.State & DrawItemState.ComboBoxEdit) != 0;
        Color fill = contrast ? (selected ? SystemColors.Highlight : SystemColors.Window)
            : selected && !field ? ConsoleTheme.Raised : ConsoleTheme.Background;
        Color ink = contrast ? (selected ? SystemColors.HighlightText : SystemColors.WindowText)
            : selected && !field ? ConsoleTheme.Lime : ConsoleTheme.Text;
        using var brush = new SolidBrush(fill);
        e.Graphics.FillRectangle(brush, e.Bounds);
        string value = e.Index >= 0 && e.Index < Items.Count ? GetItemText(Items[e.Index]) ?? ""
            : Items.Count == 0 ? "Камеры не найдены" : "Выбери камеру";
        var textBounds = Rectangle.Inflate(e.Bounds, -6, 0);
        TextRenderer.DrawText(e.Graphics, value, e.Font ?? Font, textBounds, ink,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        if (contrast) e.DrawFocusRectangle();
    }
}

internal enum ButtonKind { Secondary, Primary, Navigation, Instrument }
internal enum ButtonIcon { None, Refresh, Arrow, Center, Minus, Plus }

internal sealed class ConsoleButton : Button
{
    private readonly System.Windows.Forms.Timer animation = new() { Interval = 15 };
    private readonly Stopwatch clock = new();
    private bool hover, pressed, selected;
    private Color fill, from, target;
    private float inset, insetFrom, insetTarget;
    [DefaultValue(ButtonKind.Secondary)]
    internal ButtonKind Kind { get; set; }
    [DefaultValue(ButtonIcon.None)]
    internal ButtonIcon Icon { get; set; }

    [DefaultValue(false)]
    internal bool Selected
    {
        get => selected;
        set { selected = value; Animate(); }
    }

    public ConsoleButton()
    {
        FlatStyle = FlatStyle.Flat;
        SetStyle(ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        DoubleBuffered = true;
        animation.Tick += (_, _) =>
        {
            float t = Math.Clamp((float)clock.Elapsed.TotalMilliseconds / 135f, 0, 1);
            float ease = 1 - MathF.Pow(1 - t, 3);
            fill = ConsoleTheme.Mix(from, target, ease);
            inset = insetFrom + (insetTarget - insetFrom) * ease;
            if (t >= 1) animation.Stop();
            Invalidate();
        };
    }

    private Color Rest => Kind switch
    {
        ButtonKind.Primary => ConsoleTheme.Lime,
        ButtonKind.Instrument => ConsoleTheme.Ink,
        ButtonKind.Navigation when selected => ConsoleTheme.Raised,
        ButtonKind.Navigation => ConsoleTheme.Surface,
        _ => ConsoleTheme.Raised
    };

    private Color ButtonText => Kind == ButtonKind.Primary ? ConsoleTheme.Ink : ConsoleTheme.Text;

    private void Animate()
    {
        if (Disposing || IsDisposed) return;
        var rest = Rest;
        target = !Enabled ? ConsoleTheme.Mix(rest, Parent?.BackColor == ConsoleTheme.Lime ? ConsoleTheme.Lime : ConsoleTheme.Background, .35f)
            : pressed ? ConsoleTheme.Mix(rest, ConsoleTheme.Background, .18f)
            : hover ? ConsoleTheme.Mix(rest, Color.White, .12f)
            : rest;
        insetTarget = pressed || selected || !Enabled ? 1 : 0;
        if (fill.IsEmpty || !Visible || !SystemInformation.IsMenuAnimationEnabled || SystemInformation.HighContrast)
        {
            fill = target;
            inset = insetTarget;
            animation.Stop();
        }
        else
        {
            from = fill;
            insetFrom = inset;
            clock.Restart();
            animation.Start();
        }
        Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Animate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Animate(); }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Animate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Animate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); pressed = e.Button == MouseButtons.Left; Animate(); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); pressed = false; Animate(); }
    protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); pressed = false; Animate(); }
    protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.KeyCode == Keys.Space && !pressed) { pressed = true; Animate(); } }
    protected override void OnKeyUp(KeyEventArgs e) { base.OnKeyUp(e); if (pressed) { pressed = false; Animate(); } }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); pressed = false; Animate(); }
    protected override void Dispose(bool disposing) { if (disposing) animation.Dispose(); base.Dispose(disposing); }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width < 6 || Height < 6) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float scale = DeviceDpi / 96f;
        float offset = 4 * scale;
        var bounds = new RectangleF(offset, offset + inset * scale, Width - 2 * offset, Height - 2 * offset - inset * scale);
        if (bounds.Width < 3 || bounds.Height < 3) return;
        float radius = Math.Min(14 * scale, bounds.Height / 2);
        bool contrast = SystemInformation.HighContrast;
        Color background = contrast ? SystemColors.ButtonFace : fill.IsEmpty ? Rest : fill;
        if (contrast)
        {
            using var brush = new SolidBrush(background);
            e.Graphics.FillRectangle(brush, bounds);
        }
        else
        {
            if (inset < .94f)
                Relief.Raised(e.Graphics, bounds, radius, background, (1 - inset) * 3.7f * scale);
            else
                Relief.Well(e.Graphics, bounds, radius, background, 3 * scale);
        }
        Color ink = contrast ? (Enabled ? SystemColors.ControlText : SystemColors.GrayText)
            : selected ? ConsoleTheme.Lime : Enabled || Kind == ButtonKind.Primary ? ButtonText : ConsoleTheme.Muted;
        bool focus = Focused && ShowFocusCues;
        if (focus || contrast)
            Relief.Outline(e.Graphics, bounds, radius,
                contrast ? SystemColors.ControlText : Kind is ButtonKind.Instrument or ButtonKind.Primary
                    ? ConsoleTheme.Ink : ConsoleTheme.Lime, focus ? 2 * scale : scale);
        var textArea = Rectangle.Inflate(ClientRectangle, -(int)(12 * scale), 0);
        int down = (int)Math.Round(inset * scale);
        textArea.Offset(0, down);
        if (Icon != ButtonIcon.None)
        {
            int gap = (int)(23 * scale);
            int textWidth = Math.Min(textArea.Width - gap,
                TextRenderer.MeasureText(e.Graphics, Text, Font, Size.Empty, TextFormatFlags.NoPadding).Width);
            float x = Math.Max(9 * scale, (Width - textWidth - gap) / 2f);
            DrawIcon(e.Graphics, ink, x + 7 * scale, Height / 2f + down, scale);
            textArea = new Rectangle((int)x + gap, down, Math.Max(0, textArea.Right - (int)x - gap), Height);
        }
        var horizontal = Icon == ButtonIcon.None ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left;
        TextRenderer.DrawText(e.Graphics, Text, Font, textArea, ink,
            horizontal | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    private void DrawIcon(Graphics g, Color ink, float x, float y, float scale)
    {
        using var pen = new Pen(ink, 1.55f * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        void Line(float x1, float y1, float x2, float y2) =>
            g.DrawLine(pen, x + x1 * scale, y + y1 * scale, x + x2 * scale, y + y2 * scale);
        switch (Icon)
        {
            case ButtonIcon.Arrow:
                Line(-6, 0, 5, 0); Line(1, -4, 5, 0); Line(1, 4, 5, 0);
                break;
            case ButtonIcon.Refresh:
                g.DrawArc(pen, x - 5 * scale, y - 5 * scale, 10 * scale, 10 * scale, 35, 302);
                Line(5, -5, 5, -1); Line(1, -1, 5, -1);
                break;
            case ButtonIcon.Center:
                g.DrawEllipse(pen, x - 4 * scale, y - 4 * scale, 8 * scale, 8 * scale);
                Line(-7, 0, -3, 0); Line(3, 0, 7, 0); Line(0, -7, 0, -3); Line(0, 3, 0, 7);
                break;
            case ButtonIcon.Minus:
                Line(-5, 0, 5, 0);
                break;
            case ButtonIcon.Plus:
                Line(-5, 0, 5, 0); Line(0, -5, 0, 5);
                break;
        }
    }
}

internal sealed class ConsoleSlider : TrackBar
{
    public ConsoleSlider()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AutoSize = false;
    }

    protected override void OnValueChanged(EventArgs e) { base.OnValueChanged(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (SystemInformation.HighContrast) { base.OnPaint(e); return; }
        e.Graphics.Clear(BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float scale = DeviceDpi / 96f;
        float left = 14 * scale, right = Width - left;
        if (right <= left) return;
        var thumb = new NativeRect();
        SendMessage(Handle, 0x419, IntPtr.Zero, ref thumb); // TBM_GETTHUMBRECT
        float x = thumb.Right > thumb.Left ? Math.Clamp((thumb.Left + thumb.Right) / 2f, left, right)
            : left + (right - left) * (float)((Value - (double)Minimum) / Math.Max(1, Maximum - (double)Minimum));
        float r = 11 * scale;
        float y = thumb.Bottom > thumb.Top ? Math.Clamp((thumb.Top + thumb.Bottom) / 2f, r + 3, Math.Max(r + 3, Height - r - 3))
            : Height / 2f;
        var groove = new RectangleF(left, y - 6 * scale, right - left, 12 * scale);
        Relief.Well(e.Graphics, groove, groove.Height / 2, BackColor, 3 * scale);
        if (Enabled) Relief.Progress(e.Graphics, groove, x, ConsoleTheme.Lime);
        var knob = new RectangleF(x - r, y - r, r * 2, r * 2);
        Color face = Enabled ? ConsoleTheme.Raised : ConsoleTheme.Surface;
        Relief.Raised(e.Graphics, knob, r, face, 3 * scale);
        if (Enabled)
        {
            // The dark edge separates the thumb from the filled lime track.
            // The lighter ring defines the same edge against the dark panel.
            Relief.Outline(e.Graphics, knob, r, ConsoleTheme.Ink, 3.5f * scale);
            Relief.Outline(e.Graphics, knob, r, ConsoleTheme.Orbit, 1.8f * scale);
        }
        float inner = r * .48f;
        Relief.Well(e.Graphics, new RectangleF(x - inner, y - inner, inner * 2, inner * 2),
            inner, Enabled ? ConsoleTheme.Orbit : ConsoleTheme.Raised, 2 * scale);
        if (Focused && ShowFocusCues)
        {
            knob.Inflate(2 * scale, 2 * scale);
            Relief.Outline(e.Graphics, knob, r + 2 * scale, ConsoleTheme.Lime, scale);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wparam, ref NativeRect rect);
}
