using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AGLPTZ;

internal sealed class RemoteServer
{
    private const int MaxBodyBytes = 4096;
    private readonly SemaphoreSlim transition = new(1, 1);
    private WebApplication? application;
    internal RemotePairing? Pairing { get; private set; }
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, RemotePeer> peers = new();
    private readonly object peerGate = new();
    private bool accepting;
    internal int SessionCount => peers.Count;
    internal void DiscardPendingInputs()
    {
        foreach (var peer in peers.Values)
            lock (peer.Inbox) peer.LatestInput = null;
    }
    internal event Action<RemotePeer, System.Text.Json.JsonElement>? CommandReceived;
    internal event Action<RemotePeer>? PeerClosed;
    internal event Action? SessionsChanged;
    internal bool IsCurrent(RemotePeer peer) => peer.Alive && peers.TryGetValue(peer.Id, out var current) &&
        ReferenceEquals(current, peer);
    internal void Broadcast(Action<RemotePeer> publish)
    {
        foreach (var peer in peers.Values)
            if (peer.Ready) publish(peer);
    }

    private void DropPeer(RemotePeer peer)
    {
        peer.Abort();
        if (!peers.TryRemove(new KeyValuePair<string, RemotePeer>(peer.Id, peer))) return;
        PeerClosed?.Invoke(peer);
        SessionsChanged?.Invoke();
    }

    internal async Task<string?> StartAsync(RemoteAdapter adapter, int port, bool allowRouted = false)
    {
        await transition.WaitAsync().ConfigureAwait(false);
        try
        {
            if (application != null) return "Сервер уже запущен";
            if (!adapter.Eligible || adapter.Category is not (RemoteNetworkCategory.Private or RemoteNetworkCategory.Domain) ||
                adapter.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
                !RemoteNetwork.IsRfc1918(adapter.Address) || adapter.Prefix is < 1 or > 32)
                return adapter.Reason ?? "Адаптер не подходит";
            if (!RemoteSettings.ValidPort(port)) return "Порт должен быть от 1025 до 65535";
            return await Task.Run(async () =>
            {
                var authority = $"{adapter.Address}:{port.ToString(CultureInfo.InvariantCulture)}";
                WebApplication? app = null;
                try
                {
                    var builder = WebApplication.CreateSlimBuilder();
                    builder.Logging.ClearProviders();
                    builder.WebHost.ConfigureKestrel(options =>
                    {
                        options.Configure(new ConfigurationBuilder().Build());
                        options.AddServerHeader = false;
                        options.Limits.MaxConcurrentConnections = 16;
                        options.Limits.MaxRequestBodySize = MaxBodyBytes;
                        options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
                        options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(15);
                        options.Listen(adapter.Address, port);
                    });
                    app = builder.Build();
                    var pairing = new RemotePairing(Path.Combine(AppPaths.DataRoot, "remote-devices.json"));
                    pairing.DeviceRevoked += id =>
                    {
                        lock (peerGate)
                            if (peers.TryGetValue(id, out var peer)) DropPeer(peer);
                    };
                    app.UseWebSockets(new WebSocketOptions
                    {
                        KeepAliveInterval = TimeSpan.FromSeconds(3),
                        KeepAliveTimeout = TimeSpan.FromSeconds(6),
                    });
                    app.Run(context => RespondAsync(context, adapter, authority, pairing, allowRouted));
                    await app.StartAsync().ConfigureAwait(false);
                    pairing.Open($"http://{authority}");
                    Pairing = pairing;
                    lock (peerGate) accepting = true;
                    application = app;
                    return null;
                }
                catch (Exception error)
                {
                    if (app != null) await DisposeApplicationAsync(app).ConfigureAwait(false);
                    return StartError(error);
                }
            }).ConfigureAwait(false);
        }
        finally { transition.Release(); }
    }

    internal async Task StopAsync()
    {
        await transition.WaitAsync().ConfigureAwait(false);
        try
        {
            if (application is not { } app) return;
            application = null;
            lock (peerGate)
            {
                accepting = false;
                foreach (var peer in peers.Values) DropPeer(peer);
            }
            var pairing = Pairing;
            Pairing = null;
            await Task.Run(async () =>
            {
                try { pairing?.Close(); }
                finally { await DisposeApplicationAsync(app).ConfigureAwait(false); }
            }).ConfigureAwait(false);
        }
        finally { transition.Release(); }
    }

