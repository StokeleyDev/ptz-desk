using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AGLPTZ;

internal sealed partial class MainForm
{
    private sealed record CameraProfile(string Id, string Name, string Kind, string Target,
        string Color, string Username, string Secret, int Speed = 5, bool Virtual = false, PresetSlot[]? Presets = null);

    private static string CameraBankPath => Path.Combine(AppPaths.DataRoot, "cameras.json");
    private static readonly string[] CameraColors = ["amber", "teal", "blue", "rose", "violet", "lime", "slate"];
    private List<CameraProfile> cameraBank = [];
    private readonly Dictionary<string, string> cameraErrors = [];
    private CameraInfo[] availableUsb = [];
    private string? activeCameraId;
    private bool virtualUsbReady;
    private int cameraGeneration;
    private int cameraRequest;
    private string cameraBankStatus = "";
    private Task networkRelease = Task.CompletedTask;

    private void LoadCameraBank()
    {
        try
        {
            if (File.Exists(CameraBankPath))
                cameraBank = JsonSerializer.Deserialize<List<CameraProfile>>(File.ReadAllText(CameraBankPath)) ?? [];
        }
        catch (Exception) { cameraBankStatus = "Не удалось загрузить список камер."; }
        RefreshUsbChoices();
        if (DirectShowCamera.TestMode && Environment.GetEnvironmentVariable("AGL_PTZ_TEST_AUDIO") == "1") return;
        if (DirectShowCamera.TestMode)
            cameraBank = [new("test-usb", "Тестовая USB-камера", "usb", availableUsb[0].MonikerDisplayName,
                "teal", "", "")];
    }

    private void RefreshUsbChoices()
    {
        try { availableUsb = DirectShowCamera.List().ToArray(); }
        catch (Exception) { cameraBankStatus = "Не удалось найти USB-камеры."; }
    }

