using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AGLPTZ;

internal static class Program
{
    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    [STAThread]
    private static void Main()
    {
        // Test mode may run several isolated instances (panel + hub) at once.
        var suffix = DirectShowCamera.TestMode ? Environment.GetEnvironmentVariable("AGL_PTZ_INSTANCE") ?? "" : "";
        using var activation = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AGL.PTZ.Controller.Activate" + suffix);
        using var instance = new Mutex(true, @"Local\AGL.PTZ.Controller.Instance" + suffix, out bool first);
        if (!first)
        {
            using var current = Process.GetCurrentProcess();
            foreach (var candidate in Process.GetProcessesByName(current.ProcessName))
            {
                using (candidate)
                    if (candidate.Id != current.Id && candidate.SessionId == current.SessionId)
                        AllowSetForegroundWindow(candidate.Id);
            }
            activation.Set();
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.Dark);
        // The default font is only a convenience: a helper thread (system events, font broadcast) may
        // already have created a window handle under load, e.g. at logon, and WinForms then refuses it.
        try { Application.SetDefaultFont(ConsoleTheme.Face(10)); }
        catch (InvalidOperationException) { }
        var form = new MainForm();
        using var timer = new System.Windows.Forms.Timer { Interval = 100 };
        timer.Tick += (_, _) =>
        {
            if (!activation.WaitOne(0)) return;
            form.ShowController();
        };
        timer.Start();
        try
        {
            if (MainForm.PanelMode)
            {
                form.InitializeHiddenPanel();
                using var context = new ApplicationContext();
                form.FormClosed += (_, _) => context.ExitThread();
                Application.Run(context);
            }
            else Application.Run(form);
        }
        finally { instance.ReleaseMutex(); }
    }
}

internal sealed class JoystickControl : Control
{
    private readonly System.Windows.Forms.Timer animation = new() { Interval = 15 };
    private readonly Stopwatch clock = new();
    private PointF vector, returnFrom;
    private bool dragging;

    public event Action<float, float>? VectorChanged;

    public JoystickControl()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = ConsoleTheme.Lime;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.Selectable, false);
        animation.Tick += (_, _) =>
        {
            float t = Math.Clamp((float)clock.Elapsed.TotalMilliseconds / 190f, 0, 1);
            float remaining = MathF.Pow(1 - t, 3);
            vector = new PointF(returnFrom.X * remaining, returnFrom.Y * remaining);
            if (t >= 1) animation.Stop();
            Invalidate();
        };
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Enabled || e.Button != MouseButtons.Left) return;
        animation.Stop();
        dragging = true;
        Capture = true;
        UpdateVector(e.Location);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragging) UpdateVector(e.Location);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left) ReleaseStick();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (dragging && !Capture) ReleaseStick();
    }

    public void ReleaseStick()
    {
        if (!dragging) return;
        dragging = false;
        Capture = false;
        VectorChanged?.Invoke(0, 0);
        if (vector != PointF.Empty && SystemInformation.IsMenuAnimationEnabled && !SystemInformation.HighContrast)
        {
            returnFrom = vector;
            clock.Restart();
            animation.Start();
        }
        else
        {
            vector = PointF.Empty;
            Invalidate();
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) animation.Dispose();
    }

    private void UpdateVector(Point p)
    {
        float radius = Math.Max(1, Math.Min(Width, Height) * .38f);
        float dx = (p.X - Width / 2f) / radius;
        float dy = (Height / 2f - p.Y) / radius;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        if (length > 1) { dx /= length; dy /= length; }
        vector = new PointF(dx, dy);
        Invalidate();
        VectorChanged?.Invoke(dx, dy);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float cx = Width / 2f, cy = Height / 2f;
        float scale = DeviceDpi / 96f;
        float outer = Math.Min(Math.Min(Width, Height) * .43f, 155 * scale);
        if (outer < 16) return;
        float travel = outer * .63f;

        RectangleF Circle(float x, float y, float radius) =>
            new(x - radius, y - radius, radius * 2, radius * 2);

        var dial = Circle(cx, cy, outer);
        var collarFace = ConsoleTheme.Mix(ConsoleTheme.Lime, ConsoleTheme.Orbit, .16f);
        var grooveFace = ConsoleTheme.Mix(ConsoleTheme.Lime, ConsoleTheme.Orbit, .58f);
        Relief.Raised(g, dial, outer, collarFace, 7 * scale);
        float channel = outer * .87f;
        Relief.Well(g, Circle(cx, cy, channel), channel, grooveFace, 6 * scale);
        float bowl = outer * .70f;
        Relief.Raised(g, Circle(cx, cy, bowl), bowl, collarFace, 5 * scale);

        float magnitude = Math.Min(1, MathF.Sqrt(vector.X * vector.X + vector.Y * vector.Y));
        float ring = outer * .79f;
        if (magnitude > .01f)
        {
            float angle = MathF.Atan2(-vector.Y, vector.X) * (180 / MathF.PI);
            using var arc = new Pen(ConsoleTheme.Ink, (outer * .075f))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            float sweep = 20 + 66 * magnitude;
            g.DrawArc(arc, Circle(cx, cy, ring), angle - sweep / 2, sweep);
        }

        using var guide = new Pen(ConsoleTheme.Mix(ConsoleTheme.Ink, collarFace, .46f), 1.3f * scale)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        float tick = 4 * scale;
        foreach (PointF axis in new[] { new PointF(1, 0), new PointF(-1, 0), new PointF(0, 1), new PointF(0, -1) })
            g.DrawLine(guide, cx + axis.X * (ring - tick), cy + axis.Y * (ring - tick),
                cx + axis.X * (ring + tick), cy + axis.Y * (ring + tick));

        float kx = cx + vector.X * travel, ky = cy - vector.Y * travel;
        float knob = outer * .26f;
        var knobFace = Enabled ? ConsoleTheme.Lime : collarFace;
        Relief.Raised(g, Circle(kx, ky, knob), knob, knobFace, (dragging ? 3 : 6) * scale);
        float center = knob * .69f;
        Relief.Well(g, Circle(kx, ky, center), center, knobFace, 3 * scale);
        float grip = knob * .27f;
        g.DrawLine(guide, kx - grip, ky, kx + grip, ky);
        g.DrawLine(guide, kx, ky - grip, kx, ky + grip);
    }

}

