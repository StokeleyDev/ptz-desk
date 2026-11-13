using System.Text.Json;

namespace AGLPTZ;

// Slot 1..8 of the active camera. USB cameras have no presets of their own, so the pan/tilt/zoom
// position is kept in the camera profile. ONVIF cameras keep the position themselves (SetPreset)
// and the profile only remembers the preset token that this program created.
internal sealed record PresetSlot(int Slot, string? Token = null, int? Pan = null, int? Tilt = null, int? Zoom = null);

internal sealed partial class MainForm
{
    private const int PresetCount = 8;

    private CameraProfile? ActiveProfile => cameraBank.Find(item => item.Id == activeCameraId);

    private bool CanPreset => activeCameraId != null && ActiveConnectionState == "connected" && !virtualUsbReady &&
        (networkMode ? networkCamera != null : device != null && ranges.Count > 0);

    private bool PresetSaved(int slot) => ActiveProfile?.Presets?.Any(item => item.Slot == slot) == true;

    private bool[] PresetFlags() => Enumerable.Range(1, PresetCount).Select(PresetSaved).ToArray();

    private void StorePreset(int slot, PresetSlot? value)
    {
        int index = cameraBank.FindIndex(item => item.Id == activeCameraId);
        if (index < 0) return;
        var presets = (cameraBank[index].Presets ?? []).Where(item => item.Slot != slot).ToList();
        if (value != null) presets.Add(value);
        cameraBank[index] = cameraBank[index] with { Presets = presets.OrderBy(item => item.Slot).ToArray() };
        SaveCameraBank();
    }

    private bool HandlePreset(string? action, JsonElement data)
    {
        if (action is not ("presetSave" or "presetGo" or "presetClear")) return false;
        if (ReadInt(data, "value") is not (>= 1 and <= PresetCount) || !CanPreset) return true;
        int slot = ReadInt(data, "value")!.Value;
        var existing = ActiveProfile?.Presets?.FirstOrDefault(item => item.Slot == slot);
        if (action == "presetGo" && existing == null) return true;
        if (action == "presetClear")
        {
            if (existing?.Token is { } removed && networkMode)
                RunNetworkPreset((camera, token) => camera.RemovePresetAsync(removed, token), "Камера не удалила пресет.");
            StorePreset(slot, null);
            status.Text = $"Пресет {slot} удалён.";
        }
        else if (networkMode) NetworkPreset(action, slot, existing);
        else UsbPreset(action, slot, existing);
        return true;
    }

    private void UsbPreset(string action, int slot, PresetSlot? existing)
    {
        if (action == "presetSave")
        {
            int? Read(CameraProp prop) => values.TryGetValue(prop, out int value) ? value : null;
            var saved = new PresetSlot(slot, null, Read(CameraProp.Pan), Read(CameraProp.Tilt), Read(CameraProp.Zoom));
            if (saved.Pan == null && saved.Tilt == null && saved.Zoom == null) { status.Text = "Нет данных о положении камеры."; return; }
            StorePreset(slot, saved);
            status.Text = $"Пресет {slot} сохранён.";
            return;
        }
        StopJoystick();
        foreach (var (prop, value) in new[] { (CameraProp.Pan, existing!.Pan), (CameraProp.Tilt, existing.Tilt), (CameraProp.Zoom, existing.Zoom) })
            if (value is int target) SetValue(prop, target);
        if (ranges.ContainsKey(CameraProp.Zoom) && values.TryGetValue(CameraProp.Zoom, out int zoomValue) && zoom.Enabled)
        {
            updatingZoom = true;
            zoom.Value = Math.Clamp(zoomValue, zoom.Minimum, zoom.Maximum);
            updatingZoom = false;
            UpdateZoomLabel();
        }
        status.Text = $"Пресет {slot}.";
    }

    private void NetworkPreset(string action, int slot, PresetSlot? existing)
    {
        if (action == "presetGo")
        {
            var target = existing!.Token!;
            RunNetworkPreset((camera, token) => camera.GotoPresetAsync(target, token), "Камера не перешла на пресет.", () =>
            {
                zoomReads = 10;
                QueueNetworkMotion();
            });
            return;
        }
        var activeId = activeCameraId;
        RunNetworkPreset(async (camera, token) =>
        {
            var created = await camera.SetPresetAsync($"AGL {slot}", existing?.Token, token);
            if (activeCameraId == activeId && created != null) StorePreset(slot, new PresetSlot(slot, created));
        }, "Камера не сохранила пресет.", () => status.Text = $"Пресет {slot} сохранён в камере.");
    }

    // Runs after any pending network command, so the Stop of the stick always comes first.
    private void RunNetworkPreset(Func<OnvifCamera, CancellationToken, Task> operation, string failure, Action? done = null)
    {
        if (networkCamera is not { } camera) return;
        StopJoystick();
        networkCommand = NetworkPresetAsync(camera, networkCommand, operation, failure, done);
    }

    private async Task NetworkPresetAsync(OnvifCamera camera, Task previous, Func<OnvifCamera, CancellationToken, Task> operation,
        string failure, Action? done)
    {
        try { await previous; }
        catch (Exception) { /* The earlier command already reported its failure. */ }
        if (camera != networkCamera || IsDisposed || Disposing) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await operation(camera, timeout.Token);
            if (camera == networkCamera && !IsDisposed && !Disposing) done?.Invoke();
        }
        catch (Exception)
        {
            if (camera != networkCamera || IsDisposed || Disposing) return;
            networkStatus = failure;
        }
        if (camera == networkCamera && !IsDisposed && !Disposing) PublishReactState();
    }
}
