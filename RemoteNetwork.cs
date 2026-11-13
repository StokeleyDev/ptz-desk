using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace AGLPTZ;

internal enum RemoteNetworkCategory { Public, Private, Domain }

internal sealed record RemoteAdapter(string Id, string Name, IPAddress Address, int Prefix,
    RemoteNetworkCategory? Category, string? Reason)
{
    internal bool Eligible => Reason == null;
}

internal static class RemoteNetwork
{
    internal static RemoteAdapter[] ListAdapters()
    {
        var categories = ReadCategories();
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(adapter =>
            {
                RemoteNetworkCategory? category = Guid.TryParse(adapter.Id, out var id) &&
                    categories.TryGetValue(id, out var value) ? value : null;
                return adapter.GetIPProperties().UnicastAddresses
                    .Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(item => new RemoteAdapter(adapter.Id, adapter.Name, item.Address, item.PrefixLength,
                        category, Reason(adapter.OperationalStatus, item.Address, item.PrefixLength, category)));
            }).ToArray();
    }

    private static string? Reason(OperationalStatus status, IPAddress address, int prefix, RemoteNetworkCategory? category)
    {
        if (category == null) return "Не удалось определить тип сети";
        if (status != OperationalStatus.Up) return "Адаптер отключён";
        if (category is not (RemoteNetworkCategory.Private or RemoteNetworkCategory.Domain)) return "Сеть не частная";
        if (!IsRfc1918(address)) return "Адрес не из частного диапазона";
        return prefix is >= 1 and <= 32 ? null : "Некорректная маска сети";
    }

    internal static bool IsRfc1918(IPAddress address)
    {
        var bytes = Normalize(address).GetAddressBytes();
        return bytes.Length == 4 && (bytes[0] == 10 ||
            bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
            bytes[0] == 192 && bytes[1] == 168);
    }

    internal static bool InSameSubnet(IPAddress client, IPAddress adapterIp, int prefix)
    {
        var a = Normalize(client).GetAddressBytes();
        var b = Normalize(adapterIp).GetAddressBytes();
        if (a.Length != 4 || b.Length != 4 || prefix is < 1 or > 32) return false;
        for (int i = 0; prefix > 0; i++, prefix -= 8)
        {
            int mask = 0xff << Math.Max(0, 8 - prefix);
            if (((a[i] ^ b[i]) & mask) != 0) return false;
        }
        return true;
    }

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static Dictionary<Guid, RemoteNetworkCategory> ReadCategories()
    {
        var categories = new Dictionary<Guid, RemoteNetworkCategory>();
        var conflicts = new HashSet<Guid>();
        try
        {
            var clsid = new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B");
            var manager = (INetworkListManager)Activator.CreateInstance(Type.GetTypeFromCLSID(clsid, throwOnError: true)!)!;
            try
            {
                var connections = manager.GetNetworkConnections();
                try
                {
                    while (true)
                    {
                        uint fetched = 0;
                        int result = connections.Next(1, out var connection, ref fetched);
                        Marshal.ThrowExceptionForHR(result);
                        if (fetched == 0) break;
                        try
                        {
                            var id = connection.GetAdapterId();
                            var network = connection.GetNetwork();
                            try
                            {
                                var value = network.GetCategory();
                                if (!Enum.IsDefined(value)) continue;
                                if (!categories.TryAdd(id, value) && categories[id] != value) conflicts.Add(id);
                            }
                            finally { Marshal.ReleaseComObject(network); }
                        }
                        finally { Marshal.ReleaseComObject(connection); }
                    }
                }
                finally { Marshal.ReleaseComObject(connections); }
            }
            finally { Marshal.ReleaseComObject(manager); }
            foreach (var id in conflicts) categories.Remove(id);
        }
        catch (Exception error) when (error is COMException or InvalidCastException)
        {
            categories.Clear();
        }
        return categories;
    }

    // Order and native types match the dual interfaces in netlistmgr.h.
    [ComImport, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkListManager
    {
        [return: MarshalAs(UnmanagedType.Interface)] object GetNetworks(int flags);
        INetwork GetNetwork(Guid id);
        IEnumNetworkConnections GetNetworkConnections();
    }

    [ComImport, Guid("DCB00006-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IEnumNetworkConnections
    {
        [return: MarshalAs(UnmanagedType.IUnknown)] object GetNewEnum();
        [PreserveSig] int Next(uint count, out INetworkConnection connection, ref uint fetched);
    }

    [ComImport, Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkConnection
    {
        INetwork GetNetwork();
        [return: MarshalAs(UnmanagedType.VariantBool)] bool IsConnectedToInternet();
        [return: MarshalAs(UnmanagedType.VariantBool)] bool IsConnected();
        int GetConnectivity();
        Guid GetConnectionId();
        Guid GetAdapterId();
    }

    [ComImport, Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetwork
    {
        [return: MarshalAs(UnmanagedType.BStr)] string GetName();
        void SetName([MarshalAs(UnmanagedType.BStr)] string name);
        [return: MarshalAs(UnmanagedType.BStr)] string GetDescription();
        void SetDescription([MarshalAs(UnmanagedType.BStr)] string description);
        Guid GetNetworkId();
        int GetDomainType();
        IEnumNetworkConnections GetNetworkConnections();
        void GetTimeCreatedAndConnected(out uint createdLow, out uint createdHigh, out uint connectedLow, out uint connectedHigh);
        [return: MarshalAs(UnmanagedType.VariantBool)] bool IsConnectedToInternet();
        [return: MarshalAs(UnmanagedType.VariantBool)] bool IsConnected();
        int GetConnectivity();
        RemoteNetworkCategory GetCategory();
    }
}
