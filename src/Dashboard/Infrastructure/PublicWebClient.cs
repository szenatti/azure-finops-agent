using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AzureFinOps.Dashboard.Infrastructure;

internal static class PublicWebClient
{
    internal const int MaxBytes = 600_000;
    private static readonly IPNetwork[] BlockedNetworks =
    [
        IPNetwork.Parse("0.0.0.0/8"), IPNetwork.Parse("10.0.0.0/8"), IPNetwork.Parse("100.64.0.0/10"),
        IPNetwork.Parse("127.0.0.0/8"), IPNetwork.Parse("169.254.0.0/16"), IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.0.0.0/24"), IPNetwork.Parse("192.0.2.0/24"), IPNetwork.Parse("192.88.99.0/24"),
        IPNetwork.Parse("192.168.0.0/16"), IPNetwork.Parse("198.18.0.0/15"), IPNetwork.Parse("198.51.100.0/24"),
        IPNetwork.Parse("203.0.113.0/24"), IPNetwork.Parse("224.0.0.0/4"), IPNetwork.Parse("240.0.0.0/4"),
        IPNetwork.Parse("168.63.129.16/32"),
        IPNetwork.Parse("2001::/23"), IPNetwork.Parse("2001:db8::/32"), IPNetwork.Parse("2002::/16"),
        IPNetwork.Parse("3fff::/20")
    ];
    private static readonly IPNetwork GlobalIpv6 = IPNetwork.Parse("2000::/3");

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return (address.AddressFamily == AddressFamily.InterNetwork
                || address.AddressFamily == AddressFamily.InterNetworkV6 && GlobalIpv6.Contains(address))
            && !BlockedNetworks.Any(network => network.Contains(address));
    }

    internal static bool IsAllowedUri(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443
        && uri.UserInfo.Length == 0 && uri.HostNameType == UriHostNameType.Dns
        && uri.IdnHost.TrimEnd('.').Contains('.')
        && !new[] { ".localhost", ".local", ".internal" }.Any(suffix =>
            uri.IdnHost.TrimEnd('.').EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    internal static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = ConnectPublicAsync
    }) { Timeout = Timeout.InfiniteTimeSpan };

    private static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        if (context.DnsEndPoint.Port != 443) throw new HttpRequestException("Public HTTPS destination required.");
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
            throw new HttpRequestException("Public HTTPS destination required.");

        foreach (var address in addresses.OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, 443), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException) { socket.Dispose(); }
            catch { socket.Dispose(); throw; }
        }
        throw new HttpRequestException("Public HTTPS connection failed.");
    }

    internal sealed record Page(HttpStatusCode Status, Uri Uri, string ContentType, string Body, int Bytes);

    internal static async Task<Page> ReadAsync(HttpClient http, Uri uri, CancellationToken cancellationToken)
    {
        for (var redirect = 0; redirect <= 5; redirect++)
        {
            if (!IsAllowedUri(uri)) throw new HttpRequestException("Public HTTPS destination required.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                if (response.Headers.Location is null || !Uri.TryCreate(uri, response.Headers.Location, out var next))
                    throw new HttpRequestException("Invalid public HTTPS redirect.");
                uri = next;
                continue;
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "text/plain";
            if (!contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                && contentType is not ("application/json" or "application/xml" or "application/xhtml+xml"))
                throw new HttpRequestException("Only text, HTML, JSON and XML responses are supported.");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var body = new MemoryStream();
            var buffer = new byte[16_384];
            while (body.Length < MaxBytes)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, MaxBytes - (int)body.Length)), cancellationToken);
                if (read == 0) break;
                body.Write(buffer, 0, read);
            }
            return new Page(response.StatusCode, uri, contentType, Encoding.UTF8.GetString(body.ToArray()), (int)body.Length);
        }
        throw new HttpRequestException("Public HTTPS redirect limit exceeded.");
    }
}