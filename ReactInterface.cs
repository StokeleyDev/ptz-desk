using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AGLPTZ;

internal sealed partial class MainForm
{
    private const string ReactHost = "ptz-controller.example";
    private static readonly bool DemoMode = Environment.GetCommandLineArgs().Contains("--demo", StringComparer.OrdinalIgnoreCase);
    private static string ReactPage => "https://" + ReactHost + "/index.html" + (DemoMode ? "?demo=1" : "");
    private WebView2? reactView;
    private bool reactActive;
    private bool usbConnecting;
    private string? lastPublishedState;
    private readonly TaskCompletionSource reactReady = new();
    private static bool HasReactInterface => typeof(MainForm).Assembly.GetManifestResourceInfo("AGLPTZ.UI.index.html") != null;

    private async Task StartReactInterface()
    {
        try
        {
            var browserFolder = Environment.GetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER");
            if (string.IsNullOrWhiteSpace(CoreWebView2Environment.GetAvailableBrowserVersionString(browserFolder)))
                throw new WebView2RuntimeNotFoundException();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowMissingWebView();
            return;
        }

        if (!HasReactInterface)
        {
            if (nativeShell != null) nativeShell.Visible = true;
            return;
        }

        var view = new WebView2 { Dock = DockStyle.Fill, Visible = false, DefaultBackgroundColor = BackColor };
        reactView = view;
        Controls.Add(view);
        view.BringToFront();
        try
        {
            await InitializeReactViewAsync(view).WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (WebView2RuntimeNotFoundException) when (!IsDisposed && !Disposing)
        {
            ShowNativeInterface(view, "");
            ShowMissingWebView();
        }
        catch (Exception) when (!IsDisposed && !Disposing)
        {
            ShowNativeInterface(view, "Не удалось открыть новый интерфейс.");
        }
        catch (Exception) when (IsDisposed || Disposing) { }
    }

    private async Task InitializeReactViewAsync(WebView2 view)
    {
        var profile = Path.Combine(AppPaths.DataRoot, "WebView2");
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
        if (IsDisposed || Disposing || reactView != view) return;
        await view.EnsureCoreWebView2Async(environment);
        if (IsDisposed || Disposing || reactView != view) return;
        var core = view.CoreWebView2;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.AddWebResourceRequestedFilter($"https://{ReactHost}/*", CoreWebView2WebResourceContext.All,
            CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += (_, args) =>
        {
            var uri = new Uri(args.Request.Uri);
            var name = uri.AbsolutePath.TrimStart('/');
            string? type = null;
            var stream = name is "phone.html" or "phone.js" or "phone.css" ? null : UiAssets.Open(name, out type);
            args.Response = stream == null
                ? environment.CreateWebResourceResponse(null, 404, "Not Found", "")
                : environment.CreateWebResourceResponse(stream, 200, "OK", $"Content-Type: {type}");
        };
        core.NavigationStarting += (_, args) => args.Cancel = args.Uri != ReactPage;
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.PermissionRequested += (_, args) =>
        {
            args.SavesInProfile = false;
            args.State = !DemoMode && args.PermissionKind == CoreWebView2PermissionKind.Camera &&
                Uri.TryCreate(args.Uri, UriKind.Absolute, out var origin) &&
                origin.GetLeftPart(UriPartial.Authority) == "https://" + ReactHost
                ? CoreWebView2PermissionState.Allow
                : CoreWebView2PermissionState.Deny;
        };
        core.ProcessFailed += (_, _) =>
        {
            if (IsDisposed || Disposing) return;
            reactReady.TrySetResult();
            ShowNativeInterface(view, "Новый интерфейс завершился с ошибкой.");
        };
        core.NavigationCompleted += (_, args) =>
        {
            if (args.IsSuccess || IsDisposed || Disposing) return;
            reactReady.TrySetResult();
            ShowNativeInterface(view, "Не удалось загрузить файлы интерфейса.");
        };
        core.WebMessageReceived += ReceiveReactMessage;
        core.Navigate(ReactPage);
        await reactReady.Task;
    }

    private void ShowNativeInterface(WebView2 view, string message)
    {
        if (reactView != view) return;
        StopJoystick();
        SetNetworkMode(false);
        reactActive = false;
        reactView = null;
        reactReady.TrySetResult();
        view.Dispose();
        if (nativeShell != null) nativeShell.Visible = true;
        status.Text = string.Join(" ", new[] { message, AppPaths.Notice }.Where(text => !string.IsNullOrEmpty(text)));
    }

    private void ShowMissingWebView()
    {
        const string message = "Новый интерфейс недоступен: не установлен Microsoft Edge WebView2 Runtime. Запустите AGL.PTZ.Dependencies.cmd";
        if (nativeShell != null) nativeShell.Visible = true;
        status.Text = AppPaths.Notice is { } notice ? $"{message} {notice}" : message;
        var banner = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(12),
            BackColor = ConsoleTheme.Surface, ColumnCount = 2 };
        banner.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(550, 0), Text = message,
            ForeColor = ConsoleTheme.Lime, Font = ConsoleTheme.Face(11) }, 0, 0);
        const string file = "AGL.PTZ.Dependencies.cmd";
        var installer = new[] { Path.Combine(AppContext.BaseDirectory, file),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Dependencies", file)) }.FirstOrDefault(File.Exists);
        var button = new Button { AutoSize = true, Text = "Запустить установщик", Enabled = installer != null };
        button.Click += (_, _) =>
        {
            if (installer == null) return;
            try { Process.Start(new ProcessStartInfo(installer) { UseShellExecute = true }); }
            catch (Exception error) { status.Text = $"Не удалось запустить установщик: {error.Message}"; }
        };
        banner.Controls.Add(button, 1, 0);
        Controls.Add(banner);
        banner.BringToFront();
    }

