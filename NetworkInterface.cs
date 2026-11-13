using System.Diagnostics;
using System.Text.Json;

namespace AGLPTZ;

internal sealed partial class MainForm
{
    private readonly System.Windows.Forms.Timer networkTimer = new() { Interval = 100 };
    private OnvifCamera? networkCamera;
    private CancellationTokenSource? networkConnect;
    private Task networkCommand = Task.CompletedTask;
    private NetworkCamera[] networkResults = [];
    private bool networkMode, searchingNetwork, networkMoving, networkStopping;
    private string networkStatus = "";
    private string? networkCameraName;
    private string? networkCameraAddress, networkUsername;
    private bool networkFailed;
    private int networkZoomDirection;
    private long networkZoomStart;
    private float networkZoomHold;
    private bool networkZoomMoving;
    private int? networkZoomTarget;
    private int zoomReads;
    private long lastNetworkMove, lastNetworkSeek;

    private void SetNetworkMode(bool enabled)
    {
        if (networkMode == enabled) return;
        StopJoystick();
        networkMode = enabled;
        if (enabled)
        {
            ResetState();
            networkFailed = false;
            networkStatus = "Выберите камеру или введите IP.";
            _ = DiscoverNetworkAsync();
        }
        else
        {
            DisconnectNetwork();
            RefreshDevices();
        }
    }

    private async Task DiscoverNetworkAsync()
    {
        if (searchingNetwork) return;
        searchingNetwork = true;
        if (networkCamera == null && networkConnect == null && !networkFailed)
            networkStatus = "Поиск камер ONVIF…";
        PublishReactState();
        try
        {
            networkResults = await OnvifCamera.DiscoverAsync().WaitAsync(TimeSpan.FromSeconds(8));
            if (networkCamera == null && networkConnect == null && !networkFailed)
                networkStatus = networkResults.Length == 0
                    ? "Камеры не найдены. Введите IP вручную."
                    : $"Найдено камер: {networkResults.Length}.";
        }
        catch (Exception)
        {
            networkResults = [];
            if (networkCamera == null && networkConnect == null && !networkFailed)
                networkStatus = "Поиск камер недоступен. Введите IP вручную.";
        }
        finally
        {
            searchingNetwork = false;
            if (!IsDisposed && !Disposing) PublishReactState();
        }
    }

    private async Task ConnectNetworkAsync(string address, int? selection, string username, string password)
    {
        DisconnectNetwork();
        using var attempt = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        networkConnect = attempt;
        Uri? endpoint = null;
        networkFailed = false;
        networkStatus = "Подключение ONVIF…";
        PublishReactState();
        try
        {
            await networkRelease.WaitAsync(attempt.Token);
            var selected = selection is int index && index >= 0 && index < networkResults.Length &&
                networkResults[index].Address == address ? networkResults[index] : null;
            endpoint = selected == null ? OnvifCamera.ManualEndpoint(address) : new Uri(selected.Endpoint);
            var camera = await OnvifCamera.ConnectAsync(endpoint, username, password, attempt.Token);
            if (networkConnect != attempt || IsDisposed || Disposing)
            {
                camera.Dispose();
                return;
            }
            networkCamera = camera;
            networkCameraName = selected?.Name ??
                networkResults.FirstOrDefault(item => item.Address == endpoint.Authority)?.Name ?? endpoint.Authority;
            networkCameraAddress = address;
            networkUsername = username;
            networkStatus = $"Подключено по ONVIF: {endpoint.Authority}.";
        }
        catch (Exception error)
        {
            if (networkConnect != attempt) return;
            var explanation = error is ArgumentException or InvalidOperationException ? error.Message :
                endpoint == null ? "Камера не ответила вовремя." :
                await OnvifDiagnostics.ExplainFailureAsync(endpoint, error, attempt.IsCancellationRequested);
            if (networkConnect != attempt || IsDisposed || Disposing) return;
            networkFailed = true;
            networkStatus = explanation;
        }
        finally
        {
            if (networkConnect == attempt)
            {
                networkConnect = null;
                if (!IsDisposed && !Disposing) PublishReactState();
            }
        }
    }

    private void DisconnectNetwork()
    {
        networkConnect?.Cancel();
        networkConnect = null;
        bool needsStop = networkMoving || networkStopping || networkZoomMoving;
        StopJoystick();
        networkTimer.Stop();
        var camera = networkCamera;
        networkCamera = null;
        networkCameraName = null;
        networkCameraAddress = networkUsername = null;
        if (camera != null) networkRelease = ReleaseNetworkAsync(camera, networkCommand, needsStop);
        networkMoving = networkStopping = false;
        networkZoomMoving = false;
        networkZoomHold = 0;
        networkZoomDirection = 0;
        networkZoomTarget = null;
        zoomReads = 0;
        lastNetworkMove = lastNetworkSeek = 0;
        networkCommand = Task.CompletedTask;
    }

