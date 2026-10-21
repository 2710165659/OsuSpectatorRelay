using System.Text;

namespace OsuSpectatorRelay.Networking;

public sealed record HeaderBlock(string Text, byte[] Remainder)
{
    public static async Task<HeaderBlock> ReadAsync(Stream stream, CancellationToken token)
    {
        byte[] buffer = new byte[32768];
        int count = 0;
        while (count < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(count), token).ConfigureAwait(false);
            if (read == 0) throw new IOException("连接在 HTTP 头完整到达前关闭。");
            int start = Math.Max(0, count - 3);
            count += read;
            for (int i = start; i <= count - 4; i++)
            {
                if (buffer[i] == 13 && buffer[i + 1] == 10 && buffer[i + 2] == 13 && buffer[i + 3] == 10)
                    return new HeaderBlock(Encoding.ASCII.GetString(buffer, 0, i + 4), buffer[(i + 4)..count]);
            }
        }
        throw new InvalidDataException("HTTP 头超过 32 KiB。");
    }
}

public sealed record ProxyRequest(bool IsConnect, string Host, int Port, byte[]? ForwardHeader, byte[] Remainder)
{
    public static ProxyRequest Parse(HeaderBlock block)
    {
        string[] lines = block.Text.Split("\r\n");
        string[] first = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (first.Length != 3 || first[2] is not ("HTTP/1.0" or "HTTP/1.1"))
            throw new InvalidDataException("无效 HTTP 代理请求。");
        if (first[0] == "CONNECT")
        {
            // Only accept authority form; an absolute URL or userinfo must never change the destination.
            if (first[1].Contains('/') || first[1].Contains('@') || first[1].Contains('?') || first[1].Contains('#') ||
                !Uri.TryCreate("https://" + first[1], UriKind.Absolute, out var authority) ||
                authority.Port is < 1 or > 65535)
                throw new InvalidDataException("无效 CONNECT 地址。");
            ValidateHost(authority.IdnHost);
            return new ProxyRequest(true, authority.IdnHost, authority.Port, null, block.Remainder);
        }
        if (!Uri.TryCreate(first[1], UriKind.Absolute, out var uri) || uri.Scheme != "http" ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("普通 HTTP 请求必须使用 http 绝对地址。");
        ValidateHost(uri.IdnHost);
        // One request per connection avoids ambiguous keep-alive routing when a VPN changes.
        var header = new StringBuilder($"{first[0]} {uri.PathAndQuery} {first[2]}\r\nHost: {uri.Authority}\r\n");
        foreach (string line in lines.Skip(1))
        {
            if (line.Length == 0) continue;
            int colon = line.IndexOf(':');
            if (colon <= 0 || char.IsWhiteSpace(line[0])) throw new InvalidDataException("无效 HTTP 字段。");
            string name = line[..colon];
            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Connection", StringComparison.OrdinalIgnoreCase)) continue;
            header.Append(line).Append("\r\n");
        }
        header.Append("Connection: close\r\n\r\n");
        return new ProxyRequest(false, uri.IdnHost, uri.Port, Encoding.ASCII.GetBytes(header.ToString()), block.Remainder);
    }

    public static void ValidateHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || Uri.CheckHostName(host.Trim('[', ']')) == UriHostNameType.Unknown)
            throw new InvalidDataException("目标主机无效。");
    }
    public string Authority => Host.Contains(':') ? $"[{Host.Trim('[', ']')}]:{Port}" : $"{Host}:{Port}";
    public Uri ProbeUri => new UriBuilder(IsConnect ? "https" : "http", Host, Port).Uri;
}
