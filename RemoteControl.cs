using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AGLPTZ;

internal sealed partial class MainForm
{
    private RemotePeer? remoteOwner;
    private string? leaseKind;
    private long leaseActivity;
    private bool remoteMoving;
    private System.Windows.Forms.Timer? leaseTimer;
    private long remoteStateSequence;
    internal int RemoteSessionCount => remoteServer?.SessionCount ?? 0;
    internal (string Kind, string? Name) RemoteOwner => (leaseKind ?? "none", remoteOwner?.Name);
    internal event Action? RemoteControlChanged;

    private RemoteServer CreateRemoteServer()
    {
        var server = new RemoteServer();
        server.CommandReceived += QueueRemoteCommand;
        server.PeerClosed += peer =>
        {
            lock (peer.Inbox) peer.LatestInput = null;
            PostRemote(() =>
            {
                if (ReferenceEquals(remoteOwner, peer)) ReleaseLease();
                PublishReactState();
            });
        };
        server.SessionsChanged += () => PostRemote(() => { RemoteControlChanged?.Invoke(); PublishReactState(); });
        leaseTimer ??= new System.Windows.Forms.Timer { Interval = 50 };
        leaseTimer.Tick += (_, _) => WatchLease();
        leaseTimer.Start();
        return server;
    }

    private void PostRemote(Action action)
    {
        try { BeginInvoke(() => { if (!IsDisposed && !Disposing) action(); }); }
        catch (InvalidOperationException) { }
    }

    private void QueueRemoteCommand(RemotePeer peer, JsonElement command)
    {
        var type = ReadText(command, "type");
        if (type == "input")
        {
            if (!command.TryGetProperty("seq", out var seq) || seq.ValueKind != JsonValueKind.Number ||
                !seq.TryGetInt64(out _)) return;
            lock (peer.Inbox)
            {
                peer.LatestInput = command;
                if (peer.InputPosted) return;
                peer.InputPosted = true;
            }
            PostRemote(() =>
            {
                JsonElement? input;
                lock (peer.Inbox)
                {
                    input = peer.LatestInput;
                    peer.LatestInput = null;
                    peer.InputPosted = false;
                }
                if (input is { } value) ProcessRemote(peer, "input", value);
            });
            return;
        }
        if (type == "stop") remoteServer?.DiscardPendingInputs();
        PostRemote(() => ProcessRemote(peer, type, command));
    }

