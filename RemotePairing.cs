using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QRCoder;

namespace AGLPTZ;

internal sealed record PairingOffer(string Url, DateTimeOffset ExpiresAt);
internal sealed record PairedDevice(string Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset LastSeen);
internal sealed record PairingResult(string? Name = null, string? Token = null, int RetryAfter = 0, bool Full = false, string? DeviceId = null);

internal sealed class RemotePairing
{
    private sealed record StoredDevice(string Id, string Name, byte[] TokenHash, DateTimeOffset CreatedAt, DateTimeOffset LastSeen);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object gate = new();
    private readonly string path;
    private readonly TimeProvider clock;
    private readonly RemoteAttemptLimits limits;
    private readonly List<StoredDevice> devices;
    private string? authority;
    private byte[]? offerHash;
    private long offeredAt, lastFlush;
    private bool dirty;

    internal RemotePairing(string path, TimeProvider? clock = null)
    {
        this.path = path;
        this.clock = clock ?? TimeProvider.System;
        limits = new(this.clock);
        devices = Load();
        lastFlush = this.clock.GetTimestamp();
    }

    internal event Action? DevicesChanged;
    internal event Action<string>? DeviceRevoked;
    internal bool HasOffer
    {
        get { lock (gate) return offerHash != null && clock.GetElapsedTime(offeredAt) < TimeSpan.FromMinutes(2); }
    }

    internal void Open(string origin) { lock (gate) authority = origin; }

    internal PairingOffer CreateOffer()
    {
        lock (gate)
        {
            if (authority == null) throw new InvalidOperationException("Сервер выключен");
            var code = RandomValue(16);
            offerHash = Hash(code);
            offeredAt = clock.GetTimestamp();
            return new($"{authority}/pair#code={code}", clock.GetUtcNow().AddMinutes(2));
        }
    }

    internal void CancelOffer() { lock (gate) offerHash = null; }

    internal void Close()
    {
        lock (gate)
        {
            authority = null;
            offerHash = null;
            if (dirty) Save(devices);
        }
    }

    internal PairedDevice[] ListDevices()
    {
        lock (gate) return devices.Select(x => new PairedDevice(x.Id, x.Name, x.CreatedAt, x.LastSeen)).ToArray();
    }

    internal bool Revoke(string id)
    {
        lock (gate)
        {
            if (!devices.Any(x => x.Id == id)) return false;
            var remaining = devices.Where(x => x.Id != id).ToList();
            Save(remaining);
            devices.Clear();
            devices.AddRange(remaining);
        }
        DeviceRevoked?.Invoke(id);
        DevicesChanged?.Invoke();
        return true;
    }

    internal PairingResult Pair(IPAddress ip, string code, string? name)
    {
        PairingResult result;
        lock (gate)
        {
            int wait = limits.RetryAfter(ip, pairing: true);
            if (wait > 0) return new(RetryAfter: wait);
            if (authority == null || offerHash == null || clock.GetElapsedTime(offeredAt) >= TimeSpan.FromMinutes(2) ||
                !CryptographicOperations.FixedTimeEquals(offerHash, Hash(code)))
                return new(RetryAfter: limits.Fail(ip, pairing: true));
            if (devices.Count >= 10) return new(Full: true);
            var token = RandomValue(32);
            var now = clock.GetUtcNow();
            var device = new StoredDevice(Guid.NewGuid().ToString("N"), NormalizeName(name), Hash(token), now, now);
            Save([..devices, device]);
            devices.Add(device);
            offerHash = null;
            limits.Success(ip);
            result = new(device.Name, token);
        }
        DevicesChanged?.Invoke();
        return result;
    }

    internal PairingResult Session(IPAddress ip, string token)
    {
        lock (gate)
        {
            var hash = Hash(token);
            int match = -1;
            for (int i = 0; i < devices.Count; i++)
                if (CryptographicOperations.FixedTimeEquals(hash, devices[i].TokenHash)) match = i;
            if (match < 0 || authority == null)
            {
                int wait = limits.RetryAfter(ip, pairing: false);
                return new(RetryAfter: wait > 0 ? wait : limits.Fail(ip, pairing: false));
            }
            limits.Success(ip);
            var device = devices[match] with { LastSeen = clock.GetUtcNow() };
            devices[match] = device;
            dirty = true;
            if (clock.GetElapsedTime(lastFlush) >= TimeSpan.FromMinutes(1)) Save(devices);
            return new(device.Name, DeviceId: device.Id);
        }
    }

    internal static byte[] CreateQrPng(string url)
    {
        using var data = QRCodeGenerator.GenerateQrCode(url, QRCodeGenerator.ECCLevel.M);
        using var renderer = new PngByteQRCode(data);
        return renderer.GetGraphic(6);
    }

    private List<StoredDevice> Load()
    {
        try
        {
            var records = JsonSerializer.Deserialize<List<StoredDevice>>(File.ReadAllText(path), Json);
            if (records == null || records.Count > 10 || records.Any(x => x == null ||
                !Guid.TryParseExact(x.Id, "N", out _) || x.Name is null || x.TokenHash?.Length != 32) ||
                records.Select(x => x.Id).Distinct().Count() != records.Count) throw new JsonException();
            return records;
        }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
        catch (JsonException)
        {
            var bad = path + ".bad";
            File.Move(path, File.Exists(bad) ? bad + "." + Guid.NewGuid().ToString("N") : bad);
            return [];
        }
    }

    private void Save(List<StoredDevice> records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(records, Json));
            File.Move(temp, path, true);
            dirty = false;
            lastFlush = clock.GetTimestamp();
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static string NormalizeName(string? name)
    {
        var value = string.Concat((name ?? "").EnumerateRunes()
            .Where(rune => !Rune.IsControl(rune)).Take(40).Select(rune => rune.ToString())).Trim();
        return value.Length == 0 ? "Телефон" : value;
    }

    private static string RandomValue(int size) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(size))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
