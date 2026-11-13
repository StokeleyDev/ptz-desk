using System.Net;

namespace AGLPTZ;

// Called under the pairing lock. The clock is monotonic and injectable.
internal sealed class RemoteAttemptLimits(TimeProvider clock)
{
    private sealed class Entry
    {
        internal int Failures, Level, Duration;
        internal long BlockStarted, LastUsed;
    }

    private readonly Dictionary<IPAddress, Entry> addresses = [];
    private readonly Queue<long> failures = new();
    private long globalStarted;
    private bool globalBlocked;

    internal int RetryAfter(IPAddress address, bool pairing)
    {
        var ip = Normalize(address);
        int local = addresses.TryGetValue(ip, out var entry) ? Remaining(entry.BlockStarted, entry.Duration) : 0;
        if (entry != null) entry.LastUsed = clock.GetTimestamp();
        return Math.Max(local, pairing && globalBlocked ? Remaining(globalStarted, 300) : 0);
    }

    internal int Fail(IPAddress address, bool pairing)
    {
        var now = clock.GetTimestamp();
        var ip = Normalize(address);
        if (!addresses.TryGetValue(ip, out var entry))
        {
            if (addresses.Count >= 1024) addresses.Remove(addresses.MinBy(x => x.Value.LastUsed).Key);
            addresses[ip] = entry = new();
        }
        entry.LastUsed = now;
        if (++entry.Failures >= 5)
        {
            entry.Failures = 0;
            entry.BlockStarted = now;
            entry.Duration = entry.Level switch { 0 => 60, 1 => 300, _ => 900 };
            entry.Level = Math.Min(2, entry.Level + 1);
        }

        while (failures.TryPeek(out var first) && clock.GetElapsedTime(first, now) >= TimeSpan.FromMinutes(10))
            failures.Dequeue();
        if (failures.Count == 31) failures.Dequeue();
        failures.Enqueue(now);
        if (failures.Count > 30 && (!globalBlocked || Remaining(globalStarted, 300) == 0))
        {
            globalBlocked = true;
            globalStarted = now;
        }
        return RetryAfter(ip, pairing);
    }

    internal void Success(IPAddress address)
    {
        if (addresses.TryGetValue(Normalize(address), out var entry)) entry.Failures = 0;
    }

    private int Remaining(long started, int duration) =>
        Math.Max(0, (int)Math.Ceiling(duration - clock.GetElapsedTime(started).TotalSeconds));
    private static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
}
