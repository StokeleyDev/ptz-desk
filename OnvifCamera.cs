using System.Net;
using System.Xml;
using SharpOnvifClient;
using SharpOnvifClient.DeviceMgmt;
using SharpOnvifClient.Media;
using SharpOnvifClient.PTZ;
using SharpOnvifCommon.Onvif;
using SharpOnvifCommon.Soap;

namespace AGLPTZ;

internal sealed record NetworkCamera(string Name, string Address, string Endpoint);

internal sealed class OnvifCamera : IDisposable
{
    private const string MediaNamespace = "http://www.onvif.org/ver10/media/wsdl";
    private const string PtzNamespace = "http://www.onvif.org/ver20/ptz/wsdl";
    private readonly PTZClient client;
    private readonly string token;
    private readonly Space2DDescription? panTilt;
    private readonly Space1DDescription? zoom;
    private readonly Space1DDescription? absoluteZoom;
    private readonly string moveTimeout;

    private OnvifCamera(PTZClient client, string token, Space2DDescription? panTilt,
        Space1DDescription? zoom, TimeSpan timeout, Space1DDescription? absoluteZoom, int? zoomPercent)
    {
        this.client = client;
        this.token = token;
        this.panTilt = panTilt;
        this.zoom = zoom;
        this.absoluteZoom = absoluteZoom;
        ZoomPercent = zoomPercent;
        moveTimeout = XmlConvert.ToString(timeout);
    }

    public bool CanMove => panTilt != null;
    public bool CanZoom => zoom != null;
    public bool CanSeekZoom => absoluteZoom != null;
    public int? ZoomPercent { get; private set; }

    public static async Task<NetworkCamera[]> DiscoverAsync()
    {
        var results = await new OnvifDiscoveryClient().DiscoverAsync();
        return CameraList(results);
    }

    internal static NetworkCamera[] CameraList(IEnumerable<OnvifDiscoveryResult> results)
    {
        return results.SelectMany(result => (result.Addresses ?? []).Select(address =>
        {
            if (!TryEndpoint(address, out var endpoint)) return null;
            string name = string.IsNullOrWhiteSpace(result.Name) ? endpoint.Host : result.Name;
            return new NetworkCamera(name, endpoint.Authority, endpoint.AbsoluteUri);
        })).OfType<NetworkCamera>().DistinctBy(camera => camera.Address)
            .OrderBy(camera => camera.Name).ThenBy(camera => camera.Address).ToArray();
    }

    internal static Uri ManualEndpoint(string input)
    {
        string value = input.Trim();
        if (IPAddress.TryParse(value, out var ip))
            value = new UriBuilder(Uri.UriSchemeHttp, ip.ToString()).Uri.AbsoluteUri;
        if (!value.Contains("://", StringComparison.Ordinal)) value = "http://" + value;
        if (!TryEndpoint(value, out var endpoint))
            throw new ArgumentException("Введите IP камеры или адрес службы ONVIF.");
        return endpoint.AbsolutePath == "/" ?
            new UriBuilder(endpoint) { Path = "/onvif/device_service" }.Uri : endpoint;
    }