    private void ReceiveReactMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (args.Source != ReactPage || IsDisposed || Disposing || reactView == null) return;
        try
        {
            using var message = JsonDocument.Parse(args.WebMessageAsJson);
            var data = message.RootElement;
            if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String) return;
            var action = type.GetString();
            if (DemoMode && action != "ready") return;
            if (action == "ready")
            {
                StopJoystick();
                reactActive = true;
                reactView!.Visible = true;
                reactView.BringToFront();
                reactView.Focus();
                if (nativeShell != null) nativeShell.Visible = false;
                lastPublishedState = null;
                reactReady.TrySetResult();
                _ = CheckUpdatesAsync(false);
            }
            if (!reactActive) return;
            if (action == "checkUpdates") { _ = CheckUpdatesAsync(true); return; }
            if (HandleAudioUi(action, data) || HandleHubUi(action, data)) return;
            HandleUiCommand(DesktopCommand, data);
        }
        catch (JsonException) { }
    }

    private static int? ReadInt(JsonElement data, string key) =>
        data.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.Number &&
        item.TryGetInt32(out int value) ? value : null;

    private static float ReadFloat(JsonElement data, string key) =>
        data.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.Number &&
        item.TryGetSingle(out float value) && float.IsFinite(value) ? value : 0;

    private void ConnectReactUsb()
    {
        usbConnecting = true;
        PublishReactState();
        try { Connect(); }
        finally { usbConnecting = false; }
    }

    private void PublishReactState()
    {
        UpdatePanelTray();
        PublishRemoteState();
        if (!reactActive || reactView?.CoreWebView2 is not { } core) return;
        var state = JsonSerializer.Serialize(BuildUiSnapshot());
        if (state == lastPublishedState) return;
        core.PostWebMessageAsJson(state);
        lastPublishedState = state;
    }

    private object BuildUiSnapshot() => new
    {
        remote = BuildDesktopRemoteState(),
        audio = DesktopAudio(),
        hub = HubDesktopState(),
        cameras = cameras.Items.Cast<CameraInfo>().Select(camera => camera.Name),
        bank = CameraBankState(),
        selected = cameras.SelectedIndex,
        selectedConnected = virtualUsbReady || device != null && connectedCameraIndex == cameras.SelectedIndex,
        connection = connection.Text,
        connectionState = ActiveConnectionState,
        connectedName = networkMode ? networkCameraName :
            connectedCameraIndex >= 0 && connectedCameraIndex < cameras.Items.Count
                ? (cameras.Items[connectedCameraIndex] as CameraInfo)?.Name : null,
        status = status.Text,
        report = report.Text,
        canPreset = CanPreset, presets = PresetFlags(),
        canMove = networkMode ? networkCamera?.CanMove == true : joystick.Enabled,
        canZoom = networkMode ? networkCamera?.CanZoom == true || networkCamera?.CanSeekZoom == true : zoom.Enabled,
        network = new { active = networkMode, searching = searchingNetwork,
            connecting = networkConnect != null, connected = networkCamera != null,
            name = networkCameraName, address = networkCameraAddress, username = networkUsername,
            zoomMode = networkCamera?.CanSeekZoom == true ? "absolute" :
                networkCamera?.CanZoom == true ? "spring" : "none",
            zoomPercent = networkCamera?.ZoomPercent,
            status = networkStatus, cameras = networkResults.Select(camera => new { camera.Name, camera.Address, camera.Endpoint }) },
        pan = values.TryGetValue(CameraProp.Pan, out int pan) ? (int?)pan : null,
        tilt = values.TryGetValue(CameraProp.Tilt, out int tilt) ? (int?)tilt : null,
        speed = speed.Value,
        zoom = zoom.Value,
        zoomMin = zoom.Minimum,
        zoomMax = zoom.Maximum,
        zoomLabel = zoomValue.Text,
        zoomDetails = zoomDetails.Text,
    };
}
