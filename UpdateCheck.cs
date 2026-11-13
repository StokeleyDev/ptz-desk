using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace AGLPTZ;

internal sealed partial class MainForm
{
    private const string ReleaseSite = "https://github.com/StokeleyDev/ptz-desk/releases";
    private const string AssetName = "PTZ-Desk-Setup-x64.exe";
    private static readonly HttpClient UpdateHttp = new() { Timeout = TimeSpan.FromMinutes(3) };
    private bool checkingUpdate;

    private async Task CheckUpdatesAsync(bool manual)
    {
        if (checkingUpdate || DemoMode || DirectShowCamera.TestMode) return;
        checkingUpdate = true;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://api.github.com/repos/StokeleyDev/ptz-desk/releases/latest");
            request.Headers.UserAgent.ParseAdd("PTZ-Desk");
            using var response = await UpdateHttp.SendAsync(request);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                if (manual) MessageBox.Show(this, "Опубликованных релизов пока нет.", "PTZ Desk");
                return;
            }
            response.EnsureSuccessStatusCode();
            var release = await response.Content.ReadFromJsonAsync<JsonElement>();
            var tag = release.GetProperty("tag_name").GetString() ?? "";
            var current = typeof(MainForm).Assembly.GetName().Version!;
            if (!tag.StartsWith('v') || !Version.TryParse(tag[1..], out var next) ||
                tag != $"v{next.Major}.{next.Minor}.{next.Build}")
                throw new InvalidDataException("Некорректная версия релиза.");
            if (new Version(next.Major, next.Minor, next.Build, 0) <= current)
            {
                if (manual) MessageBox.Show(this, "Установлена актуальная версия.", "PTZ Desk");
                return;
            }
            if (IsDisposed || Disposing || !Visible) return;
            var portable = File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.flag"));
            var notes = release.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
            if (notes.Length > 1200) notes = notes[..1200] + "…";
            if (MessageBox.Show(this, $"Доступна версия {tag}.\n\n{notes}\n\n" +
                (portable ? "Открыть страницу релиза для скачивания portable ZIP?" : "Скачать и запустить установщик?"),
                "Обновление PTZ Desk", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return;
            if (portable)
            {
                Process.Start(new ProcessStartInfo($"{ReleaseSite}/tag/{tag}") { UseShellExecute = true });
                return;
            }

            var asset = release.GetProperty("assets").EnumerateArray().Single(
                item => item.GetProperty("name").GetString() == AssetName);
            var digest = asset.GetProperty("digest").GetString() ?? "";
            if (!digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71)
                throw new InvalidDataException("В релизе отсутствует SHA-256 установщика.");
            var folder = Directory.CreateTempSubdirectory("ptz-update-").FullName;
            var path = Path.Combine(folder, AssetName);
            try
            {
                using var download = await UpdateHttp.GetAsync($"{ReleaseSite}/download/{tag}/{AssetName}");
                download.EnsureSuccessStatusCode();
                var bytes = await download.Content.ReadAsByteArrayAsync();
                if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(digest[7..], StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Контрольная сумма установщика не совпадает.");
                await File.WriteAllBytesAsync(path, bytes);
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch
            {
                File.Delete(path);
                Directory.Delete(folder);
                throw;
            }
        }
        catch (Exception ex)
        {
            if (manual && !IsDisposed) MessageBox.Show(this, $"Не удалось проверить или установить обновление.\n{ex.Message}", "PTZ Desk");
        }
        finally { checkingUpdate = false; }
    }
}