    private static bool TryEndpoint(string address, out Uri endpoint)
    {
        if (Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo) &&
            string.IsNullOrEmpty(uri.Fragment) && IPAddress.TryParse(uri.DnsSafeHost.Trim('[', ']'), out var ip) &&
            !ip.IsIPv6Multicast && !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.IPv6Any) &&
            !ip.Equals(IPAddress.Broadcast) && !(ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                ip.GetAddressBytes()[0] is >= 224 and <= 239))
        {
            endpoint = uri;
            return true;
        }
        endpoint = null!;
        return false;
    }

    private static string ServiceEndpoint(Uri device, string address)
    {
        if (!TryEndpoint(address, out var service) ||
            !string.Equals(service.DnsSafeHost, device.DnsSafeHost, StringComparison.OrdinalIgnoreCase) ||
            (device.Scheme == "https" && service.Scheme != "https"))
            throw new InvalidOperationException("Камера вернула адрес ONVIF-службы на другом узле или с другим протоколом.");
        return service.AbsoluteUri;
    }

    public static async Task<OnvifCamera> ConnectAsync(Uri endpoint, string username, string password,
        CancellationToken cancellationToken, HttpClient? transport = null)
    {
        var settings = new OnvifClientSettings
        {
            Credentials = string.IsNullOrEmpty(username) ? null : new NetworkCredential(username, password),
            Timeout = TimeSpan.FromSeconds(3),
            MaxResponseContentBytes = 2 * 1024 * 1024,
            HttpClient = transport,
        };
        using var device = new DeviceClient(endpoint.AbsoluteUri, settings);
        var services = (await device.GetServicesAsync(false, cancellationToken)).Service ?? [];
        string AddressOf(string name) => ServiceEndpoint(endpoint, services.FirstOrDefault(item =>
            item.Namespace == name)?.XAddr ?? throw new InvalidOperationException(
            "У камеры нет необходимых служб ONVIF Media и PTZ."));

        using var media = new MediaClient(AddressOf(MediaNamespace), settings);
        var profiles = (await media.GetProfilesAsync(cancellationToken)).Profiles ?? [];
        var ptz = new PTZClient(AddressOf(PtzNamespace), settings);
        try
        {
            foreach (var profile in profiles.Where(profile =>
                !string.IsNullOrEmpty(profile.token) && !string.IsNullOrEmpty(profile.PTZConfiguration?.token)))
            {
                var options = (await ptz.GetConfigurationOptionsAsync(profile.PTZConfiguration.token,
                    cancellationToken)).PTZConfigurationOptions;
                var spaces = options?.Spaces;
                var panTilt = spaces?.ContinuousPanTiltVelocitySpace?.FirstOrDefault(space =>
                    Bidirectional(space.XRange) && Bidirectional(space.YRange));
                var zoom = spaces?.ContinuousZoomVelocitySpace?.FirstOrDefault(space => Bidirectional(space.XRange));
                var absolute = spaces?.AbsoluteZoomPositionSpace?.Where(space => ValidRange(space.XRange))
                    .OrderByDescending(space => space.URI == profile.PTZConfiguration.DefaultAbsoluteZoomPositionSpace)
                    .FirstOrDefault();
                int? position = absolute == null ? null :
                    await ReadZoomAsync(ptz, profile.token, absolute, cancellationToken);
                if (position == null) absolute = null;
                var timeout = TimeSpan.FromSeconds(1);
                if (options?.PTZTimeout is { Min: { } min, Max: { } max })
                {
                    var minimum = XmlConvert.ToTimeSpan(min);
                    var maximum = XmlConvert.ToTimeSpan(max);
                    if (minimum > timeout || maximum < TimeSpan.FromMilliseconds(350) || maximum < minimum)
                    {
                        panTilt = null;
                        zoom = null;
                    }
                    else if (maximum < timeout) timeout = maximum;
                }
                if (panTilt == null && zoom == null && absolute == null) continue;
                return new OnvifCamera(ptz, profile.token, panTilt, zoom, timeout, absolute, position);
            }
            throw new InvalidOperationException("Камера не сообщила совместимые диапазоны движения ONVIF.");
        }
        catch
        {
            ptz.Dispose();
            throw;
        }
    }

    private static bool Bidirectional(FloatRange? range) =>
        range != null && float.IsFinite(range.Min) && float.IsFinite(range.Max) && range.Min < 0 && range.Max > 0;

    private static bool ValidRange(FloatRange? range) =>
        range != null && float.IsFinite(range.Min) && float.IsFinite(range.Max) && range.Max > range.Min;

    private static async Task<int?> ReadZoomAsync(PTZClient client, string token, Space1DDescription space,
        CancellationToken cancellationToken)
    {
        try
        {
            var zoom = (await client.GetStatusAsync(token, cancellationToken)).PTZStatus?.Position?.Zoom;
            if (zoom == null || !float.IsFinite(zoom.x) ||
                (!string.IsNullOrEmpty(zoom.space) && zoom.space != space.URI) ||
                zoom.x < space.XRange.Min || zoom.x > space.XRange.Max) return null;
            return (int)Math.Round(100d * (zoom.x - space.XRange.Min) / (space.XRange.Max - space.XRange.Min));
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
            ex is SharpOnvifCommon.Soap.SoapTransportException or SharpOnvifCommon.Xml.SoapFaultException)
        {
            return null;
        }
    }

    public Task SeekZoomAsync(int percent, CancellationToken cancellationToken)
    {
        var space = absoluteZoom ?? throw new InvalidOperationException("Абсолютный зум недоступен.");
        float position = (float)(space.XRange.Min +
            ((double)space.XRange.Max - space.XRange.Min) * Math.Clamp(percent, 0, 100) / 100);
        return client.AbsoluteMoveAsync(token, new PTZVector
            { Zoom = new Vector1D { x = position, space = space.URI } }, null, cancellationToken);
    }

    public async Task UpdateZoomAsync(CancellationToken cancellationToken)
    {
        if (absoluteZoom != null && await ReadZoomAsync(client, token, absoluteZoom, cancellationToken) is int percent)
            ZoomPercent = percent;
    }

    private static float Velocity(float value, FloatRange range) =>
        Math.Clamp(value, -1, 1) * (value < 0 ? -range.Min : range.Max);

    public Task MoveAsync(float x, float y, float z, CancellationToken cancellationToken) =>
        client.ContinuousMoveAsync(token, new PTZSpeed
        {
            PanTilt = panTilt == null ? null : new Vector2D
                { x = Velocity(x, panTilt.XRange), y = Velocity(y, panTilt.YRange), space = panTilt.URI },
            Zoom = zoom == null || (z == 0 && CanSeekZoom) ? null : new Vector1D
                { x = Velocity(z, zoom.XRange), space = zoom.URI },
        }, moveTimeout, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) =>
        client.StopAsync(token, CanMove, CanZoom, cancellationToken);

    public Task StopZoomAsync(CancellationToken cancellationToken) =>
        client.StopAsync(token, false, true, cancellationToken);

    public async Task<string?> SetPresetAsync(string name, string? presetToken, CancellationToken cancellationToken) =>
        (await client.SetPresetAsync(token, name, presetToken, cancellationToken)).PresetToken;

    public Task GotoPresetAsync(string presetToken, CancellationToken cancellationToken) =>
        client.GotoPresetAsync(token, presetToken, null, cancellationToken);

    public Task RemovePresetAsync(string presetToken, CancellationToken cancellationToken) =>
        client.RemovePresetAsync(token, presetToken, cancellationToken);

    public void Dispose() => client.Dispose();
}
