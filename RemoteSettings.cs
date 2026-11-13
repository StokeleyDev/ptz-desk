using System.Text.Json;

namespace AGLPTZ;

internal sealed record RemoteSettings(bool Enabled = false, string AdapterId = "", int Port = 8787, bool AllowRouted = false)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private static string FilePath => Path.Combine(AppPaths.DataRoot, "remote-settings.json");

    internal static bool ValidPort(int port) => port is >= 1025 and <= 65535;

    internal static RemoteSettings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<RemoteSettings>(File.ReadAllText(FilePath), Options);
            return settings is { AdapterId: not null } && ValidPort(settings.Port) ? settings : new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    internal void Save()
    {
        Directory.CreateDirectory(AppPaths.DataRoot);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, Options));
            File.Move(temporary, FilePath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
