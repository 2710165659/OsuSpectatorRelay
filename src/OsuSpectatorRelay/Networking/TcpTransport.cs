using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace OsuSpectatorRelay.Networking;

public static class TcpTransport
{
    public static async Task<Stream> ConnectAsync(string host, int port, Uri? proxy, int localPort, CancellationToken token)
    {
        var client = new TcpClient { NoDelay = true };
        Stream? stream = null;
        try
        {
            if (proxy is not null)
            {
                // Never connect back into our own listener, including local machine aliases.
                if (proxy.Port == localPort)
                {
                    var addresses = await Dns.GetHostAddressesAsync(proxy.DnsSafeHost, token).ConfigureAwait(false);
                    var local = Dns.GetHostAddresses(Dns.GetHostName());
                    if (addresses.Any(ip => IPAddress.IsLoopback(ip) || local.Contains(ip)))
                        throw new IOException("系统代理指向本工具自身，已阻止代理循环。");
                }
                await client.ConnectAsync(proxy.DnsSafeHost, proxy.Port, token).ConfigureAwait(false);
            }
            else await client.ConnectAsync(host.Trim('[', ']'), port, token).ConfigureAwait(false);
            stream = client.GetStream();
            if (proxy?.Scheme == "https")
            {
                var tls = new SslStream(stream, leaveInnerStreamOpen: false);
                stream = tls;
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = proxy.DnsSafeHost }, token).ConfigureAwait(false);
            }
            if (proxy?.Scheme == "socks5") await SocksConnectAsync(stream, host, port, token).ConfigureAwait(false);
            else if (proxy is not null)
            {
                string authority = host.Contains(':') ? $"[{host.Trim('[', ']')}]:{port}" : $"{host}:{port}";
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n\r\n"), token).ConfigureAwait(false);
                var response = await HeaderBlock.ReadAsync(stream, token).ConfigureAwait(false);
                string[] status = response.Text.Split("\r\n")[0].Split(' ');
                if (status.Length < 2 || status[1] != "200")
                {
                    if (status.Length > 1 && status[1] == "407")
                        throw new NonRetryableException("上游系统代理要求认证；当前仅支持无需认证的本机 VPN 代理。");
                    throw new IOException("上游系统代理 CONNECT 失败。");
                }
                if (response.Remainder.Length != 0) throw new IOException("上游代理提前发送了非预期数据。");
            }
            // NetworkStream owns the socket; SslStream owns the inner stream.
            return stream;
        }
        catch
        {
            if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
            client.Dispose();
            throw;
        }
    }

    private static async Task SocksConnectAsync(Stream stream, string host, int port, CancellationToken token)
    {
        await stream.WriteAsync(new byte[] { 5, 1, 0 }, token).ConfigureAwait(false);
        byte[] hello = new byte[2];
        await stream.ReadExactlyAsync(hello, token).ConfigureAwait(false);
        if (hello[0] != 5 || hello[1] != 0) throw new IOException("SOCKS5 代理不支持免认证连接。");
        byte[] domain = Encoding.ASCII.GetBytes(host.Trim('[', ']'));
        if (domain.Length > 255) throw new IOException("SOCKS5 目标过长。");
        byte[] request = new byte[7 + domain.Length];
        request[0] = 5; request[1] = 1; request[3] = 3; request[4] = (byte)domain.Length;
        domain.CopyTo(request, 5);
        request[^2] = (byte)(port >> 8); request[^1] = (byte)port;
        await stream.WriteAsync(request, token).ConfigureAwait(false);
        byte[] reply = new byte[4];
        await stream.ReadExactlyAsync(reply, token).ConfigureAwait(false);
        if (reply[0] != 5 || reply[1] != 0) throw new IOException("SOCKS5 CONNECT 失败。");
        int addressLength;
        if (reply[3] == 1) addressLength = 4;
        else if (reply[3] == 4) addressLength = 16;
        else if (reply[3] == 3)
        {
            byte[] length = new byte[1];
            await stream.ReadExactlyAsync(length, token).ConfigureAwait(false);
            addressLength = length[0];
        }
        else throw new IOException("SOCKS5 响应地址无效。");
        await stream.ReadExactlyAsync(new byte[addressLength + 2], token).ConfigureAwait(false);
    }
}
