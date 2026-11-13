using Microsoft.Win32;
using System.Windows.Forms;

namespace AGLPTZ;

internal sealed partial class MainForm
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "AGL PTZ Controller";
    internal static bool PanelMode => Environment.GetCommandLineArgs().Contains("--panel", StringComparer.OrdinalIgnoreCase);
    private NotifyIcon? panelTray;
    private bool panelExiting;
    private string? audioUiError;
    private static string PanelCommand => $"\"{Application.ExecutablePath}\" --panel";

    private bool controllerStarted;

    internal void InitializeHiddenPanel()
    {
        _ = Handle;
        StartPanelTray();
        BeginInvoke(async () => await InitializeControllerAsync());
    }

    private async Task InitializeControllerAsync()
    {
        if (controllerStarted) return;
        controllerStarted = true;
        StartAudio();
        StartRemoteLifecycle();
        StartHub();
        await StartReactInterface();
    }

    private void StartPanelTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть", null, (_, _) => ShowController());
        menu.Items.Add("Выход", null, (_, _) => { panelExiting = true; Close(); });
        panelTray = new NotifyIcon { Icon = Icon, ContextMenuStrip = menu, Visible = true };
        panelTray.DoubleClick += (_, _) => ShowController();
        UpdatePanelTray();
    }

    internal void ShowController()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    private void UpdatePanelTray()
    {
        if (panelTray == null) return;
        var status = "Сервер: " + (listeningAdapter is { } adapter && remoteSettings is { } settings
            ? $"{adapter.Address}:{settings.Port}" : RemoteStatus ?? "Выключен");
        panelTray.Text = status[..Math.Min(63, status.Length)];
    }

    private static bool IsPanelAutostart()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return string.Equals(key?.GetValue(RunName) as string, PanelCommand, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }

    private void SetPanelAutostart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(RunName, PanelCommand);
        else key.DeleteValue(RunName, false);
    }
}
