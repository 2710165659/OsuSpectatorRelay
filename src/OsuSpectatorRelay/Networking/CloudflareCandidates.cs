using System.Net;
using System.Numerics;
using System.Security.Cryptography;

namespace OsuSpectatorRelay.Networking;

// Independent snapshot of the ranges/sampling policy used by BeatmapAccel.
// No source, assembly or runtime dependency on that project.
public static class CloudflareCandidates
{
    public static readonly string[] Ipv4Ranges = [
        "173.245.48.0/20",
        "103.21.244.0/22",
        "103.22.200.0/22",
        "103.31.4.0/22",
        "141.101.64.0/18",
        "108.162.192.0/18",
        "190.93.240.0/20",
        "188.114.96.0/20",
        "197.234.240.0/22",
        "198.41.128.0/17",
        "162.158.0.0/15",
        "104.16.0.0/12",
        "172.64.0.0/17",
        "172.64.128.0/18",
        "172.64.192.0/19",
        "172.64.224.0/22",
        "172.64.229.0/24",
        "172.64.230.0/23",
        "172.64.232.0/21",
        "172.64.240.0/21",
        "172.64.248.0/21",
        "172.65.0.0/16",
        "172.66.0.0/16",
        "172.67.0.0/16",
        "131.0.72.0/22",
    ];
    public static readonly string[] Ipv6Ranges = [
        "2400:cb00::/32",
        "2400:cb00:2049::/48",
        "2400:cb00:f00e::/48",
        "2606:4700::/32",
        "2606:4700:10::/48",
        "2606:4700:130::/48",
        "2606:4700:3000::/48",
        "2606:4700:3001::/48",
        "2606:4700:3002::/48",
        "2606:4700:3003::/48",
        "2606:4700:3004::/48",
        "2606:4700:3005::/48",
        "2606:4700:3006::/48",
        "2606:4700:3007::/48",
        "2606:4700:3008::/48",
        "2606:4700:3009::/48",
        "2606:4700:3010::/48",
        "2606:4700:3011::/48",
        "2606:4700:3012::/48",
        "2606:4700:3013::/48",
        "2606:4700:3014::/48",
        "2606:4700:3015::/48",
        "2606:4700:3016::/48",
        "2606:4700:3017::/48",
        "2606:4700:3018::/48",
        "2606:4700:3019::/48",
        "2606:4700:3020::/48",
        "2606:4700:3021::/48",
        "2606:4700:3022::/48",
        "2606:4700:3023::/48",
        "2606:4700:3024::/48",
        "2606:4700:3025::/48",
        "2606:4700:3026::/48",
        "2606:4700:3027::/48",
        "2606:4700:3028::/48",
        "2606:4700:3029::/48",
        "2606:4700:3030::/48",
        "2606:4700:3031::/48",
        "2606:4700:3032::/48",
        "2606:4700:3033::/48",
        "2606:4700:3034::/48",
        "2606:4700:3035::/48",
        "2606:4700:3036::/48",
        "2606:4700:3037::/48",
        "2606:4700:3038::/48",
        "2606:4700:3039::/48",
        "2606:4700:a0::/48",
        "2606:4700:a1::/48",
        "2606:4700:a8::/48",
        "2606:4700:a9::/48",
        "2606:4700:a::/48",
        "2606:4700:b::/48",
        "2606:4700:c::/48",
        "2606:4700:d0::/48",
        "2606:4700:d1::/48",
        "2606:4700:d::/48",
        "2606:4700:e0::/48",
        "2606:4700:e1::/48",
        "2606:4700:e2::/48",
        "2606:4700:e3::/48",
        "2606:4700:e4::/48",
        "2606:4700:e5::/48",
        "2606:4700:e6::/48",
        "2606:4700:e7::/48",
        "2606:4700:e::/48",
        "2606:4700:f1::/48",
        "2606:4700:f2::/48",
        "2606:4700:f3::/48",
        "2606:4700:f4::/48",
        "2606:4700:f5::/48",
        "2606:4700:f::/48",
        "2803:f800::/32",
        "2803:f800:50::/48",
        "2803:f800:51::/48",
        "2405:b500::/32",
        "2405:8100::/32",
        "2a06:98c0::/29",
        "2a06:98c1:3100::/48",
        "2a06:98c1:3101::/48",
        "2a06:98c1:3102::/48",
        "2a06:98c1:3103::/48",
        "2a06:98c1:3104::/48",
        "2a06:98c1:3105::/48",
        "2a06:98c1:3106::/48",
        "2a06:98c1:3107::/48",
        "2a06:98c1:3108::/48",
        "2a06:98c1:3109::/48",
        "2a06:98c1:310a::/48",
        "2a06:98c1:310b::/48",
        "2a06:98c1:310c::/48",
        "2a06:98c1:310d::/48",
        "2a06:98c1:310e::/48",
        "2a06:98c1:310f::/48",
        "2a06:98c1:3120::/48",
        "2a06:98c1:3121::/48",
        "2a06:98c1:3122::/48",
        "2a06:98c1:3123::/48",
        "2a06:98c1:3200::/48",
        "2a06:98c1:50::/48",
        "2a06:98c1:51::/48",
        "2a06:98c1:54::/48",
        "2a06:98c1:58::/48",
        "2c0f:f248::/32",
    ];

    public static string[] Build(AppConfig config, string? current)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (current is not null) candidates.Add(IPAddress.Parse(current).ToString());
        foreach (string ip in config.PreferredIps) candidates.Add(IPAddress.Parse(ip).ToString());
        if (config.AutoGenerateCandidates)
        {
            foreach (string range in Ipv4Ranges) Sample(range, 2, candidates);
            if (config.EnableIpv6Candidates)
                foreach (string range in Ipv6Ranges) Sample(range, 1, candidates);
        }
        return candidates.ToArray();
    }

    private static void Sample(string range, int count, HashSet<string> candidates)
    {
        string[] parts = range.Split('/');
        byte[] bytes = IPAddress.Parse(parts[0]).GetAddressBytes();
        int hostBits = bytes.Length * 8 - int.Parse(parts[1]);
        BigInteger hostMask = (BigInteger.One << hostBits) - 1;
        BigInteger network = new BigInteger(bytes, isUnsigned: true, isBigEndian: true) & ~hostMask;
        var offsets = new HashSet<BigInteger>();
        while (offsets.Count < count)
        {
            RandomNumberGenerator.Fill(bytes);
            BigInteger offset = new BigInteger(bytes, isUnsigned: true, isBigEndian: true) & hostMask;
            if (bytes.Length == 4 && (offset == 0 || offset == hostMask)) continue;
            if (!offsets.Add(offset)) continue;
            byte[] result = (network + offset).ToByteArray(isUnsigned: true, isBigEndian: true);
            byte[] padded = new byte[bytes.Length];
            result.CopyTo(padded, padded.Length - result.Length);
            candidates.Add(new IPAddress(padded).ToString());
        }
    }
}