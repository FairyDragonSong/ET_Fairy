#if !UNITY_WEBGL && !UNITY_WEIXINMINIGAME && !TUANJIE_WEIXINMINIGAME
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;

namespace ET
{
    /// <summary>
    /// Exposes the existing Router datagram protocol through binary WebSocket frames.
    /// TLS should terminate at a reverse proxy; this listener receives plain ws traffic.
    /// </summary>
    public sealed class WebSocketRouterTransport: IKcpTransport
    {
        private readonly WService service;
        private readonly Dictionary<long, IPEndPoint> channelEndpoints = new();
        private readonly Dictionary<string, long> endpointChannels = new();
        private readonly Queue<(IPEndPoint EndPoint, MemoryBuffer Buffer)> received = new();

        // 创建 Router WebSocket 传输层，并显式绑定主机、路径和连接上限。
        public WebSocketRouterTransport(int port, string path = "et", string bindHost = "+", int maxConnections = 4096)
        {
            string normalizedPath = string.IsNullOrWhiteSpace(path) ? "et" : path.Trim('/');
            this.service = new WService(
                new[] { $"http://{bindHost}:{port}/{normalizedPath}/" },
                $"/{normalizedPath}/",
                maxConnections);
            if (!this.service.IsListening)
            {
                throw new InvalidOperationException("Router WebSocket listener failed to start");
            }
            this.service.AcceptCallback = this.OnAccept;
            this.service.ReadCallback = this.OnRead;
            this.service.ErrorCallback = this.OnChannelError;
            Log.Info($"Router WebSocket listener started: host={bindHost} port={port} path=/{normalizedPath}/");
        }

        public void Send(byte[] bytes, int index, int length, EndPoint endPoint, ChannelType channelType)
        {
            if (endPoint == null || !this.endpointChannels.TryGetValue(endPoint.ToString(), out long channelId))
            {
                return;
            }

            MemoryBuffer buffer = this.service.Fetch(length);
            buffer.Write(bytes, index, length);
            buffer.Seek(0, SeekOrigin.Begin);
            this.service.Send(channelId, buffer);
        }

        public int Recv(byte[] buffer, ref EndPoint endPoint)
        {
            (IPEndPoint remoteEndPoint, MemoryBuffer memoryBuffer) = this.received.Dequeue();
            int count = memoryBuffer.Read(buffer);
            this.service.Recycle(memoryBuffer);
            endPoint = remoteEndPoint;
            return count;
        }

        public int Available()
        {
            return this.received.Count;
        }

        public void Update()
        {
            this.service.Update();
        }

        public void OnError(long id, int error)
        {
            this.OnChannelError(id, error);
        }

        public void Dispose()
        {
            while (this.received.Count > 0)
            {
                this.service.Recycle(this.received.Dequeue().Buffer);
            }

            this.channelEndpoints.Clear();
            this.endpointChannels.Clear();
            this.service.Dispose();
        }

        private void OnAccept(long channelId, IPEndPoint remoteAddress)
        {
            int port = 1024 + (int)((ulong)channelId % (ushort.MaxValue - 1024));
            IPEndPoint endpoint = new(remoteAddress.Address, port);
            while (this.endpointChannels.ContainsKey(endpoint.ToString()))
            {
                port = port == ushort.MaxValue ? 1024 : port + 1;
                endpoint = new IPEndPoint(remoteAddress.Address, port);
            }

            this.channelEndpoints.Add(channelId, endpoint);
            this.endpointChannels.Add(endpoint.ToString(), channelId);
        }

        private void OnRead(long channelId, MemoryBuffer buffer)
        {
            if (!this.channelEndpoints.TryGetValue(channelId, out IPEndPoint endpoint))
            {
                this.service.Recycle(buffer);
                return;
            }

            this.received.Enqueue((endpoint, buffer));
        }

        private void OnChannelError(long channelId, int error)
        {
            if (!this.channelEndpoints.Remove(channelId, out IPEndPoint endpoint))
            {
                return;
            }

            this.endpointChannels.Remove(endpoint.ToString());
            Log.Warning($"Router WebSocket channel closed: {endpoint}, error={error}");
        }
    }
}
#endif
