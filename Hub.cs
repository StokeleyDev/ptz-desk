using System.Text.Json;

namespace AGLPTZ;

internal sealed partial class MainForm
{
    private readonly List<RoomLink> roomLinks = [];
    private readonly Dictionary<(string Room, string Target), (JsonElement? Volume, JsonElement? Muted)> pendingRoomAudio = new();
    private System.Windows.Forms.Timer? roomTimer;
    private bool hubBusy;
    private string? hubError;

    private void StartHub()
    {
        roomTimer = new() { Interval = 100 };
        roomTimer.Tick += (_, _) =>
        {
            roomTimer.Stop();
            foreach (var ((room, target), change) in pendingRoomAudio)
            {
                var link = roomLinks.FirstOrDefault(item => item.Id == room);
                if (link == null) continue;
                if (change.Volume is { } volume) link.SendAudio("audioVolume", target, volume);
                if (change.Muted is { } muted) link.SendAudio("audioMute", target, muted);
            }
            pendingRoomAudio.Clear();
        };
        foreach (var record in RoomStore.Load()) AttachRoom(record);
        if (DirectShowCamera.TestMode && Environment.GetEnvironmentVariable("AGL_PTZ_TEST_HUB_LINK") is { Length: > 0 } link &&
            roomLinks.Count == 0)
            _ = AddRoomAsync("Тестовая аудитория", link);
    }

    private void StopHub()
    {
        roomTimer?.Dispose();
        roomTimer = null;
        foreach (var link in roomLinks) link.Dispose();
        roomLinks.Clear();
    }

    private void AttachRoom(RoomRecord record)
    {
        var link = new RoomLink(record);
        link.Changed += () => PostRemote(PublishReactState);
        roomLinks.Add(link);
    }

    private void SaveRooms()
    {
        var records = roomLinks.Select(link => link.Record).ToArray();
        try { RoomStore.Save(records); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            hubError = "Не удалось сохранить список аудиторий";
        }
    }

    private async Task AddRoomAsync(string name, string link)
    {
        if (hubBusy) return;
        hubError = null;
        name = name.Trim();
        if (name.Length == 0) name = $"Аудитория {roomLinks.Count + 1}";
        if (name.Length > 40) name = name[..40];
        if (!RoomLink.TryParseLink(link, out var origin, out var code, out var problem)) hubError = problem;
        else if (roomLinks.Count >= RoomStore.MaxRooms) hubError = "Слишком много аудиторий";
        else if (roomLinks.Any(item => item.Record.Origin == origin)) hubError = "Эта панель уже добавлена";
        else if (listeningAdapter is { } adapter && remoteSettings is { } settings &&
            origin == $"http://{adapter.Address}:{settings.Port}") hubError = "Это адрес самого контроллера, нужна ссылка с панели";
        else
        {
            hubBusy = true;
            PublishReactState();
            try
            {
                var (room, error) = await RoomLink.PairAsync(name, origin, code, Environment.MachineName, CancellationToken.None);
                if (room == null) hubError = error;
                else
                {
                    AttachRoom(room);
                    SaveRooms();
                }
            }
            finally { hubBusy = false; }
        }
        PublishReactState();
    }

    private bool HandleHubUi(string? type, JsonElement data)
    {
        if (HandleRoomAudio(type, data)) return true;
        switch (type)
        {
            case "hubAdd": _ = AddRoomAsync(ReadText(data, "name"), ReadText(data, "link")); return true;
            case "hubClearError": hubError = null; break;
            case "hubRemove":
                if (roomLinks.FirstOrDefault(item => item.Id == ReadText(data, "id")) is { } link)
                {
                    roomLinks.Remove(link);
                    link.Dispose();
                    SaveRooms();
                }
                break;
            case "hubRename":
                var name = ReadText(data, "name").Trim();
                if (name.Length is > 0 and <= 40 && roomLinks.FirstOrDefault(item => item.Id == ReadText(data, "id")) is { } room)
                {
                    room.Record = room.Record with { Name = name };
                    SaveRooms();
                }
                break;
            default: return false;
        }
        PublishReactState();
        return true;
    }

    // Shared by the PC UI and paired phones: set volume / mute of a room's speakerphone.
    private bool HandleRoomAudio(string? type, JsonElement command)
    {
        if (type is not ("roomAudioVolume" or "roomAudioMute")) return false;
        var room = ReadText(command, "roomId");
        var target = ReadText(command, "target");
        if (target is not ("speaker" or "mic") || roomLinks.All(item => item.Id != room) ||
            !command.TryGetProperty("value", out var value)) return true;
        var pending = pendingRoomAudio.GetValueOrDefault((room, target));
        if (type == "roomAudioVolume" && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var level) && level is >= 0 and <= 100)
            pendingRoomAudio[(room, target)] = (value.Clone(), pending.Muted);
        else if (type == "roomAudioMute" && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            pendingRoomAudio[(room, target)] = (pending.Volume, value.Clone());
        else return true;
        roomTimer?.Start();
        return true;
    }

    private object HubDesktopState() => new
    {
        busy = hubBusy, error = hubError,
        rooms = roomLinks.Select(link => new
        {
            id = link.Id, name = link.Record.Name, address = link.Record.Origin,
            online = link.View.Online, status = link.View.Status, audio = link.View.Audio,
        }),
    };

    // Phones never receive panel addresses or keys, only names and audio state.
    private object[] HubRemoteState() => roomLinks.Select(link => (object)new
    {
        id = link.Id, name = link.Record.Name, online = link.View.Online, audio = link.View.Audio,
    }).ToArray();
}
