using System.Net.NetworkInformation;

namespace AGLPTZ;

internal sealed partial class MainForm
{
    private RemoteServer? remoteServer;
    private RemoteSettings? remoteSettings;
    private RemoteAdapter? listeningAdapter;
    private System.Windows.Forms.Timer? remoteProfileTimer;
    private bool remoteClosing, remoteCheckPending;
    private int remoteSettingsGeneration;
    internal string? RemoteStatus { get; private set; }

    private void StartRemoteLifecycle()
    {
        desktopRemoteSettings = RemoteSettings.Load();
        _ = SetRemoteMonitoringAsync(desktopRemoteSettings);
    }

    private async Task SetRemoteMonitoringAsync(RemoteSettings settings)
    {
        if (remoteClosing) return;
        int generation = DetachRemoteMonitoring();
        await StopRemoteServerAsync();
        if (remoteClosing || generation != remoteSettingsGeneration) return;
        RemoteStatus = null;
        if (!settings.Enabled) { PublishReactState(); return; }
        remoteSettings = settings;

        NetworkChange.NetworkAddressChanged += RemoteNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += RemoteNetworkChanged;
        // A profile-category change need not raise an IP address or availability event.
        remoteProfileTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        remoteProfileTimer.Tick += (_, _) => _ = CheckRemoteNetworkAsync();
        remoteProfileTimer.Start();
        await CheckRemoteNetworkAsync(start: true);
    }

    private void RemoteNetworkChanged(object? sender, EventArgs args)
    {
        try { BeginInvoke(() => _ = CheckRemoteNetworkAsync()); }
        catch (InvalidOperationException) { /* The window handle is closing. */ }
    }

    private async Task CheckRemoteNetworkAsync(bool start = false)
    {
        if (remoteClosing || remoteCheckPending || remoteSettings is not { } settings) return;
        int generation = remoteSettingsGeneration;
        remoteCheckPending = true;
        try
        {
            var adapters = await Task.Run(RemoteNetwork.ListAdapters);
            if (remoteClosing || generation != remoteSettingsGeneration) return;
            if (DirectShowCamera.TestMode)
                adapters = adapters.Select(item => item.Reason is "Сеть не частная" or "Не удалось определить тип сети"
                    ? item with { Category = RemoteNetworkCategory.Private, Reason = null } : item).ToArray();
            desktopAdapters = adapters;

            var candidates = adapters.Where(adapter => string.Equals(adapter.Id, settings.AdapterId,
                StringComparison.OrdinalIgnoreCase)).ToArray();
            var adapter = candidates.FirstOrDefault(item => item.Eligible);
            if (listeningAdapter is { } current && !candidates.Contains(current))
            {
                listeningAdapter = null;
                await StopRemoteServerAsync();
                if (!remoteClosing && generation == remoteSettingsGeneration)
                    RemoteStatus = adapter == null ? candidates.FirstOrDefault()?.Reason ?? "Адаптер недоступен" :
                        "Параметры сети изменились. LAN-сервер остановлен";
            }
            else if (start || listeningAdapter == null)
            {
                if (adapter == null)
                    RemoteStatus = candidates.FirstOrDefault()?.Reason ?? "Адаптер недоступен";
                else
                {
                    remoteServer ??= CreateRemoteServer();
                    var result = await remoteServer.StartAsync(adapter, settings.Port, settings.AllowRouted);
                    if (!remoteClosing && generation == remoteSettingsGeneration)
                    {
                        RemoteStatus = result;
                        if (result == null)
                        {
                            listeningAdapter = adapter;
                            ObserveRemotePairing();
                            UpdateRemoteOffer();
                            if (DirectShowCamera.TestMode)
                            {
                                var pairing = remoteServer.Pairing!;
                                void WriteOffer() => File.WriteAllText(Path.Combine(AppPaths.DataRoot, "test-pairing.txt"),
                                    pairing.CreateOffer().Url);
                                pairing.DevicesChanged += WriteOffer;
                                WriteOffer();
                            }
                        }
                    }
                }
            }
        }
        catch (Exception)
        {
            if (remoteClosing || generation != remoteSettingsGeneration) return;
            listeningAdapter = null;
            await StopRemoteServerAsync();
            if (!remoteClosing && generation == remoteSettingsGeneration) RemoteStatus = "Не удалось определить тип сети";
        }
        finally
        {
            if (generation == remoteSettingsGeneration)
            {
                remoteCheckPending = false;
                if (!remoteClosing) PublishReactState();
            }
        }
    }

    private async void CloseRemoteBeforeExit(object? sender, FormClosingEventArgs args)
    {
        if (PanelMode && !panelExiting && args.CloseReason == CloseReason.UserClosing)
        {
            args.Cancel = true;
            Hide();
            return;
        }
        if (args.Cancel || remoteClosing || remoteServer == null) return;
        if (args.CloseReason is CloseReason.UserClosing or CloseReason.ApplicationExitCall or CloseReason.None)
        {
            args.Cancel = true;
            await StopRemoteLifecycleAsync();
            if (!IsDisposed && !Disposing) BeginInvoke(Close);
        }
        else
        {
            try { StopRemoteLifecycleAsync().Wait(TimeSpan.FromSeconds(1)); }
            catch (AggregateException) { /* Continue closing even if server shutdown fails. */ }
        }
    }

    private async Task StopRemoteLifecycleAsync()
    {
        remoteClosing = true;
        bool remoteWasDriving = remoteOwner != null;
        if (remoteOwner != null) ReleaseLease();
        if (remoteWasDriving && networkCamera != null) DisconnectNetwork();
        leaseTimer?.Dispose();
        leaseTimer = null;
        DetachRemoteMonitoring();
        var stop = StopRemoteServerAsync();
        if (remoteWasDriving)
            await Task.WhenAll(stop, networkRelease).ConfigureAwait(false);
        else await stop.ConfigureAwait(false);
    }

    private int DetachRemoteMonitoring()
    {
        NetworkChange.NetworkAddressChanged -= RemoteNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= RemoteNetworkChanged;
        remoteProfileTimer?.Dispose();
        remoteProfileTimer = null;
        listeningAdapter = null;
        remoteSettings = null;
        remoteCheckPending = false;
        return ++remoteSettingsGeneration;
    }

    private Task StopRemoteServerAsync()
    {
        if (observedPairing != null) observedPairing.DevicesChanged -= RemoteDevicesUpdated;
        observedPairing = null;
        offerQr = null;
        if (remoteOwner != null) ReleaseLease();
        return remoteServer?.StopAsync() ?? Task.CompletedTask;
    }
}
