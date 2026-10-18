using System;
using System.Net;

namespace ET;

public static class NetworkExposurePolicy
{
    public const string RouterPublicAcknowledgement = "firewall-ddos-rate-limit-v1";

    // 解析 RouterManager 的安全监听前缀。
    public static string ResolveRouterManagerPrefix(Options options, int port)
    {
        if (port is < 1024 or > 65535) throw new InvalidOperationException("RouterManager port is invalid");
        string configured = Environment.GetEnvironmentVariable("ET_ROUTER_MANAGER_BIND_IP");
        if (options.Develop != 0 && string.IsNullOrWhiteSpace(configured)) return $"http://*:{port}/";
        IPAddress address = ParseExplicitAddress(configured, "RouterManager bind IP");
        if (!IPAddress.IsLoopback(address))
            throw new InvalidOperationException("Production RouterManager HTTP must bind loopback behind the ingress proxy");
        return $"http://{FormatHost(address)}:{port}/";
    }

    // 解析路由 WebSocket 的绑定地址。
    public static string ResolveRouterWebSocketBindHost(Options options)
    {
        string configured = Environment.GetEnvironmentVariable("ET_ROUTER_WEBSOCKET_BIND_IP");
        if (options.Develop != 0 && string.IsNullOrWhiteSpace(configured)) return "+";
        IPAddress address = ParseExplicitAddress(configured, "Router WebSocket bind IP");
        if (!IPAddress.IsLoopback(address))
            throw new InvalidOperationException("Production plain WebSocket must bind loopback behind a TLS ingress proxy");
        return FormatHost(address);
    }

    // 校验路由服务不会暴露到不安全的网络边界。
    public static void ValidateRouter(Options options, IPEndPoint outerAddress, string innerIp)
    {
        if (options.Develop != 0) return;
        if (outerAddress == null || IsWildcard(outerAddress.Address))
            throw new InvalidOperationException("Production Router outer listener requires an explicit IP");
        IPAddress inner = ParseExplicitAddress(innerIp, "Router inner IP");
        if (!IsPrivateOrLoopback(inner))
            throw new InvalidOperationException("Production Router inner transport must use a private or loopback address");
        if (!IPAddress.IsLoopback(outerAddress.Address) &&
            Environment.GetEnvironmentVariable("ET_ROUTER_PUBLIC_EXPOSURE_ACK") != RouterPublicAcknowledgement)
            throw new InvalidOperationException("Production public Router requires the firewall and DDoS control acknowledgement");
    }

    // 解析显式 IP 地址，并拒绝无效配置。
    private static IPAddress ParseExplicitAddress(string text, string name)
    {
        if (string.IsNullOrWhiteSpace(text) || !IPAddress.TryParse(text, out IPAddress address) || IsWildcard(address))
            throw new InvalidOperationException($"{name} must be an explicit IP address");
        return address;
    }

    // 判断地址是否为全网卡通配地址。
    private static bool IsWildcard(IPAddress address) => address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);

    // 按 IPv4 或 IPv6 格式化监听主机。
    private static string FormatHost(IPAddress address) => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
        ? $"[{address}]" : address.ToString();

    // 判断地址是否仅属于私网或回环网段。
    private static bool IsPrivateOrLoopback(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        byte[] bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
            return bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168) || (bytes[0] == 169 && bytes[1] == 254);
        return address.IsIPv6LinkLocal || (bytes[0] & 0xfe) == 0xfc;
    }
}