internal sealed class MotionAxis
{
    private const double AccelerationSeconds = .18;
    private double velocity;
    private double remainder;

    internal int NextSteps(double input, int speed, double elapsed, bool keyboard)
    {
        if (input == 0)
        {
            Reset();
            return 0;
        }

        int direction = Math.Sign(input);
        bool starting = velocity == 0 || direction != Math.Sign(velocity);
        if (starting) Reset();

        double strength = Math.Abs(input);
        double target = direction * strength * (.2 + .8 * strength);
        double ramp = elapsed / AccelerationSeconds;
        velocity += Math.Clamp(target - velocity, -ramp, ramp);

        double steps = remainder + Math.Abs(velocity) * speed * 8 * elapsed;
        int whole = (int)steps;
        remainder = steps - whole;
        if (starting && keyboard && whole == 0)
        {
            remainder = 0;
            whole = 1;
        }
        return direction * whole;
    }

    internal void Reset() => velocity = remainder = 0;
}

internal sealed partial class MainForm : Form, IMessageFilter
{
    private readonly ComboBox cameras = new ConsoleCameraPicker();
    private Control? nativeShell;
    private readonly Label status = new();
    private readonly Label connection = new();
    private readonly Label panValue = new();
    private readonly Label tiltValue = new();
    private readonly Label zoomValue = new();
    private readonly Label zoomDetails = new();
    private readonly Label speedValue = new();
    private readonly TextBox report = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        BorderStyle = BorderStyle.None, AccessibleName = "Результат проверки камеры"
    };
    private readonly JoystickControl joystick = new() { Enabled = false, AccessibleName = "Джойстик поворота и наклона" };
    private readonly TrackBar zoom = new ConsoleSlider() { TickStyle = TickStyle.None, Enabled = false, AccessibleName = "Зум" };
    private readonly TrackBar speed = new ConsoleSlider() { Minimum = 1, Maximum = 10, Value = 5, TickStyle = TickStyle.None, AccessibleName = "Скорость движения" };
    private readonly ConsoleButton connect = new ConsoleButton();
    private readonly ConsoleButton home = new ConsoleButton();
    private readonly ConsoleButton wide = new ConsoleButton();
    private readonly ConsoleButton tight = new ConsoleButton();
    private readonly System.Windows.Forms.Timer moveTimer = new() { Interval = 65 };
    private readonly System.Windows.Forms.Timer zoomTimer = new() { Interval = 100 };
    private DirectShowCamera? device;
    private int connectedCameraIndex = -1;
    private readonly Dictionary<CameraProp, CameraRange> ranges = new();
    private readonly Dictionary<CameraProp, int> values = new();
    private float stickX, stickY;
    private bool updatingZoom;
    private bool zoomPending;
    private int wantedZoom;
    private readonly HashSet<Keys> heldKeys = new();
    private readonly MotionAxis panMotion = new();
    private readonly MotionAxis tiltMotion = new();
    private long lastMovement;
    private long lastKeyboardZoom;

    public MainForm()
    {
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Text = "AGL PTZ Controller";
        FormClosing += CloseRemoteBeforeExit;
        ClientSize = new Size(1150, 752);
        MinimumSize = HasReactInterface ? new Size(420, 410) : new Size(960, 640);
        StartPosition = FormStartPosition.CenterScreen;
        if (Environment.GetCommandLineArgs().Contains("--maximized", StringComparer.OrdinalIgnoreCase))
            WindowState = FormWindowState.Maximized;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = ConsoleTheme.Background;
        ForeColor = ConsoleTheme.Text;
        Font = ConsoleTheme.Face(10);
        Deactivate += (_, _) =>
        {
            if (!Disposing && !IsDisposed && CurrentInputOwner.Source == ControlSource.Desktop)
            {
                if (leaseKind == "desktop") StopLeasedMotion();
                else StopJoystick();
            }
        };

        BuildInterface();
        if (DemoMode && nativeShell != null) nativeShell.Enabled = false;
        if (HasReactInterface && nativeShell != null) nativeShell.Visible = false;
        cameras.DropDown += (_, _) => StopJoystick();
        joystick.VectorChanged += (x, y) =>
        {
            stickX = x;
            stickY = y;
            UpdateMovement();
        };
        moveTimer.Tick += (_, _) => SendMovement();
        networkTimer.Tick += (_, _) => QueueNetworkMotion();
        zoom.ValueChanged += (_, _) => QueueZoom();
        zoom.MouseUp += (_, _) => FlushZoom();
        speed.ValueChanged += (_, _) => UpdateSpeed();
        zoomTimer.Tick += (_, _) => FlushZoom();
        UpdateSpeed();
        if (!DemoMode)
        {
            RefreshDevices();
            LoadCameraBank();
        }
        Application.AddMessageFilter(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Application.RemoveMessageFilter(this);
            panelTray?.ContextMenuStrip?.Dispose();
            panelTray?.Dispose();
            audioTimer?.Dispose();
            StopHub();
            audioBackend?.Dispose();
            _ = StopRemoteLifecycleAsync();
            DisconnectNetwork();
            networkTimer.Dispose();
            moveTimer.Dispose();
            zoomTimer.Dispose();
            device?.Dispose();
            device = null;
        }
        base.Dispose(disposing);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        await InitializeControllerAsync();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ConsoleTheme.DarkFrame(Handle);
    }

    private static TableLayoutPanel Grid(int columns, int rows) => new()
    {
        Dock = DockStyle.Fill, ColumnCount = columns, RowCount = rows,
        Margin = Padding.Empty, GrowStyle = TableLayoutPanelGrowStyle.FixedSize
    };

    private static void Row(TableLayoutPanel table, int height) =>
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, height));

    private static void FluidRow(TableLayoutPanel table) =>
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

    private static void Column(TableLayoutPanel table, SizeType type, float width) =>
        table.ColumnStyles.Add(new ColumnStyle(type, width));

    private static Label Caption(string text, float size, Color color, bool strong = false) =>
        StyleLabel(new Label { Text = text }, size, color, strong);

    private static Label StyleLabel(Label label, float size, Color color, bool strong = false)
    {
        label.Dock = DockStyle.Fill;
        label.Margin = Padding.Empty;
        label.Font = ConsoleTheme.Face(size, strong);
        label.ForeColor = color;
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.AutoEllipsis = true;
        return label;
    }

    private static void StyleButton(ConsoleButton button, string text, Action action,
        ButtonKind kind = ButtonKind.Secondary, ButtonIcon icon = ButtonIcon.None)
    {
        button.Text = text;
        button.Kind = kind;
        button.Icon = icon;
        button.Dock = DockStyle.Fill;
        button.Margin = Padding.Empty;
        button.Cursor = Cursors.Hand;
        button.Font = ConsoleTheme.Face(10, true);
        button.Click += (_, _) => action();
    }

    private static SurfacePanel Card(TableLayoutPanel content, Color fill, Padding padding)
    {
        var panel = new SurfacePanel(fill)
        {
            Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = padding,
            BackColor = Color.Transparent
        };
        content.BackColor = fill;
        panel.Controls.Add(content);
        return panel;
    }

    private void BuildInterface()
    {
        var shell = Grid(1, 5);
        shell.Padding = new Padding(27, 18, 27, 10);
        Row(shell, 50);
        Row(shell, 79);
        Row(shell, 73);
        FluidRow(shell);
        Row(shell, 37);
        nativeShell = shell;
        Controls.Add(shell);

        var nav = Grid(5, 1);
        FluidRow(nav);
        nav.Padding = new Padding(0, 0, 0, 8);
        Column(nav, SizeType.Absolute, 132);
        Column(nav, SizeType.Absolute, 140);
        Column(nav, SizeType.Absolute, 8);
        Column(nav, SizeType.Absolute, 152);
        Column(nav, SizeType.Percent, 100);
        nav.Controls.Add(Caption("AGL / PTZ", 16, ConsoleTheme.Text, true), 0, 0);
        var controlTab = new ConsoleButton();
        var testTab = new ConsoleButton();
        nav.Controls.Add(controlTab, 1, 0);
        nav.Controls.Add(testTab, 3, 0);
        StyleLabel(connection, 10, ConsoleTheme.Muted, true);
        connection.TextAlign = ContentAlignment.MiddleRight;
        var statusWell = new SurfacePanel(ConsoleTheme.Background, recessed: true)
        {
            Dock = DockStyle.Right, Width = 190, Margin = Padding.Empty,
            Padding = new Padding(10, 0, 10, 0), BackColor = Color.Transparent
        };
        connection.TextAlign = ContentAlignment.MiddleCenter;
        statusWell.Controls.Add(connection);
        nav.Controls.Add(statusWell, 4, 0);
        shell.Controls.Add(nav, 0, 0);

        var heading = Grid(1, 2);
        Row(heading, 53);
        FluidRow(heading);
        var title = Caption("Пульт камеры", 32, ConsoleTheme.Text, true);
        var subtitle = Caption("Управление поворотом, наклоном и зумом по USB", 10, ConsoleTheme.Muted);
        heading.Controls.Add(title, 0, 0);
        heading.Controls.Add(subtitle, 0, 1);
        shell.Controls.Add(heading, 0, 1);

        var deviceBar = Grid(6, 1);
        FluidRow(deviceBar);
        Column(deviceBar, SizeType.Absolute, 92);
        Column(deviceBar, SizeType.Percent, 100);
        Column(deviceBar, SizeType.Absolute, 9);
        Column(deviceBar, SizeType.Absolute, 132);
        Column(deviceBar, SizeType.Absolute, 9);
        Column(deviceBar, SizeType.Absolute, 156);
        deviceBar.Controls.Add(Caption("USB-камера", 9, ConsoleTheme.Muted, true), 0, 0);
        cameras.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        cameras.Margin = Padding.Empty;
        cameras.BackColor = ConsoleTheme.Background;
        cameras.ForeColor = ConsoleTheme.Text;
        var cameraWell = new SurfacePanel(ConsoleTheme.Background, recessed: true, focusFrame: true)
        {
            Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(9, 0, 9, 0),
            BackColor = Color.Transparent
        };
        var cameraLayout = Grid(1, 1);
        FluidRow(cameraLayout);
        cameraLayout.Controls.Add(cameras, 0, 0);
        cameraWell.Controls.Add(cameraLayout);
        deviceBar.Controls.Add(cameraWell, 1, 0);
        var refresh = new ConsoleButton();
        StyleButton(refresh, "Обновить", RefreshDevices, icon: ButtonIcon.Refresh);
        StyleButton(connect, "Подключить", Connect, ButtonKind.Primary, ButtonIcon.Arrow);
        deviceBar.Controls.Add(refresh, 3, 0);
        deviceBar.Controls.Add(connect, 5, 0);
        var deviceCard = Card(deviceBar, ConsoleTheme.Surface, new Padding(16, 8, 8, 8));
        deviceCard.Margin = new Padding(0, 5, 0, 16);
        shell.Controls.Add(deviceCard, 0, 2);

        var host = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        var controls = BuildControlsPage();
        var test = BuildTestPage();
        host.Controls.Add(test);
        host.Controls.Add(controls);
        shell.Controls.Add(host, 0, 3);
        void SelectPage(bool check)
        {
            StopJoystick();
            controls.Visible = !check;
            test.Visible = check;
            controlTab.Selected = !check;
            testTab.Selected = check;
            title.Text = check ? "Проверка камеры" : "Пульт камеры";
            subtitle.Text = check ? "Поддерживаемые оси, шаги и диапазоны USB PTZ"
                : "Управление поворотом, наклоном и зумом по USB";
        }
        StyleButton(controlTab, "Управление", () => SelectPage(false), ButtonKind.Navigation);
        StyleButton(testTab, "Проверка камеры", () => SelectPage(true), ButtonKind.Navigation);
        SelectPage(false);

        StyleLabel(status, 10, ConsoleTheme.Muted);
        shell.Controls.Add(status, 0, 4);
    }

    private Control BuildControlsPage()
    {
        var columns = Grid(3, 1);
        Column(columns, SizeType.Percent, 56);
        Column(columns, SizeType.Absolute, 16);
        Column(columns, SizeType.Percent, 44);

        var pan = Grid(1, 4);
        Row(pan, 43);
        FluidRow(pan);
        Row(pan, 27);
        Row(pan, 42);
        pan.Controls.Add(Caption("Поворот и наклон", 23, ConsoleTheme.Ink, true), 0, 0);
        joystick.Dock = DockStyle.Fill;
        joystick.Margin = new Padding(0, 3, 0, 6);
        pan.Controls.Add(joystick, 0, 1);
        var axes = Grid(2, 1);
        Column(axes, SizeType.Percent, 50);
        Column(axes, SizeType.Percent, 50);
        axes.Controls.Add(StyleLabel(panValue, 10, ConsoleTheme.Ink, true), 0, 0);
        StyleLabel(tiltValue, 10, ConsoleTheme.Ink, true).TextAlign = ContentAlignment.MiddleRight;
        axes.Controls.Add(tiltValue, 1, 0);
        pan.Controls.Add(axes, 0, 2);
        StyleButton(home, "Вернуть в центр", Home, ButtonKind.Instrument, ButtonIcon.Center);
        pan.Controls.Add(home, 0, 3);
        columns.Controls.Add(Card(pan, ConsoleTheme.Lime, new Padding(22, 13, 22, 16)), 0, 0);

        var right = Grid(1, 3);
        FluidRow(right);
        Row(right, 14);
        Row(right, 132);
        var lens = Grid(1, 6);
        Row(lens, 24);
        Row(lens, 66);
        Row(lens, 28);
        FluidRow(lens);
        Row(lens, 34);
        Row(lens, 40);
        lens.Controls.Add(Caption("Зум объектива", 10, ConsoleTheme.Muted, true), 0, 0);
        lens.Controls.Add(StyleLabel(zoomValue, 39, ConsoleTheme.Lime, true), 0, 1);
        lens.Controls.Add(StyleLabel(zoomDetails, 10, ConsoleTheme.Muted), 0, 2);
        zoom.Dock = DockStyle.Fill;
        zoom.Margin = Padding.Empty;
        zoom.BackColor = ConsoleTheme.Surface;
        lens.Controls.Add(zoom, 0, 4);
        var edges = Grid(3, 1);
        Column(edges, SizeType.Percent, 50);
        Column(edges, SizeType.Absolute, 8);
        Column(edges, SizeType.Percent, 50);
        StyleButton(wide, "Минимум", () => SetZoomToEdge(false), icon: ButtonIcon.Minus);
        StyleButton(tight, "Максимум", () => SetZoomToEdge(true), icon: ButtonIcon.Plus);
        edges.Controls.Add(wide, 0, 0);
        edges.Controls.Add(tight, 2, 0);
        lens.Controls.Add(edges, 0, 5);
        right.Controls.Add(Card(lens, ConsoleTheme.Surface, new Padding(19, 12, 19, 14)), 0, 0);

        var movement = Grid(2, 3);
        Column(movement, SizeType.Percent, 100);
        Column(movement, SizeType.Absolute, 66);
        Row(movement, 31);
        Row(movement, 34);
        FluidRow(movement);
        movement.Controls.Add(Caption("Скорость движения", 13, ConsoleTheme.Text, true), 0, 0);
        StyleLabel(speedValue, 12, ConsoleTheme.Lime, true).TextAlign = ContentAlignment.MiddleRight;
        movement.Controls.Add(speedValue, 1, 0);
        speed.Dock = DockStyle.Fill;
        speed.Margin = Padding.Empty;
        speed.BackColor = ConsoleTheme.Surface;
        movement.Controls.Add(speed, 0, 1);
        movement.SetColumnSpan(speed, 2);
        var shortcuts = Caption("Стрелки — движение, + и − — зум", 9, ConsoleTheme.Muted);
        movement.Controls.Add(shortcuts, 0, 2);
        movement.SetColumnSpan(shortcuts, 2);
        right.Controls.Add(Card(movement, ConsoleTheme.Surface, new Padding(19, 13, 19, 13)), 0, 2);
        columns.Controls.Add(right, 2, 0);
        return columns;
    }

    private Control BuildTestPage()
    {
        var panel = Grid(1, 2);
        Row(panel, 43);
        FluidRow(panel);
        panel.Controls.Add(Caption("Возможности устройства", 21, ConsoleTheme.Text, true), 0, 0);
        report.Dock = DockStyle.Fill;
        report.Margin = new Padding(0, 10, 0, 0);
        report.BackColor = ConsoleTheme.Background;
        report.ForeColor = ConsoleTheme.Text;
        report.Font = ConsoleTheme.Face(10);
        var reportPanel = new SurfacePanel(ConsoleTheme.Background, recessed: true, focusFrame: true)
        {
            Dock = DockStyle.Fill, BackColor = Color.Transparent,
            Padding = new Padding(18), Margin = new Padding(0, 8, 0, 0)
        };
        reportPanel.Controls.Add(report);
        panel.Controls.Add(reportPanel, 0, 1);
        return Card(panel, ConsoleTheme.Surface, new Padding(22, 14, 22, 18));
    }

    private bool CanControl(CameraProp prop) => ranges.TryGetValue(prop, out var range) && range.Manual;

    private void UpdateDeviceControls()
    {
        home.Enabled = joystick.Enabled = CanControl(CameraProp.Pan) || CanControl(CameraProp.Tilt);
        wide.Enabled = tight.Enabled = zoom.Enabled;
        connect.Enabled = cameras.Items.Count != 0;
        connection.Text = device == null ? "○   Не подключено" : ranges.Count == 0 ? "○   PTZ недоступен" : "●   Камера подключена";
        connection.ForeColor = device != null && ranges.Count > 0 ? ConsoleTheme.Lime : ConsoleTheme.Muted;
        UpdateAxisValues();
    }

    private void UpdateAxisValues()
    {
        panValue.Text = values.TryGetValue(CameraProp.Pan, out int pan) ? $"Pan   {pan:N0}" : "Pan   —";
        tiltValue.Text = values.TryGetValue(CameraProp.Tilt, out int tilt) ? $"Tilt   {tilt:N0}" : "Tilt   —";
    }

    private void StopJoystick()
    {
        heldKeys.Clear();
        stickX = stickY = 0;
        ResetMotion();
        joystick.ReleaseStick();
        StopNetworkMotion();
    }
    private void ResetState()
    {
        StopJoystick(); zoomTimer.Stop(); zoomPending = false; zoom.Enabled = false;
        device?.Dispose(); device = null; ranges.Clear(); values.Clear();
        connectedCameraIndex = -1;
        zoomValue.Text = "—";
        zoomDetails.Text = "Зум доступен после подключения";
        UpdateDeviceControls();
    }
    private void RefreshDevices()
    {
        try
        {
            ResetState(); cameras.Items.Clear();
            foreach (var info in DirectShowCamera.List()) cameras.Items.Add(info);
            if (cameras.Items.Count > 0)
            {
                int match = -1;
                for (int i = 0; i < cameras.Items.Count; i++)
                    if (cameras.Items[i] is CameraInfo ci && ci.Name.Contains("PTZ", StringComparison.OrdinalIgnoreCase)) { match = i; break; }
                cameras.SelectedIndex = match >= 0 ? match : 0;
            }
            status.Text = $"Обнаружено видеокамер: {cameras.Items.Count}.";
            report.Text = "Подключите камеру, чтобы увидеть доступные оси и диапазоны.";
        }
        catch (Exception ex) { status.Text = "Ошибка поиска устройств"; report.Text = ex.ToString(); }
        UpdateDeviceControls();
    }
    private void Connect()
    {
        if (cameras.SelectedItem is not CameraInfo info) { status.Text = "Камера не выбрана"; return; }
        ResetState();
        try
        {
            device = DirectShowCamera.Open(info);
            connectedCameraIndex = cameras.SelectedIndex;
            report.Text = $"Устройство: {info.Name}\r\nDirectShow IAMCameraControl: {(device.HasInterface ? "доступен" : "отсутствует")}\r\n";
            foreach (CameraProp prop in new[] { CameraProp.Pan, CameraProp.Tilt, CameraProp.Zoom })
            {
                var range = device.Range(prop);
                if (range == null) { report.AppendText($"{prop}: не поддерживается\r\n"); continue; }
                ranges[prop] = range;
                values[prop] = device.Get(prop) ?? range.Default;
                report.AppendText($"{prop}: {range.Min}..{range.Max}; шаг {range.Step}; текущий {values[prop]}; manual={(range.Manual ? "да" : "нет")}\r\n");
            }
            if (ranges.TryGetValue(CameraProp.Zoom, out var zr) && zr.Manual)
            {
                updatingZoom = true;
                zoom.Minimum = zr.Min; zoom.Maximum = Math.Max(zr.Min + 1, zr.Max);
                zoom.SmallChange = Math.Max(1, zr.Step);
                zoom.LargeChange = Math.Max(1, zr.Step * 60);
                zoom.Value = Math.Clamp(values[CameraProp.Zoom], zoom.Minimum, zoom.Maximum);
                zoom.Enabled = true;
                updatingZoom = false;
                UpdateZoomLabel();
            }
            status.Text = ranges.Count == 0 ? "USB PTZ недоступен." : $"Подключено: {info.Name}. Доступно свойств: {ranges.Count}/3.";
        }
        catch (Exception ex)
        {
            ResetState();
            status.Text = "Ошибка подключения";
            report.Text = ex.ToString();
        }
        if (device != null && !zoom.Enabled) zoomDetails.Text = "Камера не поддерживает ручной зум";
        UpdateDeviceControls();
    }
    private bool SetValue(CameraProp p, int target)
    {
        if (device == null || !ranges.TryGetValue(p, out var range) || !range.Manual) return false;
        target = Snap(target, range);
        if (values.TryGetValue(p, out int previous) && previous == target) return true;
        try
        {
            if (!device.Set(p, target)) { status.Text = $"{p}: камера отклонила команду."; return false; }
            values[p] = target;
            if (p is CameraProp.Pan or CameraProp.Tilt) UpdateAxisValues();
            return true;
        }
        catch (Exception ex) { status.Text = $"{p}: {ex.Message}"; return false; }
    }
    private static int Snap(int value, CameraRange r)
    {
        long clamped = Math.Clamp((long)value, r.Min, r.Max);
        long steps = (clamped - r.Min + r.Step / 2L) / r.Step;
        return (int)Math.Clamp(r.Min + steps * r.Step, r.Min, r.Max);
    }
    private static float DeadZone(float value)
    {
        const float threshold = .09f;
        return Math.Sign(value) * Math.Max(0, Math.Abs(value) - threshold) / (1 - threshold);
    }

    private (float X, float Y, bool Keyboard) Direction()
    {
        int left = heldKeys.Contains(Keys.Left) ? 1 : 0;
        int right = heldKeys.Contains(Keys.Right) ? 1 : 0;
        int up = heldKeys.Contains(Keys.Up) ? 1 : 0;
        int down = heldKeys.Contains(Keys.Down) ? 1 : 0;
        return left + right + up + down > 0
            ? (right - left, up - down, true)
            : (DeadZone(stickX), DeadZone(stickY), false);
    }

    private void ResetMotion()
    {
        moveTimer.Stop();
        lastMovement = 0;
        panMotion.Reset();
        tiltMotion.Reset();
    }

    private void UpdateMovement()
    {
        if (networkMode)
        {
            QueueNetworkMotion();
            return;
        }
        var (x, y, _) = Direction();
        if (Disposing || IsDisposed) return;
        if (!MovementEnabled || (x == 0 && y == 0))
        {
            ResetMotion();
            return;
        }
        if (x == 0) panMotion.Reset();
        if (y == 0) tiltMotion.Reset();
        if (moveTimer.Enabled) return;

        SendMovement();
        moveTimer.Start();
    }

    private void SendMovement()
    {
        if (!MovementEnabled || (reactActive && InputExpired))
        {
            StopJoystick();
            return;
        }
        var (x, y, keyboard) = Direction();
        long now = Stopwatch.GetTimestamp();
        double elapsed = lastMovement == 0 ? .025
            : Math.Min(Stopwatch.GetElapsedTime(lastMovement, now).TotalSeconds, .12);
        lastMovement = now;
        Advance(CameraProp.Pan, panMotion.NextSteps(x, speed.Value, elapsed, keyboard));
        Advance(CameraProp.Tilt, tiltMotion.NextSteps(y, speed.Value, elapsed, keyboard));
        PublishReactState();
    }

    private void Advance(CameraProp prop, int steps)
    {
        if (steps == 0 || !ranges.TryGetValue(prop, out var range) || !range.Manual) return;
        int current = values.GetValueOrDefault(prop, range.Default);
        long candidate = current + (long)steps * range.Step;
        SetValue(prop, (int)Math.Clamp(candidate, range.Min, range.Max));
    }
    private void UpdateSpeed() => speedValue.Text = $"{speed.Value} / 10";
    private void QueueZoom()
    {
        if (updatingZoom || !zoom.Enabled) return;
        wantedZoom = zoom.Value;
        zoomPending = true;
        UpdateZoomLabel();
        zoomTimer.Stop(); zoomTimer.Start();
    }
    private void FlushZoom()
    {
        zoomTimer.Stop();
        if (!zoomPending) return;
        zoomPending = false;
        if (SetValue(CameraProp.Zoom, wantedZoom)) UpdateZoomLabel();
        PublishReactState();
    }
    private void UpdateZoomLabel()
    {
        if (!ranges.TryGetValue(CameraProp.Zoom, out var r)) return;
        int percent = r.Max == r.Min ? 0 : (int)Math.Round(100.0 * (zoom.Value - r.Min) / (r.Max - r.Min));
        zoomValue.Text = $"{percent}%";
        zoomDetails.Text = $"Положение в диапазоне (UVC {zoom.Value:N0})";
    }
    private void SetZoomToEdge(bool maximum)
    {
        if (!zoom.Enabled) { status.Text = "Сначала подключите камеру с поддержкой Zoom."; return; }
        zoom.Value = maximum ? zoom.Maximum : zoom.Minimum;
        QueueZoom(); FlushZoom();
    }
    private void Home()
    {
        if (device == null) { status.Text = "Сначала подключитесь к камере."; return; }
        StopJoystick();
        foreach (var prop in new[] { CameraProp.Pan, CameraProp.Tilt })
            if (ranges.TryGetValue(prop, out var r)) SetValue(prop, Math.Clamp(0, r.Min, r.Max));
        status.Text = "Pan/Tilt: установлены координаты 0 (не аппаратная команда Home).";
    }
    bool IMessageFilter.PreFilterMessage(ref Message message)
    {
        const int keyDown = 0x0100, keyUp = 0x0101, systemKeyUp = 0x0105;
        if (reactActive || message.Msg is not (keyDown or keyUp or systemKeyUp) ||
            ActiveForm != this || !joystick.Visible || cameras.DroppedDown ||
            Control.FromChildHandle(message.HWnd)?.FindForm() != this) return false;

        var key = (Keys)(int)message.WParam;
        bool arrow = key is Keys.Left or Keys.Right or Keys.Up or Keys.Down;
        int zoomDirection = key switch
        {
            Keys.Add or Keys.Oemplus => 1,
            Keys.Subtract or Keys.OemMinus => -1,
            _ => 0
        };
        if (!arrow && zoomDirection == 0) return false;

        if (message.Msg != keyDown)
        {
            bool wasHeld = heldKeys.Remove(key);
            if (arrow && wasHeld) UpdateMovement();
            return wasHeld;
        }

        if ((ModifierKeys & (Keys.Control | Keys.Alt)) != Keys.None) return false;
        if (arrow ? !joystick.Enabled : !zoom.Enabled) return false;
        bool autoRepeat = (message.LParam.ToInt64() & (1L << 30)) != 0;
        if (autoRepeat && !heldKeys.Contains(key)) return true;
        bool first = heldKeys.Add(key);
        if (arrow)
        {
            if (first) UpdateMovement();
        }
        else if (first || Stopwatch.GetElapsedTime(lastKeyboardZoom).TotalMilliseconds >= 75)
        {
            lastKeyboardZoom = Stopwatch.GetTimestamp();
            ZoomBy(zoomDirection);
        }
        return true;
    }

    private void ZoomBy(int direction)
    {
        if (!zoom.Enabled || !ranges.TryGetValue(CameraProp.Zoom, out var range)) return;
        long units = Math.Max(range.Step, ((long)range.Max - range.Min) / 50);
        long target = zoom.Value + direction * units;
        zoom.Value = Snap((int)Math.Clamp(target, range.Min, range.Max), range);
        FlushZoom();
    }
}
