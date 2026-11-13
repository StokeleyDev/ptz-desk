using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Xml;
using SharpOnvifClient.DeviceMgmt;
using SharpOnvifCommon.Soap;
using SharpOnvifCommon.Xml;

namespace AGLPTZ;

internal static class OnvifDiagnostics
{
    internal const string Generic = "Не удалось подключиться. Проверьте IP, доступность ONVIF и учётные данные.";

    internal static async Task<string> ExplainFailureAsync(Uri endpoint, Exception error, bool timedOut)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromMilliseconds(2450));
        try
        {
            return await Task.Run(() => ExplainAsync(endpoint, error, timedOut, budget.Token))
                .WaitAsync(budget.Token).ConfigureAwait(false);
        }
        catch (Exception) { return Generic; }
    }

    private static async Task<string> ExplainAsync(Uri endpoint, Exception error, bool timedOut, CancellationToken token)
    {
        var causes = Chain(error).ToArray();
        if (causes.Any(IsAuthorizationFailure))
        {
            try
            {
                using var device = new DeviceClient(endpoint.AbsoluteUri, new OnvifClientSettings
                {
                    Timeout = TimeSpan.FromSeconds(2), MaxResponseContentBytes = 64 * 1024,
                });
                var utc = (await device.GetSystemDateAndTimeAsync(token).ConfigureAwait(false)).SystemDateAndTime?.UTCDateTime;
                if (utc?.Date is { } date && utc.Time is { } time)
                {
                    var cameraTime = new DateTime(date.Year, date.Month, date.Day, time.Hour, time.Minute, time.Second, DateTimeKind.Utc);
                    var delta = (cameraTime - DateTime.UtcNow).Duration();
                    if (delta.TotalSeconds > 60)
                        return $"Время камеры отличается от времени ПК на {delta.TotalMinutes:F1} мин. ONVIF отклоняет вход при расхождении часов: синхронизируйте время в камере (NTP).";
                }
            }
            catch (Exception) { /* Some cameras require authentication for their clock. */ }
            return "Неверный ONVIF-логин или пароль. Учётная запись веб-интерфейса может не подходить: создайте отдельного ONVIF-пользователя в камере.";
        }

        var host = endpoint.DnsSafeHost.Trim('[', ']');
        if (causes.Any(e => e is SocketException { SocketErrorCode: SocketError.ConnectionRefused }))
            return ClosedPort(endpoint.Port);
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(token);
        probe.CancelAfter(TimeSpan.FromMilliseconds(1500));
        using var tcp = new TcpClient();
        try { await tcp.ConnectAsync(host, endpoint.Port, probe.Token).ConfigureAwait(false); }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return ClosedPort(endpoint.Port);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException) { return NoResponse(host); }

        if (timedOut || causes.Any(e => e is OperationCanceledException or TimeoutException or SoapTransportException { TimedOut: true }))
            return NoResponse(host);
        if (causes.Any(e => e is HttpRequestException { StatusCode: HttpStatusCode.NotFound } or XmlException ||
            e is SoapFaultException or SoapTransportException && HasHttpStatus(e.Message, 404)))
            return $"По адресу {endpoint.Authority} нет службы ONVIF. Включите ONVIF в камере или проверьте порт.";
        return Generic;
    }

    private static bool IsAuthorizationFailure(Exception error) =>
        error is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } ||
        error is SoapFaultException or SoapTransportException && (HasHttpStatus(error.Message, 401) || HasHttpStatus(error.Message, 403)) ||
        error is SoapFaultException fault && (fault.Fault.Subcodes?.Any(IsDenied) == true ||
            IsDenied(fault.Fault.Code) || IsDenied(fault.Fault.Reason));

    private static bool HasHttpStatus(string message, int code) =>
        message.Contains($"HTTP {code} ", StringComparison.OrdinalIgnoreCase);

    private static string ClosedPort(int port) =>
        $"Камера отвечает, но порт {port} закрыт. Включите ONVIF в настройках камеры или укажите другой порт: IP:порт.";

    private static bool IsDenied(string? text) => text != null && (text.Contains("NotAuthorized", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("not authorized", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<Exception> Chain(Exception error)
    {
        for (Exception? cause = error; cause != null; cause = cause.InnerException) yield return cause;
    }

    private static string NoResponse(string host)
    {
        var message = $"Нет ответа от {host}. Проверьте питание и кабель камеры и что ПК и камера в одной сети.";
        if (!IPAddress.TryParse(host, out var camera)) return message;
        try
        {
            var adapters = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .Select(n => n.GetIPProperties()).SelectMany(p => p.UnicastAddresses.Select(a =>
                    (a.Address, a.PrefixLength, Gateway: p.GatewayAddresses.Any(g =>
                        g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))));
            return message + SubnetHint(camera, adapters);
        }
        catch (NetworkInformationException) { return message; }
    }

    internal static string SubnetHint(IPAddress camera, IEnumerable<(IPAddress Address, int Prefix, bool Gateway)> addresses)
    {
        if (camera.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(camera)) return "";
        var usable = addresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork &&
            !IPAddress.IsLoopback(a.Address) && !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal) &&
            a.Prefix is >= 1 and <= 32).OrderByDescending(a => a.Gateway).ToArray();
        if (usable.Any(a => RemoteNetwork.InSameSubnet(camera, a.Address, a.Prefix))) return "";
        var local = usable.Length == 0 ? "нет подходящего IPv4" : $"{usable[0].Address}/{usable[0].Prefix}";
        var bytes = camera.GetAddressBytes();
        int suffix = bytes[3] == 10 ? 11 : 10;
        return $" IP камеры {camera} не из подсети этого ПК ({local}). Подключите камеру кабелем и задайте сетевой карте адрес {bytes[0]}.{bytes[1]}.{bytes[2]}.{suffix} с маской 255.255.255.0, либо измените IP камеры на адрес из вашей сети.";
    }
}
