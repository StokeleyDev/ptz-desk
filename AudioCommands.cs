using System.Text.Json;

namespace AGLPTZ;

internal sealed partial class MainForm
{
    private AudioSettings audioSettings = AudioSettings.Load();
    private IAudioBackend? audioBackend;
    private AudioSnapshot? audioSnapshot;
    private readonly Dictionary<string, (int? Volume, bool? Muted)> pendingAudio = new();
    private System.Windows.Forms.Timer? audioTimer;

    private void StartAudio()
    {
        void Changed(AudioSnapshot? snapshot) => PostRemote(() =>
        {
            audioSnapshot = snapshot;
            PublishReactState();
        });
        audioBackend = DirectShowCamera.TestMode || DemoMode
            ? new FakeAudioBackend(Changed) : new WindowsAudioBackend(audioSettings, Changed);
        audioTimer = new() { Interval = 100 };
        audioTimer.Tick += (_, _) =>
        {
            audioTimer.Stop();
            foreach (var (target, change) in pendingAudio)
                audioBackend.Set(target, change.Volume, change.Muted);
            pendingAudio.Clear();
        };
    }

    private bool HandleAudioCommand(string? type, JsonElement command)
    {
        if (type is not ("audioVolume" or "audioMute")) return false;
        var target = ReadText(command, "target");
        if (target is not ("speaker" or "mic") || !command.TryGetProperty("value", out var value)) return true;
        var pending = pendingAudio.GetValueOrDefault(target);
        if (type == "audioVolume" && value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out var level) && level is >= 0 and <= 100)
            pendingAudio[target] = (level, pending.Muted);
        else if (type == "audioMute" && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            pendingAudio[target] = (pending.Volume, value.GetBoolean());
        else return true;
        audioTimer?.Start();
        return true;
    }

    private bool HandleAudioUi(string? type, JsonElement data)
    {
        if (HandleAudioCommand(type, data)) return true;
        if (type is not ("audioDevice" or "audioAutostart")) return false;
        try
        {
            audioUiError = null;
            if (type == "audioAutostart" && data.TryGetProperty("value", out var value) &&
                value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                SetPanelAutostart(value.GetBoolean());
            if (type == "audioDevice" && audioSnapshot is { } state)
            {
                var target = ReadText(data, "target");
                var id = ReadText(data, "id") ?? "";
                var choices = target == "speaker" ? state.Speakers : state.Mics;
                if (target is ("speaker" or "mic") && (id.Length == 0 || choices.Any(choice => choice.Id == id)))
                {
                    var next = target == "speaker" ? audioSettings with { Speaker = id } : audioSettings with { Mic = id };
                    next.Save();
                    pendingAudio.Remove(target);
                    audioSettings = next;
                    audioBackend?.Configure(next);
                }
            }
        }
        catch (Exception) { audioUiError = "Не удалось сохранить настройку звука"; }
        PublishReactState();
        return true;
    }

    private object DesktopAudio() => new
    {
        speaker = audioSnapshot?.Speaker, mic = audioSnapshot?.Mic,
        speakers = audioSnapshot?.Speakers, mics = audioSnapshot?.Mics,
        settings = new { speaker = audioSettings.Speaker, mic = audioSettings.Mic },
        autostart = IsPanelAutostart(), error = audioUiError,
    };

    private object? RemoteAudio() => audioSnapshot is { } state ? new
    {
        speaker = new { state.Speaker.Present, state.Speaker.Volume, state.Speaker.Muted, state.Speaker.Adjustable },
        mic = new { state.Mic.Present, state.Mic.Volume, state.Mic.Muted, state.Mic.Adjustable },
    } : null;
}

internal sealed class FakeAudioBackend : IAudioBackend
{
    private readonly Action<AudioSnapshot?> changed;
    private AudioEndpoint speaker = new("Динамики (демо)", 62, false, true);
    private AudioEndpoint mic = new("Микрофон (демо)", 48, false, true);

    internal FakeAudioBackend(Action<AudioSnapshot?> changed)
    {
        this.changed = changed;
        Publish();
    }

    private void Publish() => changed(new(speaker, mic, [new("demo-speaker", speaker.Name)], [new("demo-mic", mic.Name)]));
    public void Configure(AudioSettings settings) => Publish();
    public void Set(string target, int? volume, bool? muted)
    {
        ref var endpoint = ref (target == "speaker" ? ref speaker : ref mic);
        endpoint = endpoint with { Volume = volume ?? endpoint.Volume, Muted = muted ?? endpoint.Muted };
        Publish();
    }
    public void Dispose() { }
}
