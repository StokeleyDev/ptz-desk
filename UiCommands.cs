using System.Diagnostics;
using System.Text.Json;

namespace AGLPTZ;

internal enum ControlSource { Desktop, Remote }
internal readonly record struct CommandContext(ControlSource Source, string ClientId);

internal sealed partial class MainForm
{
    private const int DesktopInputTimeoutMs = 350;
    private const int RemoteInputTimeoutMs = 300;
    private static readonly CommandContext DesktopCommand = new(ControlSource.Desktop, "desktop");
    private static readonly HashSet<string> RemoteActions = new(StringComparer.Ordinal)
    {
        "bankActivate", "bankDisconnect", "stop", "heartbeat", "stick", "key",
        "speed", "zoom", "flushZoom", "center", "wide", "tight",
        "networkZoom", "networkZoomHold", "networkZoomSeek", "remoteInput", "presetGo", "presetSave", "presetClear",
    };

    private CommandContext? inputOwner;
    private long lastOwnerInput;
    private CommandContext CurrentInputOwner => inputOwner ?? DesktopCommand;

    private bool CanDrive(CommandContext context) => context.Source == ControlSource.Desktop ? ContainsFocus :
        remoteOwner is { } peer && peer.Id == context.ClientId && remoteServer?.IsCurrent(peer) == true;
    private bool MovementEnabled => networkMode
        ? networkCamera?.CanMove == true && CanDrive(CurrentInputOwner)
        : joystick.Enabled && (reactActive || CurrentInputOwner.Source == ControlSource.Remote
            ? CanDrive(CurrentInputOwner) : joystick.Visible);
    private bool InputExpired => Stopwatch.GetElapsedTime(lastOwnerInput).TotalMilliseconds >
        (CurrentInputOwner.Source == ControlSource.Remote ? RemoteInputTimeoutMs : DesktopInputTimeoutMs);

    private long NoteInput(CommandContext context)
    {
        inputOwner = context;
        return lastOwnerInput = Stopwatch.GetTimestamp();
    }

    private void HandleUiCommand(CommandContext context, JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String) return;
        var action = type.GetString();
        if (context.Source == ControlSource.Desktop && action is ("remoteOpen" or "remoteClose" or "remoteNewCode" or
            "remoteRevoke" or "remoteSetEnabled" or "remoteSetAdapter" or "remoteSetPort" or "remoteSetRouted"))
        {
            _ = HandleRemoteSettingsAsync(action, data);
            return;
        }
        if (context.Source == ControlSource.Remote &&
            (action == null || !RemoteActions.Contains(action) ||
                action is not ("stop" or "speed" or "bankActivate" or "bankDisconnect") && !CanDrive(context))) return;
        if (context.Source == ControlSource.Desktop && remoteServer?.Pairing != null)
        {
            if (remoteOwner != null && (action is "stop" or "heartbeat" || action == "key" && ReadInt(data, "down") != 1)) return;
            bool cameraSwitch = action is "bankActivate" or "bankDisconnect";
            bool routed = activeCameraId != null && ReadText(data, "cameraId") == activeCameraId &&
                ReadInt(data, "generation") == cameraGeneration && ActiveConnectionState == "connected" && !virtualUsbReady;
            if (cameraSwitch || routed && ContainsFocus && (action switch
            {
                "stick" => ReadFloat(data, "x") != 0 || ReadFloat(data, "y") != 0,
                "networkZoomHold" => ReadFloat(data, "value") != 0,
                "key" => ReadInt(data, "down") == 1,
                "zoom" or "networkZoom" or "networkZoomSeek" or "center" or "wide" or "tight" or "presetGo" => true,
                _ => false,
            })) TakeDesktopLease();
            else if (remoteOwner != null && routed && action != "ready") return;
            if (leaseKind == "desktop" && (action == "stop" ||
                action == "stick" && ReadFloat(data, "x") == 0 && ReadFloat(data, "y") == 0 ||
                action == "networkZoomHold" && ReadFloat(data, "value") == 0 ||
                action == "key" && ReadInt(data, "down") == 0))
                leaseActivity = Stopwatch.GetTimestamp();
        }
        if (HandleCameraBank(action, data)) return;
        if (action != "ready" && (activeCameraId == null || ReadText(data, "cameraId") != activeCameraId ||
            ReadInt(data, "generation") != cameraGeneration || ActiveConnectionState != "connected" || virtualUsbReady)) return;

        switch (action)
        {
            case "networkZoom" when networkMode && ReadInt(data, "value") is -1 or 1:
                PulseNetworkZoom(context, ReadInt(data, "value")!.Value);
                break;
            case "networkZoomHold" when networkMode:
                SetNetworkZoomHold(context, ReadFloat(data, "value"));
                break;
            case "networkZoomSeek" when networkMode && ReadInt(data, "value") is int percent:
                SeekNetworkZoom(percent);
                break;
            case "center" or "wide" or "tight" or "zoom" or "flushZoom"
                when networkMode: break;
            case "center": Home(); break;
            case "wide": SetZoomToEdge(false); break;
            case "tight": SetZoomToEdge(true); break;
            case "speed" when ReadInt(data, "value") is int rate:
                speed.Value = Math.Clamp(rate, speed.Minimum, speed.Maximum);
                SaveActiveCameraSpeed(speed.Value);
                break;
            case "zoom" when zoom.Enabled && ReadInt(data, "value") is int position:
                zoom.Value = Math.Clamp(position, zoom.Minimum, zoom.Maximum);
                break;
            case "flushZoom": FlushZoom(); break;
            case "stop": StopJoystick(); break;
            case "heartbeat" when CanDrive(CurrentInputOwner):
                NoteInput(context);
                break;
            case "stick" when MovementEnabled:
                NoteInput(context);
                stickX = Math.Clamp(ReadFloat(data, "x"), -1, 1);
                stickY = Math.Clamp(ReadFloat(data, "y"), -1, 1);
                UpdateMovement();
                break;
            case "remoteInput" when context.Source == ControlSource.Remote:
                stickX = ReadFloat(data, "x");
                stickY = ReadFloat(data, "y");
                if (networkMode) SetNetworkZoomHold(context, ReadFloat(data, "z"));
                UpdateMovement();
                break;
            case "presetSave" or "presetGo" or "presetClear": HandlePreset(action, data); break;
            case "key": HandleReactKey(context, data); break;
        }
        PublishReactState();
    }

    private void HandleReactKey(CommandContext context, JsonElement data)
    {
        var key = ReadInt(data, "value");
        if (key is 187 or 189)
        {
            if (CanDrive(CurrentInputOwner) && ReadInt(data, "down") == 1 &&
                Stopwatch.GetElapsedTime(lastKeyboardZoom).TotalMilliseconds >= 75)
            {
                lastKeyboardZoom = Stopwatch.GetTimestamp();
                if (networkMode) PulseNetworkZoom(context, key == 187 ? 1 : -1);
                else ZoomBy(key == 187 ? 1 : -1);
            }
            return;
        }
        if (key is not (37 or 38 or 39 or 40)) return;
        if (ReadInt(data, "down") == 1 && MovementEnabled)
        {
            NoteInput(context);
            heldKeys.Add((Keys)key.Value);
        }
        else heldKeys.Remove((Keys)key.Value);
        UpdateMovement();
    }
}
