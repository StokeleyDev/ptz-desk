using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace AGLPTZ;

// Hub: this controller connects to other controllers running in "Panel" mode (one per room)
// as an ordinary paired device and mirrors their audio state. Cameras stay on this PC.
internal sealed record RoomRecord(string Id, string Name, string Origin, string Secret);
internal sealed record RoomView(bool Online, string Status, JsonElement? Audio);

internal static class RoomStore
{
    internal const int MaxRooms = 30;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string PathName => Path.Combine(AppPaths.DataRoot, "rooms.json");

    internal static List<RoomRecord> Load()
    {
        try
        {
            var records = JsonSerializer.Deserialize<List<RoomRecord>>(File.ReadAllText(PathName), Json);
            return records?.Where(Valid).Take(MaxRooms).ToList() ?? [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    private static bool Valid(RoomRecord? record) => record is { Id.Length: > 0, Name: not null, Secret.Length: > 0 } &&
        TryParseOrigin(record.Origin, out _);

    internal static void Save(IEnumerable<RoomRecord> records)
    {
        Directory.CreateDirectory(AppPaths.DataRoot);
        var temp = PathName + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(records, Json));
            File.Move(temp, PathName, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal static string Protect(string token) => Convert.ToBase64String(
        ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser));

    internal static string? Unprotect(string secret)
    {
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(secret), null, DataProtectionScope.CurrentUser)); }
        catch (Exception error) when (error is CryptographicException or FormatException) { return null; }
    }

    internal static bool TryParseOrigin(string? origin, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var value) || value.Scheme != "http" ||
            !IPAddress.TryParse(value.Host, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            !RemoteNetwork.IsRfc1918(ip) || value.IsDefaultPort || !RemoteSettings.ValidPort(value.Port)) return false;
        uri = value;
        return true;
    }
}

internal sealed class RoomAuthException : Exception { }

