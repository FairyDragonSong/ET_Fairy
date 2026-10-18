#if UNITY_WEBGL || UNITY_WEIXINMINIGAME || TUANJIE_WEIXINMINIGAME
using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.InteropServices;
using AOT;

namespace ET
{
    internal static class MiniGameWebSocketBridge
    {
        private sealed class Callbacks
        {
            public Action Open;
            public Action<byte[]> Message;
            public Action<string> Error;
            public Action<int, string> Close;
        }

        private delegate void OpenCallback(int socketId);
        private delegate void MessageCallback(int socketId, IntPtr data, int length);
        private delegate void ErrorCallback(int socketId, IntPtr message);
        private delegate void CloseCallback(int socketId, int code, IntPtr reason);

        // 保持回调引用，避免原生层调用时被 GC 回收。
        [StaticField] private static readonly Dictionary<int, Callbacks> Sockets = new();
        [StaticField] private static readonly OpenCallback OpenDelegate = OnOpen;
        [StaticField] private static readonly MessageCallback MessageDelegate = OnMessage;
        [StaticField] private static readonly ErrorCallback ErrorDelegate = OnError;
        [StaticField] private static readonly CloseCallback CloseDelegate = OnClose;
        [StaticField] private static bool initialized;

        [DllImport("__Internal")]
        private static extern void ETMiniGameWebSocketSetCallbacks(
            OpenCallback open,
            MessageCallback message,
            ErrorCallback error,
            CloseCallback close);

        [DllImport("__Internal")]
        private static extern int ETMiniGameWebSocketCreate(string url);

        [DllImport("__Internal")]
        private static extern int ETMiniGameWebSocketConnect(int socketId);

        [DllImport("__Internal")]
        private static extern int ETMiniGameWebSocketSend(int socketId, byte[] data, int length);

        [DllImport("__Internal")]
        private static extern void ETMiniGameWebSocketClose(int socketId);

        [DllImport("__Internal")]
        private static extern void ETMiniGameWebSocketFree(int socketId);

        public static int Create(
            string url,
            Action open,
            Action<byte[]> message,
            Action<string> error,
            Action<int, string> close)
        {
            EnsureInitialized();
            int socketId = ETMiniGameWebSocketCreate(url);
            if (socketId <= 0)
            {
                throw new InvalidOperationException($"Unable to allocate WebSocket for {url}, code: {socketId}");
            }

            Sockets.Add(socketId, new Callbacks
            {
                Open = open,
                Message = message,
                Error = error,
                Close = close,
            });

            int result = ETMiniGameWebSocketConnect(socketId);
            if (result != 0)
            {
                Sockets.Remove(socketId);
                ETMiniGameWebSocketFree(socketId);
                throw new InvalidOperationException($"Unable to connect WebSocket {url}, code: {result}");
            }

            return socketId;
        }

        public static int Send(int socketId, byte[] data)
        {
            return ETMiniGameWebSocketSend(socketId, data, data.Length);
        }

        public static void Dispose(int socketId)
        {
            Release(socketId, true);
        }

        public static void ReleaseClosed(int socketId)
        {
            Release(socketId, false);
        }

        private static void Release(int socketId, bool close)
        {
            Sockets.Remove(socketId);
            if (close)
            {
                ETMiniGameWebSocketClose(socketId);
            }
            ETMiniGameWebSocketFree(socketId);
        }

        private static void EnsureInitialized()
        {
            if (initialized)
            {
                return;
            }

            ETMiniGameWebSocketSetCallbacks(OpenDelegate, MessageDelegate, ErrorDelegate, CloseDelegate);
            initialized = true;
        }

        [MonoPInvokeCallback(typeof(OpenCallback))]
        private static void OnOpen(int socketId)
        {
            if (Sockets.TryGetValue(socketId, out Callbacks callbacks))
            {
                callbacks.Open?.Invoke();
            }
        }

        [MonoPInvokeCallback(typeof(MessageCallback))]
        private static void OnMessage(int socketId, IntPtr data, int length)
        {
            if (!Sockets.TryGetValue(socketId, out Callbacks callbacks))
            {
                return;
            }

            byte[] bytes = new byte[length];
            Marshal.Copy(data, bytes, 0, length);
            callbacks.Message?.Invoke(bytes);
        }

        [MonoPInvokeCallback(typeof(ErrorCallback))]
        private static void OnError(int socketId, IntPtr message)
        {
            if (Sockets.TryGetValue(socketId, out Callbacks callbacks))
            {
                callbacks.Error?.Invoke(Marshal.PtrToStringAnsi(message));
            }
        }

