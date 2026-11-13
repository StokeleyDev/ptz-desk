using System.Diagnostics;
using System.Text.Json;

namespace AGLPTZ;

internal sealed partial class MainForm
{
    private RemoteSettings desktopRemoteSettings = new();
    private RemoteAdapter[] desktopAdapters = [];
    private bool remoteDialogOpen, applyingRemoteSettings;
    private long offerStarted;
    private string? offerQr, offerUrl;
    private RemotePairing? observedPairing;

    private object BuildDesktopRemoteState()
    {
        var owner = RemoteOwner;
        var pairing = remoteServer?.Pairing;
        var settings = desktopRemoteSettings;
        int secondsLeft = offerQr == null ? 0 : Math.Max(0, (int)Math.Ceiling(120 - Stopwatch.GetElapsedTime(offerStarted).TotalSeconds));
        return new {
            enabled = settings.Enabled, allowRouted = settings.AllowRouted, adapterId = settings.AdapterId, port = settings.Port, applying = applyingRemoteSettings,
            adapters = desktopAdapters.Select(a => new { id = a.Id, name = a.Name, address = a.Address.ToString(),
                category = a.Category?.ToString(), eligible = a.Eligible, reason = a.Reason }),
            address = listeningAdapter is { } a ? $"http://{a.Address}:{settings.Port}" : null,
            status = !settings.Enabled ? "Выключено" : RemoteStatus ??
                (listeningAdapter == null ? "Запуск…" : RemoteSessionCount == 0 ? "Ожидает подключений" : $"Подключено: {RemoteSessionCount}"),
            sessions = RemoteSessionCount, owner = new { kind = owner.Kind, name = owner.Name },
            devices = pairing?.ListDevices().Select(d => new { id = d.Id, name = d.Name, createdAt = d.CreatedAt, lastSeen = d.LastSeen }),
            offer = !remoteDialogOpen || listeningAdapter == null || offerQr == null ? null :
                new { qr = offerQr, secondsLeft, url = offerUrl },
        };
    }

    private void UpdateRemoteOffer()
    {
        offerQr = offerUrl = null;
        if (!remoteDialogOpen || listeningAdapter == null || remoteServer?.Pairing is not { } pairing) return;
        var offer = pairing.CreateOffer();
        offerStarted = Stopwatch.GetTimestamp();
        offerUrl = offer.Url;
        offerQr = "data:image/png;base64," + Convert.ToBase64String(RemotePairing.CreateQrPng(offer.Url));
    }

    private void RemoteDevicesUpdated() => PostRemote(() =>
    {
        if (remoteDialogOpen && observedPairing?.HasOffer != true) offerQr = null;
        PublishReactState();
    });

    private void ObserveRemotePairing()
    {
        if (observedPairing != null) observedPairing.DevicesChanged -= RemoteDevicesUpdated;
        observedPairing = remoteServer?.Pairing;
        if (observedPairing != null) observedPairing.DevicesChanged += RemoteDevicesUpdated;
    }

    private async Task HandleRemoteSettingsAsync(string action, JsonElement data)
    {
        if (remoteClosing) return;
        try
        {
            switch (action)
            {
                case "remoteOpen":
                    remoteDialogOpen = true;
                    desktopAdapters = await Task.Run(RemoteNetwork.ListAdapters);
                    UpdateRemoteOffer();
                    break;
                case "remoteClose":
                    remoteDialogOpen = false;
                    offerQr = null;
                    remoteServer?.Pairing?.CancelOffer();
                    break;
                case "remoteNewCode": UpdateRemoteOffer(); break;
                case "remoteRevoke": remoteServer?.Pairing?.Revoke(ReadText(data, "id")); break;
                default:
                    if (applyingRemoteSettings) return;
                    var current = desktopRemoteSettings;
                    var next = action switch
                    {
                        "remoteSetEnabled" when ReadInt(data, "value") is 0 or 1 =>
                            current with { Enabled = ReadInt(data, "value") == 1 },
                        "remoteSetRouted" when ReadInt(data, "value") is 0 or 1 =>
                            current with { AllowRouted = ReadInt(data, "value") == 1 },
                        "remoteSetAdapter" when desktopAdapters.Any(a => a.Id == ReadText(data, "id")) =>
                            current with { AdapterId = ReadText(data, "id") },
                        "remoteSetPort" when ReadInt(data, "value") is int port && RemoteSettings.ValidPort(port) =>
                            current with { Port = port },
                        _ => current,
                    };
                    if (next == current) return;
                    applyingRemoteSettings = true;
                    try
                    {
                        await Task.Run(next.Save);
                        if (remoteClosing) return;
                        desktopRemoteSettings = next;
                        offerQr = null;
                        RemoteStatus = null;
                        PublishReactState();
                        await SetRemoteMonitoringAsync(next);
                    }
                    finally { applyingRemoteSettings = false; }
                    break;
            }
        }
        catch (Exception) { RemoteStatus = "Не удалось применить настройки удалённого доступа"; }
        if (!IsDisposed && !Disposing) PublishReactState();
    }
}