    private void ProcessRemote(RemotePeer peer, string type, JsonElement command)
    {
        if (remoteClosing || remoteServer?.IsCurrent(peer) != true) return;
        if (type == "hello")
        {
            if (ReadInt(command, "protocol") != 1) { Error(peer, "not_allowed", "Версия протокола не поддерживается"); return; }
            peer.Ready = true;
            PublishRemoteState(peer);
            return;
        }
        if (type == "stop")
        {
            peer.MustRelease = false;
            // A peer that does not hold the lease must not interrupt the current operator.
            if (leaseKind == null || ReferenceEquals(remoteOwner, peer)) StopLeasedMotion();
            PublishReactState();
            return;
        }
        if (!peer.Ready) return;
        if (HandleAudioCommand(type, command) || HandleRoomAudio(type, command)) return;

        var context = new CommandContext(ControlSource.Remote, peer.Id);
        bool bank = type is "bankActivate" or "bankDisconnect";
        var id = ReadText(command, "cameraId");
        bool input = type == "input";
        float x = 0, y = 0, z = 0;
        if (input)
        {
            if (!command.TryGetProperty("seq", out var seq) || !seq.TryGetInt64(out var number) || number <= peer.Sequence) return;
            peer.Sequence = number;
            if (!command.TryGetProperty("stick", out var stick) || stick.ValueKind != JsonValueKind.Object ||
                !Axis(stick, "x", out x) || !Axis(stick, "y", out y) || !Axis(command, "zoomHold", out z)) return;
            if (ReadInt(command, "generation") != cameraGeneration || id != activeCameraId)
            {
                Error(peer, "bad_generation", "Активная камера изменилась");
                return;
            }
            if (x == 0 && y == 0 && z == 0)
            {
                peer.MustRelease = false;
                if (ReferenceEquals(remoteOwner, peer) && remoteMoving) StopLeasedMotion();
                PublishReactState();
                return;
            }
        }
        if (!input && !bank && type is not ("speed" or "zoom" or "flushZoom" or "center" or "wide" or "tight" or "zoomSeek" or "zoomStep" or "presetGo" or "presetSave" or "presetClear"))
        {
            Error(peer, "not_allowed", "Команда недоступна");
            return;
        }
        if (bank && (!cameraBank.Any(c => c.Id == id) || type == "bankDisconnect" && id != activeCameraId))
        {
            peer.Reply(new { type = "ack", requestId = ReadText(command, "requestId"), ok = false });
            return;
        }
        int? numberValue = ReadInt(command, "value");
        if ((type == "speed" && numberValue is not (>= 1 and <= 10)) ||
            (type == "zoomSeek" && numberValue is not (>= 0 and <= 100)) ||
            (type == "zoomStep" && numberValue is not (-1 or 1)) || (type.StartsWith("preset") && numberValue is not (>= 1 and <= 8)) || (type == "zoom" && numberValue == null)) return;
        if (!bank && (activeCameraId == null || ActiveConnectionState != "connected" || virtualUsbReady))
        {
            Error(peer, "not_allowed", "Камера недоступна");
            return;
        }
        if (peer.MustRelease || leaseKind != null && !ReferenceEquals(remoteOwner, peer))
        {
            Error(peer, peer.MustRelease ? "taken_by_pc" : "busy", peer.MustRelease ? "Управление перехвачено ПК" :
                $"Занято: {remoteOwner?.Name ?? "ПК"}");
            return;
        }
        if (type is not ("speed" or "flushZoom"))
        {
            remoteOwner = peer;
            leaseKind = "remote";
            NoteInput(context);
            leaseActivity = Stopwatch.GetTimestamp();
        }
        else if (ReferenceEquals(remoteOwner, peer)) leaseActivity = Stopwatch.GetTimestamp();
        if (bank)
        {
            StopLeasedMotion();
            Dispatch(context, type, id: id);
            peer.Reply(new { type = "ack", requestId = ReadText(command, "requestId"), ok = true });
        }
        else if (input)
        {
            remoteMoving = true;
            // Both axes and spring zoom are applied before the network queue runs.
            HandleUiCommand(context, JsonSerializer.SerializeToElement(new {
                type = "remoteInput", x, y, z, cameraId = activeCameraId, generation = cameraGeneration,
            }));
        }
        else if (type == "zoomStep")
            Dispatch(context, networkMode ? "networkZoom" : "key", networkMode ? numberValue : numberValue == 1 ? 187 : 189, down: 1);
        else Dispatch(context, type == "zoomSeek" ? "networkZoomSeek" : type, numberValue);
        PublishReactState();
    }

    private static bool Axis(JsonElement value, string name, out float axis)
    {
        axis = 0;
        return value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number &&
            item.TryGetSingle(out axis) && float.IsFinite(axis) && axis is >= -1 and <= 1;
    }

    private void Dispatch(CommandContext source, string type, int? value = null, string? id = null, int? down = null) =>
        HandleUiCommand(source, JsonSerializer.SerializeToElement(new { type, value, id, down, cameraId = activeCameraId, generation = cameraGeneration }));

    private static void Error(RemotePeer peer, string code, string message) => peer.Reply(new { type = "error", code, message });

    private void TakeDesktopLease()
    {
        if (remoteOwner is { } peer)
        {
            StopLeasedMotion();
            peer.MustRelease = true;
            Error(peer, "taken_by_pc", "Управление перехвачено ПК");
        }
        remoteOwner = null;
        leaseKind = "desktop";
        inputOwner = DesktopCommand;
        leaseActivity = Stopwatch.GetTimestamp();
    }

    private void StopLeasedMotion()
    {
        if (remoteOwner is { } peer)
            lock (peer.Inbox) peer.LatestInput = null;
        if (remoteOwner != null)
        {
            zoomTimer.Stop();
            zoomPending = false;
            networkZoomTarget = null;
        }
        StopJoystick();
        remoteMoving = false;
        leaseActivity = Stopwatch.GetTimestamp();
    }