    private static async Task DisposeApplicationAsync(WebApplication app)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try { await app.StopAsync(deadline.Token).ConfigureAwait(false); }
        catch (Exception error) when (error is OperationCanceledException or InvalidOperationException) { }
        finally { await app.DisposeAsync().ConfigureAwait(false); }
    }

    private static string StartError(Exception error)
    {
        for (Exception? cause = error; cause != null; cause = cause.InnerException)
        {
            if (cause is not System.Net.Sockets.SocketException socket) continue;
            return socket.SocketErrorCode switch
            {
                System.Net.Sockets.SocketError.AddressAlreadyInUse => "Порт уже занят",
                System.Net.Sockets.SocketError.AccessDenied => "Windows запретила открыть порт",
                System.Net.Sockets.SocketError.AddressNotAvailable => "IP-адрес адаптера недоступен",
                _ => "Не удалось открыть сетевой порт",
            };
        }
        return "Не удалось запустить LAN-сервер";
    }

    private async Task RespondAsync(HttpContext context, RemoteAdapter adapter, string authority, RemotePairing pairing, bool allowRouted)
    {
        if (context.Connection.RemoteIpAddress is not { } address ||
            !RemoteNetwork.InSameSubnet(address, adapter.Address, adapter.Prefix) &&
            !(allowRouted && RemoteNetwork.IsRfc1918(address)))
        {
            context.Abort();
            return;
        }

        var response = context.Response;
        response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; " +
            "style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:; " +
            $"connect-src 'self' ws://{authority}; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";

        if (!string.Equals(context.Request.Host.Value, authority, StringComparison.Ordinal))
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        bool head = HttpMethods.IsHead(context.Request.Method);
        bool post = HttpMethods.IsPost(context.Request.Method);
        if (context.Request.ContentLength > MaxBodyBytes)
        {
            response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }
        if (!head && !post && !HttpMethods.IsGet(context.Request.Method))
        {
            response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            response.Headers.Allow = "GET, HEAD, POST";
            return;
        }
        if (post && context.Request.Path == "/api/pair")
        {
            await RemotePairingHttp.PairAsync(context, pairing, address, $"http://{authority}");
            return;
        }
        if (HttpMethods.IsGet(context.Request.Method) && context.Request.Path == "/api/session")
        {
            await RemotePairingHttp.SessionAsync(context, pairing, address);
            return;
        }
        if (context.Request.Path == "/ws")
        {
            await ConnectPeerAsync(context, pairing, address, $"http://{authority}");
            return;
        }
        if (context.Request.Path == "/api/pair" || context.Request.Path == "/api/session" ||
            post && context.Request.Path.Value is "/" or "/pair" or "/phone.js" or "/app-icon.png")
        {
            response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            response.Headers.Allow = context.Request.Path == "/api/pair" ? "POST" :
                context.Request.Path == "/api/session" ? "GET" : "GET, HEAD";
            return;
        }
        var name = context.Request.Path.Value switch
        {
            "/" or "/pair" => "phone.html",
            "/app-icon.png" => "app-icon.png",
            "/phone.js" => "phone.js",
            "/phone.css" => "phone.css",
            _ => null,
        };
        if (name == null)
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        using var stream = UiAssets.Open(name, out var contentType);
        if (stream == null)
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        response.ContentType = contentType;
        response.ContentLength = stream.Length;
        if (name == "phone.html") response.Headers.CacheControl = "no-store";
        if (!head) await stream.CopyToAsync(response.Body, context.RequestAborted).ConfigureAwait(false);
    }

    private async Task ConnectPeerAsync(HttpContext context, RemotePairing pairing, System.Net.IPAddress address, string origin)
    {
        if (!HttpMethods.IsGet(context.Request.Method) || !context.WebSockets.IsWebSocketRequest ||
            context.Request.Headers.Origin.ToString() != origin)
        {
            context.Response.StatusCode = 403;
            return;
        }
        const string cookie = RemotePairingHttp.CookieName;
        if (!context.Request.Cookies.TryGetValue(cookie, out var token))
        {
            context.Response.StatusCode = 401;
            return;
        }
        var session = pairing.Session(address, token);
        if (session.DeviceId == null)
        {
            context.Response.StatusCode = session.RetryAfter > 0 ? 429 : 401;
            if (session.RetryAfter > 0)
                context.Response.Headers.RetryAfter = session.RetryAfter.ToString(CultureInfo.InvariantCulture);
            return;
        }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var peer = new RemotePeer(session.DeviceId, session.Name!, socket);
        lock (peerGate)
        {
            // Recheck after the upgrade so a concurrent revoke cannot leave a live session.
            if (!accepting || !pairing.ListDevices().Any(device => device.Id == peer.Id))
            {
                peer.Abort();
                return;
            }
            if (peers.TryGetValue(peer.Id, out var previous)) DropPeer(previous);
            peers[peer.Id] = peer;
        }
        SessionsChanged?.Invoke();
        try { await peer.RunAsync((client, command) => CommandReceived?.Invoke(client, command), context.RequestAborted); }
        finally { DropPeer(peer); }
    }
}