    private void SaveCameraBank()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CameraBankPath)!);
            string temp = CameraBankPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(cameraBank));
            File.Move(temp, CameraBankPath, true);
            cameraBankStatus = "";
        }
        catch (Exception) { cameraBankStatus = "Не удалось сохранить список камер."; }
    }

    private string ActiveConnectionState => networkMode
        ? networkConnect != null ? "connecting" : networkCamera != null ? "connected" : networkFailed ? "error" : "idle"
        : usbConnecting ? "connecting" : device != null || virtualUsbReady ? "connected" :
            status.Text.StartsWith("Ошибка", StringComparison.Ordinal) ? "error" : "idle";

    private object CameraBankState()
    {
        string activeState = ActiveConnectionState;
        if (activeCameraId is { } active && activeState == "error")
            cameraErrors[active] = networkMode ? networkStatus :
                status.Text == "Ошибка: USB-камера недоступна." ? status.Text : "Не удалось подключиться к USB-камере. Проверьте подключение.";
        return new {
            activeId = activeCameraId, generation = cameraGeneration, request = cameraRequest,
            message = string.Join(" ", new[] { cameraBankStatus, AppPaths.Notice }.Where(text => !string.IsNullOrEmpty(text))),
            usb = availableUsb.Select(item => new { name = item.Name, target = item.MonikerDisplayName, isVirtual = item.IsVirtual }),
            profiles = cameraBank.Select(item => new {
                id = item.Id, name = item.Name, kind = item.Kind, target = item.Target, color = item.Color,
                username = item.Username, hasPassword = item.Secret.Length > 0,
                isVirtual = item.Kind == "usb" && (item.Virtual || availableUsb.Any(usb => usb.MonikerDisplayName == item.Target && usb.IsVirtual)),
                state = item.Id == activeCameraId ? activeState : cameraErrors.ContainsKey(item.Id) ? "error" : "idle",
                error = cameraErrors.GetValueOrDefault(item.Id),
                presets = Enumerable.Range(1, PresetCount).Select(slot => item.Presets?.Any(p => p.Slot == slot) == true).ToArray(),
            }),
        };
    }

    private void ReleaseActiveCamera()
    {
        ++cameraGeneration;
        DisconnectNetwork();
        networkMode = false;
        networkFailed = false;
        ResetState();
        virtualUsbReady = false;
        activeCameraId = null;
        status.Text = "Камера отключена.";
        report.Text = "";
    }

    private async Task ActivateProfileAsync(CameraProfile profile)
    {
        if (activeCameraId == profile.Id && ActiveConnectionState is "connected" or "connecting") return;
        ReleaseActiveCamera();
        activeCameraId = profile.Id;
        int generation = cameraGeneration;
        cameraErrors.Remove(profile.Id);
        speed.Value = Math.Clamp(profile.Speed, 1, 10);
        if (profile.Kind == "onvif")
        {
            networkMode = true;
            networkStatus = "Подключение ONVIF…";
            try
            {
                string password = profile.Secret.Length == 0 ? "" : Encoding.UTF8.GetString(
                    ProtectedData.Unprotect(Convert.FromBase64String(profile.Secret), null, DataProtectionScope.CurrentUser));
                await ConnectNetworkAsync(profile.Target, null, profile.Username, password);
            }
            catch (CryptographicException)
            {
                if (cameraGeneration != generation || activeCameraId != profile.Id) return;
                networkFailed = true;
                networkStatus = "Не удалось прочитать пароль Windows. Введите его заново.";
            }
        }
        else
        {
            RefreshUsbChoices();
            int index = Array.FindIndex(availableUsb, item => item.MonikerDisplayName == profile.Target);
            if (index >= 0)
            {
                cameras.Items.Clear();
                cameras.Items.AddRange(availableUsb);
                cameras.SelectedIndex = index;
                if (availableUsb[index].IsVirtual)
                {
                    virtualUsbReady = true;
                    status.Text = "Виртуальная камера может зависеть от запущенной программы";
                }
                else ConnectReactUsb();
            }
            else
            {
                cameras.SelectedIndex = -1;
                status.Text = "Ошибка: USB-камера недоступна.";
            }
        }
        if (cameraGeneration == generation && activeCameraId == profile.Id && !IsDisposed && !Disposing)
            PublishReactState();
    }

    private bool HandleCameraBank(string? action, JsonElement data)
    {
        var id = ReadText(data, "id");
        var existing = cameraBank.Find(item => item.Id == id);
        switch (action)
        {
            case "bankUsb": RefreshUsbChoices(); break;
            case "bankSearch": _ = DiscoverNetworkAsync(); break;
            case "bankActivate":
                cameraRequest = ReadInt(data, "request") ?? cameraRequest;
                if (existing != null) _ = ActivateProfileAsync(existing);
                break;
            case "bankDisconnect":
                cameraRequest = ReadInt(data, "request") ?? cameraRequest;
                if (existing != null && activeCameraId == id) ReleaseActiveCamera();
                cameraErrors.Remove(id);
                break;
            case "bankDelete":
                if (existing == null) break;
                if (activeCameraId == id) ReleaseActiveCamera();
                cameraBank.Remove(existing);
                cameraErrors.Remove(id);
                SaveCameraBank();
                break;
            case "bankReorder":
                int destination = cameraBank.FindIndex(item => item.Id == ReadText(data, "before"));
                if (existing == null || destination < 0 || id == cameraBank[destination].Id) break;
                cameraBank.Remove(existing);
                cameraBank.Insert(destination, existing);
                SaveCameraBank();
                break;
            case "bankRename":
                var name = ReadText(data, "name").Trim();
                if (existing == null || name.Length is 0 or > 80) break;
                cameraBank[cameraBank.IndexOf(existing)] = existing with { Name = name };
                SaveCameraBank();
                break;
            case "bankSave":
                SaveProfile(existing, data);
                break;
            default: return false;
        }
        PublishReactState();
        return true;
    }

    private void SaveProfile(CameraProfile? existing, JsonElement data)
    {
        var kind = ReadText(data, "kind");
        var name = ReadText(data, "name").Trim();
        var target = ReadText(data, "target").Trim();
        var color = ReadText(data, "color");
        var username = kind == "onvif" ? ReadText(data, "username").Trim() : "";
        var password = ReadText(data, "password");
        if (kind is not ("usb" or "onvif") || name.Length is 0 or > 80 ||
            target.Length is 0 or > 2048 || username.Length > 512 || password.Length > 2048 ||
            !CameraColors.Contains(color)) return;
        try
        {
            string secret = kind != "onvif" ? "" : password.Length > 0 ?
                Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser)) :
                ReadInt(data, "clearPassword") == 1 || existing?.Target != target ? "" : existing.Secret;
            var profile = new CameraProfile(existing?.Id ?? Guid.NewGuid().ToString("N"),
                name, kind, target, color, username, secret, existing?.Speed ?? 5,
                kind == "usb" && (availableUsb.Any(item => item.MonikerDisplayName == target && item.IsVirtual) ||
                    existing is { Virtual: true } && existing.Target == target),
                existing?.Target == target ? existing.Presets : null);
            if (existing == null) cameraBank.Add(profile);
            else
            {
                if (activeCameraId == profile.Id) ReleaseActiveCamera();
                cameraBank[cameraBank.IndexOf(existing)] = profile;
                cameraErrors.Remove(profile.Id);
            }
            SaveCameraBank();
        }
        catch (CryptographicException) { cameraBankStatus = "Windows не смогла защитить пароль."; }
    }

    private void SaveActiveCameraSpeed(int rate)
    {
        int index = cameraBank.FindIndex(item => item.Id == activeCameraId);
        if (index < 0 || cameraBank[index].Speed == rate) return;
        cameraBank[index] = cameraBank[index] with { Speed = rate };
        SaveCameraBank();
    }
}
