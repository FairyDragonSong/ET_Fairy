using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.WebSockets;

namespace ET
{
    public class WService: AService
    {
        private long idGenerater = 200000000;
        
        private HttpListener httpListener;
        
        private readonly Dictionary<long, WChannel> channels = new Dictionary<long, WChannel>();
        private readonly string expectedPath;
        private readonly int maxConnections;

        public ThreadSynchronizationContext ThreadSynchronizationContext;

        public WService(IEnumerable<string> prefixs, string expectedPath = null, int maxConnections = 4096)
        {
            if (maxConnections < 1 || maxConnections > 100000)
            {
                throw new ArgumentOutOfRangeException(nameof(maxConnections));
            }
            this.expectedPath = expectedPath;
            this.maxConnections = maxConnections;
            this.ThreadSynchronizationContext = new ThreadSynchronizationContext();
            
            this.httpListener = new HttpListener();

            StartAccept(prefixs).Coroutine();
        }
        
        public WService()
        {
            this.ServiceType = ServiceType.Outer;
            this.ThreadSynchronizationContext = new ThreadSynchronizationContext();
        }
        
        private long GetId
        {
            get
            {
                return ++this.idGenerater;
            }
        }

        public bool IsListening => this.httpListener?.IsListening == true;
        
        public override void Create(long id, IPEndPoint ipEndpoint)
        {
			ClientWebSocket webSocket = new();
            WChannel channel = new(id, webSocket, ipEndpoint, this);
            this.channels[channel.Id] = channel;
        }

        public override void Update()
        {
            this.ThreadSynchronizationContext.Update();
        }

        public override void Remove(long id, int error = 0)
        {
            WChannel channel;
            if (!this.channels.TryGetValue(id, out channel))
            {
                return;
            }

            channel.Error = error;

            this.channels.Remove(id);
            channel.Dispose();
        }

        public override bool IsDisposed()
        {
            return this.ThreadSynchronizationContext == null;
        }

        protected void Get(long id, IPEndPoint ipEndPoint)
        {
            if (!this.channels.TryGetValue(id, out _))
            {
                this.Create(id, ipEndPoint);
            }
        }
        
        public WChannel Get(long id)
        {
            WChannel channel = null;
            this.channels.TryGetValue(id, out channel);
            return channel;
        }

        public override void Dispose()
        {
            base.Dispose();
            
            this.ThreadSynchronizationContext = null;
            this.httpListener?.Close();
            this.httpListener = null;
        }

        private async ETTask StartAccept(IEnumerable<string> prefixs)
        {
            try
            {
                foreach (string prefix in prefixs)
                {
                    this.httpListener.Prefixes.Add(prefix);
                }
                
                httpListener.Start();

                while (true)
                {
                    try
                    {
                        HttpListenerContext httpListenerContext = await this.httpListener.GetContextAsync();

                        if (this.expectedPath != null &&
                            (httpListenerContext.Request.HttpMethod != "GET" ||
                             httpListenerContext.Request.Url.AbsolutePath != this.expectedPath ||
                             httpListenerContext.Request.Url.Query.Length != 0 ||
                             !httpListenerContext.Request.IsWebSocketRequest))
                        {
                            httpListenerContext.Response.Headers["Cache-Control"] = "no-store";
                            httpListenerContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                            httpListenerContext.Response.Close();
                            continue;
                        }
                        if (this.channels.Count >= this.maxConnections)
                        {
                            httpListenerContext.Response.Headers["Cache-Control"] = "no-store";
                            httpListenerContext.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                            httpListenerContext.Response.Close();
                            continue;
                        }

                        HttpListenerWebSocketContext webSocketContext = await httpListenerContext.AcceptWebSocketAsync(null);

                        WChannel channel = new WChannel(this.GetId, webSocketContext, this);
                        channel.RemoteAddress = httpListenerContext.Request.RemoteEndPoint;
                        this.channels[channel.Id] = channel;

                        this.AcceptCallback(channel.Id, channel.RemoteAddress);
                    }
                    catch (Exception e)
                    {
                        if (this.IsDisposed() || !this.IsListening)
                        {
                            return;
                        }
                        Log.Error(e);
                    }
                }
            }
            catch (HttpListenerException e)
            {
                if (e.ErrorCode == 5)
                {
                    throw new Exception($"WebSocket URL ACL is not authorized for prefixes: {prefixs.ToList().ListToString()}", e);
                }

                Log.Error(e);
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }

        public override void Send(long channelId, MemoryBuffer memoryBuffer)
        {
            this.channels.TryGetValue(channelId, out WChannel channel);
            if (channel == null)
            {
                return;
            }
            channel.Send(memoryBuffer);
        }
    }
}
