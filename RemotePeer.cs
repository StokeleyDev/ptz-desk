using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;

namespace AGLPTZ;

internal sealed class RemotePeer(string id, string name, WebSocket socket)
{
    internal string Id => id;
    internal string Name => name;
    internal bool Alive => Volatile.Read(ref closed) == 0 && socket.State == WebSocketState.Open;
    internal bool Ready { get; set; }
    internal bool MustRelease { get; set; }
    internal string? LastProjection { get; set; }
    internal long Sequence { get; set; } = -1;
    internal readonly object Inbox = new();
    internal JsonElement? LatestInput;
    internal bool InputPosted;
    private int closed;
    private int statePending;
    private byte[]? state;
    private readonly Channel<byte[]> replies = Channel.CreateBounded<byte[]>(64);
    private readonly SemaphoreSlim wake = new(0, 1);

    internal void Reply(object value)
    {
        if (!replies.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(value))) Abort();
        Signal();
    }

    internal void State(byte[] value)
    {
        Volatile.Write(ref state, value);
        Volatile.Write(ref statePending, 1);
        Signal();
    }

    private void Signal()
    {
        try { wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    internal void Abort()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        socket.Abort();
        Signal();
    }

    internal async Task RunAsync(Action<RemotePeer, JsonElement> receive, CancellationToken cancellation)
    {
        using var done = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var send = SendAsync(done.Token);
        var read = ReadAsync(receive, done.Token);
        try { await Task.WhenAny(send, read).ConfigureAwait(false); }
        finally
        {
            Abort();
            await done.CancelAsync().ConfigureAwait(false);
            try { await Task.WhenAll(send, read).ConfigureAwait(false); }
            catch (Exception error) when (error is OperationCanceledException or WebSocketException or IOException or
                ObjectDisposedException or InvalidOperationException) { }
        }
    }

    private async Task SendAsync(CancellationToken token)
    {
        while (Alive)
        {
            await wake.WaitAsync(token).ConfigureAwait(false);
            while (replies.Reader.TryRead(out var reply))
                await socket.SendAsync(reply.AsMemory(), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
            if (Interlocked.Exchange(ref statePending, 0) != 0 && Volatile.Read(ref state) is { } snapshot)
                await socket.SendAsync(snapshot.AsMemory(), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
        }
    }

    private async Task ReadAsync(Action<RemotePeer, JsonElement> receive, CancellationToken token)
    {
        var buffer = new byte[1025];
        long window = Environment.TickCount64;
        int messages = 0, count = 0;
        while (Alive)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(count), token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return;
            count += result.Count;
            if (count > 1024) return;
            if (!result.EndOfMessage) continue;
            var length = count;
            count = 0;
            long now = Environment.TickCount64;
            if (now - window >= 1000) { window = now; messages = 0; }
            if (++messages > 40 || result.MessageType != WebSocketMessageType.Text) continue;
            try
            {
                using var doc = JsonDocument.Parse(buffer.AsMemory(0, length));
                if (doc.RootElement.ValueKind == JsonValueKind.Object) receive(this, doc.RootElement.Clone());
            }
            catch (JsonException) { }
        }
    }
}