    private static async Task ReleaseNetworkAsync(OnvifCamera camera, Task pending, bool stop)
    {
        using (camera)
        {
            try { await pending; }
            catch (Exception) { /* A failed command must still be followed by Stop. */ }
            try
            {
                if (stop)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
                    await camera.StopAsync(timeout.Token);
                }
            }
            catch (Exception) { /* A sent move also has a camera-side deadline. */ }
        }
    }

    private void StopNetworkMotion()
    {
        networkZoomDirection = 0;
        networkZoomHold = 0;
        if (networkCamera != null && networkMoving)
        {
            networkStopping = true;
            QueueNetworkMotion();
        }
    }

    private void QueueNetworkMotion()
    {
        if (networkCamera is not { } camera || !networkCommand.IsCompleted) return;
        var (x, y, _) = Direction();
        if (!CanDrive(CurrentInputOwner) || InputExpired ||
            networkStopping)
        {
            x = y = 0;
            networkZoomDirection = 0;
            networkZoomHold = 0;
        }
        if (!camera.CanMove) x = y = 0;
        if (Stopwatch.GetElapsedTime(networkZoomStart).TotalMilliseconds >= 240) networkZoomDirection = 0;
        float z = camera.CanZoom ? (networkZoomHold != 0 ? networkZoomHold : networkZoomDirection) : 0;
        bool moving = x != 0 || y != 0 || z != 0;
        if (networkZoomMoving && z == 0 && moving)
        {
            networkZoomMoving = false;
            networkCommand = RunNetworkZoomAsync(camera, camera.StopZoomAsync);
            return;
        }
        if (!moving && !networkMoving)
        {
            networkStopping = false;
            if (networkZoomTarget is int target)
            {
                if (Stopwatch.GetElapsedTime(lastNetworkSeek).TotalMilliseconds < 100) return;
                networkZoomTarget = null;
                lastNetworkSeek = Stopwatch.GetTimestamp();
                zoomReads = 10;
                networkCommand = RunNetworkZoomAsync(camera, token => camera.SeekZoomAsync(target, token));
                return;
            }
            if (zoomReads > 0 && camera.CanSeekZoom)
            {
                zoomReads--;
                networkCommand = RunNetworkZoomAsync(camera, camera.UpdateZoomAsync);
                return;
            }
            networkTimer.Stop();
            return;
        }
        if (!networkTimer.Enabled) networkTimer.Start();
        if (moving && networkMoving && Stopwatch.GetElapsedTime(lastNetworkMove).TotalMilliseconds < 165) return;
        lastNetworkMove = Stopwatch.GetTimestamp();
        networkZoomMoving = z != 0;
        if (networkZoomMoving && camera.CanSeekZoom) zoomReads = 10;
        networkCommand = SendNetworkMotionAsync(camera, x, y, z, moving);
    }

    private async Task SendNetworkMotionAsync(OnvifCamera camera, float x, float y, float z, bool moving)
    {
        networkMoving = moving;
        float scale = speed.Value / 10f;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(650));
            if (moving) await camera.MoveAsync(x * scale, y * scale, z * scale, timeout.Token);
            else await camera.StopAsync(timeout.Token);
        }
        catch (Exception)
        {
            if (camera != networkCamera || IsDisposed || Disposing) return;
            networkFailed = true;
            networkStatus = "Камера не подтвердила команду. Переподключитесь.";
            // Cancel input and do not resume queued motion after a failed command.
            DisconnectNetwork();
            PublishReactState();
        }
        finally
        {
            if (camera == networkCamera && !moving) networkStopping = false;
            if (camera == networkCamera && networkZoomMoving && networkZoomHold == 0 &&
                networkZoomDirection == 0 && !IsDisposed && !Disposing)
                BeginInvoke(QueueNetworkMotion);
        }
    }

    private async Task RunNetworkZoomAsync(OnvifCamera camera, Func<CancellationToken, Task> operation)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(650));
            await operation(timeout.Token);
        }
        catch (Exception)
        {
            if (camera != networkCamera || IsDisposed || Disposing) return;
            networkStatus = "Камера не подтвердила команду зума.";
        }
        if (camera == networkCamera && !IsDisposed && !Disposing) PublishReactState();
    }

    private void SetNetworkZoomHold(CommandContext context, float value)
    {
        if (networkCamera is not { CanZoom: true, CanSeekZoom: false } || (value != 0 && !CanDrive(context))) return;
        networkZoomDirection = 0;
        networkZoomHold = Math.Clamp(value, -1, 1);
        NoteInput(context);
        QueueNetworkMotion();
    }

    private void SeekNetworkZoom(int percent)
    {
        if (networkCamera?.CanSeekZoom != true) return;
        networkZoomTarget = Math.Clamp(percent, 0, 100);
        if (!networkTimer.Enabled) networkTimer.Start();
        QueueNetworkMotion();
    }

    private void PulseNetworkZoom(CommandContext context, int direction)
    {
        if (networkCamera is not { } camera || !CanDrive(context)) return;
        if (!camera.CanZoom)
        {
            if (camera.ZoomPercent is int position)
                SeekNetworkZoom((networkZoomTarget ?? position) + direction * 2);
            return;
        }
        networkZoomStart = NoteInput(context);
        networkZoomDirection = direction;
        QueueNetworkMotion();
    }

    private static string ReadText(JsonElement data, string key) =>
        data.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String
            ? item.GetString() ?? "" : "";
}