        [MonoPInvokeCallback(typeof(CloseCallback))]
        private static void OnClose(int socketId, int code, IntPtr reason)
        {
            if (Sockets.TryGetValue(socketId, out Callbacks callbacks))
            {
                callbacks.Close?.Invoke(code, Marshal.PtrToStringAnsi(reason));
            }
        }
    }

    public sealed class MiniGameWebSocketTransport: IKcpTransport
    {
        private sealed class SocketState
        {
            public int Id;
            public IPEndPoint EndPoint;
            public bool IsOpen;
            public readonly Queue<byte[]> Pending = new();
        }

        private readonly Dictionary<string, SocketState> sockets = new();
        private readonly Queue<(IPEndPoint EndPoint, byte[] Data)> received = new();
        private bool disposed;

        public void Send(byte[] bytes, int index, int length, EndPoint endPoint, ChannelType channelType)
        {
            if (this.disposed)
            {
                return;
            }

            if (endPoint is not IPEndPoint ipEndPoint)
            {
                throw new ArgumentException($"WebSocket transport requires IPEndPoint, got: {endPoint}", nameof(endPoint));
            }

            byte[] packet = new byte[length];
            Buffer.BlockCopy(bytes, index, packet, 0, length);
            SocketState state = this.GetOrCreate(ipEndPoint);
            if (!state.IsOpen)
            {
                state.Pending.Enqueue(packet);
                return;
            }

            this.SendPacket(state, packet);
        }

        public int Recv(byte[] buffer, ref EndPoint endPoint)
        {
            (IPEndPoint remoteEndPoint, byte[] data) = this.received.Dequeue();
            if (data.Length > buffer.Length)
            {
                throw new InvalidOperationException(
                    $"WebSocket packet exceeds receive buffer: {data.Length} > {buffer.Length}");
            }

            Buffer.BlockCopy(data, 0, buffer, 0, data.Length);
            endPoint = remoteEndPoint;
            return data.Length;
        }

        public int Available()
        {
            return this.received.Count;
        }

        public void Update()
        {
        }

        public void OnError(long id, int error)
        {
        }

        public void Dispose()
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            foreach (SocketState state in this.sockets.Values)
            {
                MiniGameWebSocketBridge.Dispose(state.Id);
            }
            this.sockets.Clear();
            this.received.Clear();
        }

        private SocketState GetOrCreate(IPEndPoint endPoint)
        {
            string key = endPoint.ToString();
            if (this.sockets.TryGetValue(key, out SocketState state))
            {
                return state;
            }

            IPEndPoint remoteEndPoint = new(endPoint.Address, endPoint.Port);
            state = new SocketState { EndPoint = remoteEndPoint };
            state.Id = MiniGameWebSocketBridge.Create(
                MiniGameNetworkSettings.GetRouterWebSocketUrl(remoteEndPoint),
                () => this.OnOpen(state),
                data => this.OnMessage(state, data),
                error => this.OnSocketError(state, error),
                (code, reason) => this.OnSocketClose(state, code, reason));
            this.sockets.Add(key, state);
            return state;
        }

        private void OnOpen(SocketState state)
        {
            state.IsOpen = true;
            while (state.Pending.Count > 0)
            {
                this.SendPacket(state, state.Pending.Dequeue());
            }
        }

        private void OnMessage(SocketState state, byte[] data)
        {
            if (!this.disposed)
            {
                this.received.Enqueue((state.EndPoint, data));
            }
        }

        private void OnSocketError(SocketState state, string error)
        {
            Log.Error($"MiniGame WebSocket error: {state.EndPoint} {error}");
            this.RemoveSocket(state, false);
        }

        private void OnSocketClose(SocketState state, int code, string reason)
        {
            Log.Warning($"MiniGame WebSocket closed: {state.EndPoint} code={code} reason={reason}");
            this.RemoveSocket(state, true);
        }

        private void SendPacket(SocketState state, byte[] packet)
        {
            int result = MiniGameWebSocketBridge.Send(state.Id, packet);
            if (result != 0)
            {
                this.OnSocketError(state, $"send failed, code={result}");
            }
        }

        private void RemoveSocket(SocketState state, bool alreadyClosed)
        {
            if (state.Id == 0)
            {
                return;
            }

            string key = state.EndPoint.ToString();
            if (this.sockets.TryGetValue(key, out SocketState current) && ReferenceEquals(current, state))
            {
                this.sockets.Remove(key);
            }

            int socketId = state.Id;
            state.Id = 0;
            state.IsOpen = false;
            state.Pending.Clear();
            if (alreadyClosed)
            {
                MiniGameWebSocketBridge.ReleaseClosed(socketId);
            }
            else
            {
                MiniGameWebSocketBridge.Dispose(socketId);
            }
        }
    }
}
#endif
