using NAudio.CoreAudioApi;
using System.Collections.Concurrent;
using System.Text.Json;

namespace AGLPTZ;

internal sealed record AudioSettings(string Speaker = "", string Mic = "", bool Panel = false)
{
    private static string PathName => Path.Combine(AppPaths.DataRoot, "audio-settings.json");
    internal static AudioSettings Load()
    {
        try { return JsonSerializer.Deserialize<AudioSettings>(File.ReadAllText(PathName)) ?? new(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    internal void Save()
    {
        Directory.CreateDirectory(AppPaths.DataRoot);
        var temp = PathName + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this));
        File.Move(temp, PathName, true);
    }
}

internal sealed record AudioEndpoint(string Name, int? Volume, bool? Muted, bool Present, bool Adjustable = true);
internal sealed record AudioChoice(string Id, string Name);
internal sealed record AudioSnapshot(AudioEndpoint Speaker, AudioEndpoint Mic, AudioChoice[] Speakers, AudioChoice[] Mics);
internal interface IAudioBackend : IDisposable
{
    void Configure(AudioSettings settings);
    void Set(string target, int? volume, bool? muted);
}

internal sealed class WindowsAudioBackend : IAudioBackend
{
    private readonly BlockingCollection<Action> queue = new();
    private readonly Thread worker;
    private readonly Action<AudioSnapshot?> changed;
    private AudioSettings settings;
    private MMDeviceEnumerator? enumerator;
    private MMDevice? speaker, mic;
    private int refreshQueued, updateQueued;

    internal WindowsAudioBackend(AudioSettings settings, Action<AudioSnapshot?> changed)
    {
        this.settings = settings;
        this.changed = changed;
        worker = new Thread(Run) { IsBackground = true, Name = "Core Audio" };
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
    }

    private void Run()
    {
        // Retry: at logon the Windows Audio service may not be ready yet (panel autostart).
        while (!queue.IsAddingCompleted)
        {
            try
            {
                using var devices = new MMDeviceEnumerator();
                enumerator = devices;
                using var events = devices.CreateNotificationClient(false);
                events.DeviceAdded += (_, _) => Refresh();
                events.DeviceRemoved += (_, _) => Refresh();
                events.DeviceStateChanged += (_, _) => Refresh();
                events.DefaultDeviceChanged += (_, _) => Refresh();
                RefreshEndpoints();
                while (!queue.IsCompleted)
                {
                    if (queue.TryTake(out var job, 10000))
                    {
                        try { job(); } catch (Exception) { Publish(); }
                    }
                    // Safety net for missed notifications: re-bind a device that is missing.
                    else if (!queue.IsCompleted && (speaker == null || mic == null)) RefreshEndpoints();
                }
            }
            catch (Exception) { changed(null); }
            finally { speaker?.Dispose(); mic?.Dispose(); speaker = mic = null; enumerator = null; }
            if (!queue.IsAddingCompleted) Thread.Sleep(3000);
        }
    }

    private void Enqueue(Action action)
    {
        try { queue.Add(action); } catch (InvalidOperationException) { }
    }

    private void Refresh()
    {
        if (Interlocked.Exchange(ref refreshQueued, 1) != 0) return;
        Enqueue(() => { Interlocked.Exchange(ref refreshQueued, 0); RefreshEndpoints(); });
    }

    private void VolumeChanged(AudioVolumeNotificationData _)
    {
        if (Interlocked.Exchange(ref updateQueued, 1) != 0) return;
        Enqueue(() => { Interlocked.Exchange(ref updateQueued, 0); Publish(); });
    }

    private MMDevice? Bind(DataFlow flow, string? id)
    {
        MMDevice? endpoint = null;
        try
        {
            endpoint = string.IsNullOrEmpty(id) ? enumerator!.GetDefaultAudioEndpoint(flow, Role.Communications)
                : enumerator!.GetDevice(id);
            if (endpoint.State == DeviceState.Active && endpoint.DataFlow == flow)
            {
                endpoint.AudioEndpointVolume.OnVolumeNotification += VolumeChanged;
                return endpoint;
            }
        }
        catch (Exception) { }
        endpoint?.Dispose();
        return null;
    }

    private void RefreshEndpoints()
    {
        speaker?.Dispose(); mic?.Dispose();
        speaker = Bind(DataFlow.Render, settings.Speaker);
        mic = Bind(DataFlow.Capture, settings.Mic);
        Publish();
    }

    private static AudioEndpoint Read(MMDevice? endpoint)
    {
        try
        {
            if (endpoint != null)
            {
                var volume = endpoint.AudioEndpointVolume;
                // Some microphones report a 0 dB range: the level is fixed in hardware.
                bool adjustable = volume.VolumeRange.MaxDecibels - volume.VolumeRange.MinDecibels > 0.01f;
                return new(endpoint.FriendlyName, (int)Math.Round(volume.MasterVolumeLevelScalar * 100), volume.Mute, true, adjustable);
            }
        }
        catch (Exception) { }
        return new("Устройство не найдено", null, null, false);
    }

    private AudioChoice[] Choices(DataFlow flow) => enumerator!.EnumerateAudioEndPoints(flow, DeviceState.Active)
        .Select(device => { using (device) return new AudioChoice(device.ID, device.FriendlyName); }).ToArray();

    private void Publish()
    {
        try { changed(new(Read(speaker), Read(mic), Choices(DataFlow.Render), Choices(DataFlow.Capture))); }
        catch (Exception) { changed(null); }
    }

    public void Configure(AudioSettings next) => Enqueue(() => { settings = next; RefreshEndpoints(); });
    public void Set(string target, int? volume, bool? muted) => Enqueue(() =>
    {
        try
        {
            var endpoint = (target == "speaker" ? speaker : mic)?.AudioEndpointVolume;
            if (endpoint != null)
            {
                if (volume is int value) endpoint.MasterVolumeLevelScalar = value / 100f;
                if (muted is bool mute) endpoint.Mute = mute;
            }
        }
        finally { Publish(); }
    });
    public void Dispose() => queue.CompleteAdding();
}

