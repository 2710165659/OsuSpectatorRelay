using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace OsuSpectatorRelay.Networking;

public sealed record ProxySettings(string Proxy, string Bypass, string PacUrl, bool AutoDetect)
{
    public bool IsEnabled(bool enableWpad) => Proxy.Length > 0 || PacUrl.Length > 0 || (enableWpad && AutoDetect);
}

public sealed record ProxyRoute(Uri? Proxy, bool IsSystemRoute);

public interface ISystemProxy
{
    ProxySettings Read();
    Task<ProxyRoute> ResolveAsync(ProxySettings settings, Uri target, bool enableWpad, CancellationToken cancellationToken);
}

// Read WinINET settings directly, never HttpClient.DefaultProxy: the latter is cached and
// the launcher deliberately overrides proxy environment variables in the osu child process.
public sealed class WindowsSystemProxy : ISystemProxy
{
    public ProxySettings Read()
    {
        if (!WinHttpGetIEProxyConfigForCurrentUser(out var native))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取 Windows 系统代理设置。");
        try
        {
            return new ProxySettings(Text(native.Proxy), Text(native.Bypass), Text(native.PacUrl), native.AutoDetect);
        }
        finally
        {
            Free(native.Proxy); Free(native.Bypass); Free(native.PacUrl);
        }
    }

    public async Task<ProxyRoute> ResolveAsync(ProxySettings settings, Uri target, bool enableWpad, CancellationToken cancellationToken)
    {
        if (!settings.IsEnabled(enableWpad)) return new ProxyRoute(null, false);
        if (settings.PacUrl.Length > 0 || (enableWpad && settings.AutoDetect))
            return await Task.Run(() => ResolvePac(settings, target, enableWpad), cancellationToken).ConfigureAwait(false);
        if (IsBypassed(target.Host, settings.Bypass)) return new ProxyRoute(null, true);
        return new ProxyRoute(ParseProxy(settings.Proxy, target.Scheme), true);
    }

    public static Uri? ParseProxy(string value, string scheme)
    {
        string? candidate = null;
        foreach (string entry in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int equals = entry.IndexOf('=');
            if (equals < 0) candidate ??= entry;
            else if (entry[..equals].Equals(scheme, StringComparison.OrdinalIgnoreCase))
            {
                candidate = entry[(equals + 1)..];
                break;
            }
        }
        if (candidate is null) return null;
        if (!candidate.Contains("://")) candidate = "http://" + candidate;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https" or "socks5") || uri.UserInfo.Length > 0 ||
            uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidOperationException("不支持此系统代理格式（支持 HTTP、HTTPS、SOCKS5；不支持嵌入账号密码）。");
        return uri;
    }

    public static bool IsBypassed(string host, string bypass)
    {
        foreach (string pattern in bypass.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (pattern.Equals("<local>", StringComparison.OrdinalIgnoreCase))
            {
                if (!host.Contains('.') && !host.Contains(':')) return true;
                continue;
            }
            string expression = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            if (Regex.IsMatch(host, expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return true;
        }
        return false;
    }

    private static ProxyRoute ResolvePac(ProxySettings settings, Uri target, bool enableWpad)
    {
        nint session = WinHttpOpen("OsuSpectatorRelay/1.0", 1, null, null, 0);
        if (session == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            WinHttpSetTimeouts(session, 3000, 3000, 3000, 3000);
            var options = new AutoProxyOptions
            {
                Flags = settings.PacUrl.Length > 0 ? 2u : 1u,
                AutoDetectFlags = enableWpad && settings.AutoDetect ? 3u : 0,
                AutoConfigUrl = settings.PacUrl.Length > 0 ? settings.PacUrl : null,
                AutoLogonIfChallenged = false
            };
            if (!WinHttpGetProxyForUrl(session, target.AbsoluteUri, ref options, out var result))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "PAC/WPAD 代理解析失败；未回退至 Worker。");
            try
            {
                if (result.AccessType == 1 || IsBypassed(target.Host, Text(result.Bypass))) return new ProxyRoute(null, true);
                var proxy = ParseProxy(Text(result.Proxy), target.Scheme);
                if (proxy is null) throw new InvalidOperationException("PAC 未返回可用代理。");
                return new ProxyRoute(proxy, true);
            }
            finally { Free(result.Proxy); Free(result.Bypass); }
        }
        finally { WinHttpCloseHandle(session); }
    }

    private static string Text(nint pointer) => Marshal.PtrToStringUni(pointer) ?? "";
    private static void Free(nint pointer) { if (pointer != 0) GlobalFree(pointer); }

    [StructLayout(LayoutKind.Sequential)]
    private struct IeProxyConfig
    {
        [MarshalAs(UnmanagedType.Bool)] public bool AutoDetect;
        public nint PacUrl, Proxy, Bypass;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AutoProxyOptions
    {
        public uint Flags, AutoDetectFlags;
        [MarshalAs(UnmanagedType.LPWStr)] public string? AutoConfigUrl;
        public nint Reserved;
        public uint ReservedFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool AutoLogonIfChallenged;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProxyInfo { public uint AccessType; public nint Proxy, Bypass; }

    [DllImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinHttpGetIEProxyConfigForCurrentUser(out IeProxyConfig config);
    [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint WinHttpOpen(string agent, uint accessType, string? proxy, string? bypass, uint flags);
    [DllImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinHttpSetTimeouts(nint session, int resolve, int connect, int send, int receive);
    [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinHttpGetProxyForUrl(nint session, string url, ref AutoProxyOptions options, out ProxyInfo info);
    [DllImport("winhttp.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinHttpCloseHandle(nint handle);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint pointer);
}