internal sealed class RoomLink : IDisposable
{
    private const string CookieName = RemotePairingHttp.CookieName;
    private readonly CancellationTokenSource stop = new();
    private readonly Channel<byte[]> outbox = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.DropOldest });
    private volatile RoomView view = new(false, "Подключение…", null);
    private readonly Task loop;

    internal RoomLink(RoomRecord record)
    {
        Record = record;
        loop = Task.Run(() => RunAsync(stop.Token));
    }

    internal RoomRecord Record { get; set; }
    internal string Id => Record.Id;
    internal RoomView View => view;
    internal event Action? Changed;

    internal bool SendAudio(string type, string target, JsonElement value)
    {
        if (!view.Online) return false;
        outbox.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(new { type, target, value }));
        return true;
    }

    private void Set(RoomView next)
    {
        var previous = view;
        view = next;
        if (previous.Online != next.Online || previous.Status != next.Status ||
            previous.Audio?.GetRawText() != next.Audio?.GetRawText()) Changed?.Invoke();
    }

    private async Task RunAsync(CancellationToken token)
    {
        int attempt = 0;
        while (!token.IsCancellationRequested)
        {
            int delay;
            try
            {
                bool received = await ConnectOnceAsync(token).ConfigureAwait(false);
                if (received) attempt = 0;
                Set(new(false, "Нет связи", null));
                delay = Math.Min(10, 1 << Math.Min(attempt++, 4));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (RoomAuthException)
            {
                Set(new(false, "Доступ отозван. Добавьте аудиторию заново", null));
                delay = 30;
            }
            catch (Exception)
            {
                Set(new(false, "Нет связи", null));
                delay = Math.Min(10, 1 << Math.Min(attempt++, 4));
            }
            try { await Task.Delay(TimeSpan.FromSeconds(delay), token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task<bool> ConnectOnceAsync(CancellationToken token)
    {
        var secret = RoomStore.Unprotect(Record.Secret) ?? throw new RoomAuthException();
        if (!RoomStore.TryParseOrigin(Record.Origin, out var origin)) throw new RoomAuthException();
        while (outbox.Reader.TryRead(out _)) { }
        using var ws = new ClientWebSocket();
        ws.Options.CollectHttpResponseDetails = true;
        ws.Options.Proxy = new WebProxy();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(3);
        ws.Options.KeepAliveTimeout = TimeSpan.FromSeconds(6);
        ws.Options.SetRequestHeader("Origin", $"http://{origin.Authority}");
        ws.Options.Cookies = new CookieContainer();
        ws.Options.Cookies.Add(origin, new Cookie(CookieName, secret, "/"));
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            connect.CancelAfter(TimeSpan.FromSeconds(5));
            try { await ws.ConnectAsync(new Uri($"ws://{origin.Authority}/ws"), connect.Token).ConfigureAwait(false); }
            catch (WebSocketException) when (ws.HttpStatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new RoomAuthException();
            }
        }
        await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { type = "hello", protocol = 1 }),
            WebSocketMessageType.Text, true, token).ConfigureAwait(false);

        using var done = CancellationTokenSource.CreateLinkedTokenSource(token);
        bool received = false;
        var send = SendLoopAsync(ws, done.Token);
        var receive = ReceiveLoopAsync(ws, () => received = true, done.Token);
        try { await Task.WhenAny(send, receive).ConfigureAwait(false); }
        finally
        {
            ws.Abort();
            await done.CancelAsync().ConfigureAwait(false);
            try { await Task.WhenAll(send, receive).ConfigureAwait(false); }
            catch (Exception error) when (error is OperationCanceledException or WebSocketException or ObjectDisposedException or
                InvalidOperationException or IOException) { }
        }
        token.ThrowIfCancellationRequested();
        return received;
    }

    private async Task SendLoopAsync(ClientWebSocket ws, CancellationToken token)
    {
        await foreach (var message in outbox.Reader.ReadAllAsync(token).ConfigureAwait(false))
            await ws.SendAsync(message, WebSocketMessageType.Text, true, token).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, Action onState, CancellationToken token)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        while (ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(buffer, token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return;
            message.Write(buffer, 0, result.Count);
            if (message.Length > 65536) return;
            if (!result.EndOfMessage) continue;
            try
            {
                using var doc = JsonDocument.Parse(message.ToArray());
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("type", out var type) &&
                    type.ValueKind == JsonValueKind.String && type.GetString() == "state")
                {
                    JsonElement? audio = root.TryGetProperty("audio", out var value) && value.ValueKind == JsonValueKind.Object
                        ? value.Clone() : null;
                    onState();
                    Set(new(true, audio == null ? "Звук недоступен" : "В сети", audio));
                }
            }
            catch (JsonException) { }
            message.SetLength(0);
        }
    }

    // Pairing: the room's panel shows a link http://IP:port/pair#code=...; the hub redeems it once.
    internal static bool TryParseLink(string? link, out string origin, out string code, out string error)
    {
        origin = code = "";
        error = "Вставьте ссылку привязки с экрана панели: http://IP:порт/pair#code=…";
        if (!Uri.TryCreate(link?.Trim(), UriKind.Absolute, out var uri) || !RoomStore.TryParseOrigin($"http://{uri.Authority}", out _) ||
            uri.Scheme != "http") return false;
        var value = uri.Fragment.TrimStart('#').Split('&').Select(part => part.Split('=', 2))
            .FirstOrDefault(part => part.Length == 2 && part[0] == "code")?[1];
        if (string.IsNullOrEmpty(value) || value.Length > 64) return false;
        origin = $"http://{uri.Authority}";
        code = value;
        error = "";
        return true;
    }

    internal static async Task<(RoomRecord? Room, string? Error)> PairAsync(string name, string origin, string code,
        string hubName, CancellationToken token)
    {
        using var handler = new HttpClientHandler { UseProxy = false, UseCookies = false, AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, origin + "/api/pair")
            {
                Content = new StringContent(JsonSerializer.Serialize(new { code, name = hubName }), Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("Origin", origin);
            using var reply = await client.SendAsync(request, token).ConfigureAwait(false);
            if ((int)reply.StatusCode == 429) return (null, "Слишком много попыток. Подождите минуту");
            if (!reply.IsSuccessStatusCode)
            {
                var body = await reply.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                return (null, body.Contains("device_limit") ? "На панели достигнут лимит устройств. Отзовите одно на панели" :
                    "Код недействителен или устарел. Покажите новый QR на панели и вставьте свежую ссылку");
            }
            var cookie = reply.Headers.TryGetValues("Set-Cookie", out var values)
                ? values.FirstOrDefault(item => item.StartsWith(CookieName + "=", StringComparison.Ordinal)) : null;
            var token2 = cookie?[(CookieName.Length + 1)..].Split(';')[0];
            if (string.IsNullOrEmpty(token2)) return (null, "Панель не вернула ключ доступа");
            return (new RoomRecord(Guid.NewGuid().ToString("N"), name, origin, RoomStore.Protect(token2)), null);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            return (null, "Панель не отвечает. Проверьте адрес, что приложение запущено и включено удалённое управление");
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        outbox.Writer.TryComplete();
        try { loop.Wait(1500); } catch (AggregateException) { }
        stop.Dispose();
    }
}
