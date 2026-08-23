using System;
using System.Collections.Generic;
using System.Net;

namespace ET.Server
{
    [EntitySystemOf(typeof(HttpComponent))]
    [FriendOf(typeof(HttpComponent))]
    public static partial class HttpComponentSystem
    {
        [EntitySystem]
        private static void Awake(this HttpComponent self, string address)
        {
            try
            {
                self.Listener = new HttpListener();

                foreach (string s in address.Split(';'))
                {
                    if (s.Trim() == "")
                    {
                        continue;
                    }
                    self.Listener.Prefixes.Add(s);
                }

                self.Listener.Start();

                self.Accept().Coroutine();
            }
            catch (HttpListenerException e)
            {
                throw new Exception($"请先在cmd中运行: netsh http add urlacl url=http://*:你的address中的端口/ user=Everyone, address: {address}", e);
            }
        }
        
        [EntitySystem]
        private static void Destroy(this HttpComponent self)
        {
            self.Listener.Stop();
            self.Listener.Close();
        }

        private static async ETTask Accept(this HttpComponent self)
        {
            long instanceId = self.InstanceId;
            while (self.InstanceId == instanceId)
            {
                try
                {
                    HttpListenerContext context = await self.Listener.GetContextAsync();
                    self.Handle(context).Coroutine();
                }
                catch (ObjectDisposedException)
                {
                }
                catch (Exception e)
                {
                    Log.Error(e);
                }
            }
        }

        private static async ETTask Handle(this HttpComponent self, HttpListenerContext context)
        {
            string requestId = Guid.NewGuid().ToString("N");
            context.Response.Headers["X-Request-Id"] = requestId;
            bool error = false;
            try
            {
                IHttpHandler handler = HttpDispatcher.Instance.Get(self.IScene.SceneType, context.Request.Url.AbsolutePath);
                await handler.Handle(self.Scene(), context);
            }
            catch (KeyNotFoundException)
            {
                error = true;
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                HttpHelper.TextResponse(context, "not found\n", "text/plain; charset=utf-8");
            }
            catch (Exception e)
            {
                error = true;
                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                Log.Error($"event=http_request_failed request_id={requestId} method={context.Request.HttpMethod} " +
                          $"path={context.Request.Url.AbsolutePath}\n{e}");
            }
            OperationalMetrics.Instance.RecordHttp(error || context.Response.StatusCode >= 500);
            context.Request.InputStream.Dispose();
            context.Response.OutputStream.Dispose();
        }
    }
}