    private void ReleaseLease()
    {
        StopLeasedMotion();
        remoteOwner = null;
        leaseKind = null;
        inputOwner = null;
    }

    private void WatchLease()
    {
        if (leaseKind == null) return;
        if (remoteOwner is { } peer && remoteServer?.IsCurrent(peer) != true) ReleaseLease();
        else if (remoteMoving && InputExpired) StopLeasedMotion();
        else if (!remoteMoving && Stopwatch.GetElapsedTime(leaseActivity).TotalSeconds >= 3 &&
            (remoteOwner != null || (stickX == 0 && stickY == 0 && heldKeys.Count == 0 && networkZoomHold == 0)))
            ReleaseLease();
        PublishReactState();
    }

    private void PublishRemoteState(RemotePeer? initial = null)
    {
        if (RemoteSessionCount == 0 || remoteServer == null) return;
        bool changed = false;
        remoteServer.Broadcast(peer =>
        {
            var content = JsonSerializer.Serialize(BuildRemoteState(peer));
            if (peer.LastProjection == content && !ReferenceEquals(initial, peer)) return;
            peer.LastProjection = content;
            peer.State(System.Text.Encoding.UTF8.GetBytes(
                $"{{\"type\":\"state\",\"stateSeq\":{++remoteStateSequence},{content[1..]}"));
            changed = true;
        });
        if (changed) RemoteControlChanged?.Invoke();
    }

    private object BuildRemoteState(RemotePeer peer)
    {
        var owner = RemoteOwner;
        return new
        {
            audio = RemoteAudio(), rooms = HubRemoteState(), activeId = activeCameraId, generation = cameraGeneration, speed = speed.Value,
            canPreset = CanPreset, presets = PresetFlags(),
            canMove = networkMode ? networkCamera?.CanMove == true : joystick.Enabled,
            canZoom = networkMode ? networkCamera?.CanZoom == true || networkCamera?.CanSeekZoom == true : zoom.Enabled,
            zoom = new { mode = networkMode ? networkCamera?.CanSeekZoom == true ? "absolute" :
                networkCamera?.CanZoom == true ? "spring" : "none" : zoom.Enabled ? "absolute" : "none",
                percent = networkMode ? networkCamera?.ZoomPercent : null, value = networkMode ? (int?)null : zoom.Value,
                min = networkMode ? (int?)null : zoom.Minimum, max = networkMode ? (int?)null : zoom.Maximum },
            pan = values.TryGetValue(CameraProp.Pan, out int pan) ? (int?)pan : null,
            tilt = values.TryGetValue(CameraProp.Tilt, out int tilt) ? (int?)tilt : null,
            connectionState = ActiveConnectionState,
            status = ActiveConnectionState switch
            {
                "connected" => "Подключена", "connecting" => "Подключение…",
                "error" when networkMode && networkStatus.Contains("пароль", StringComparison.OrdinalIgnoreCase) => "Неверный пароль",
                "error" => "Недоступна", _ => "Не подключена",
            },
            owner = new { kind = owner.Kind, name = owner.Name,
                isYou = owner.Kind == "remote" && remoteOwner?.Id == peer.Id },
            cameras = cameraBank.Select((camera, i) => new {
                id = camera.Id,
                name = SafeCameraName(camera.Name, camera.Target, i),
                kind = camera.Kind, color = camera.Color,
                status = camera.Id == activeCameraId ? ActiveConnectionState : cameraErrors.ContainsKey(camera.Id) ? "error" : "idle",
                isVirtual = camera.Kind == "usb" && (camera.Virtual || availableUsb.Any(u => u.MonikerDisplayName == camera.Target && u.IsVirtual)),
            }),
        };
    }

    private static string SafeCameraName(string name, string target, int index) =>
        name.Equals(target, StringComparison.OrdinalIgnoreCase) ||
        (!string.IsNullOrEmpty(target) && name.Contains(target, StringComparison.OrdinalIgnoreCase)) ||
        name.Contains("@device:", StringComparison.OrdinalIgnoreCase) ||
        System.Net.IPAddress.TryParse(name, out _) || name.Contains("://", StringComparison.Ordinal) ||
        Regex.IsMatch(name, @"(?:\d{1,3}\.){3}\d{1,3}") ? $"Камера {index + 1}" : name;
}
