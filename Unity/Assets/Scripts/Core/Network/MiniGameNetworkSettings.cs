using System;
using System.Globalization;
using System.Net;

namespace ET
{
    public static class MiniGameNetworkSettings
    {
        public static string RouterHttpUrl { get; private set; } = "http://127.0.0.1:30300/get_router";

        public static string RouterWebSocketUrl { get; private set; } = "ws://{0}:{2}/et/";

        public static int RouterWebSocketPortOffset { get; private set; } = 1000;

        public static void Configure(string routerHttpUrl, string routerWebSocketUrl, int routerWebSocketPortOffset)
        {
            if (!string.IsNullOrWhiteSpace(routerHttpUrl))
            {
                RouterHttpUrl = routerHttpUrl.Trim();
            }

            if (!string.IsNullOrWhiteSpace(routerWebSocketUrl))
            {
                RouterWebSocketUrl = routerWebSocketUrl.Trim();
            }

            if (routerWebSocketPortOffset < 0 || routerWebSocketPortOffset > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(routerWebSocketPortOffset),
                    routerWebSocketPortOffset,
                    "Router WebSocket port offset must be between 0 and 65535.");
            }

            RouterWebSocketPortOffset = routerWebSocketPortOffset;
        }

        public static string GetRouterWebSocketUrl(IPEndPoint routerAddress)
        {
            int webSocketPort = routerAddress.Port + RouterWebSocketPortOffset;
            if (webSocketPort > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(routerAddress),
                    $"Router WebSocket port is out of range: {routerAddress.Port} + {RouterWebSocketPortOffset}");
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                RouterWebSocketUrl,
                routerAddress.Address,
                routerAddress.Port,
                webSocketPort);
        }
    }
}
