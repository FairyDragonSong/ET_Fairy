using System;
using System.Net;

namespace ET.Server
{
    [HttpHandler(SceneType.RouterManager, "/health/live")]
    public class HttpLivenessHandler: IHttpHandler
    {
        // 返回进程存活状态；该检查不依赖下游服务。
        public async ETTask Handle(Scene scene, HttpListenerContext context)
        {
            string response = $"{{\"status\":\"live\",\"utc\":\"{DateTime.UtcNow:O}\",\"process\":{Options.Instance.Process}}}";
            HttpHelper.TextResponse(context, response, "application/json; charset=utf-8");
            await ETTask.CompletedTask;
        }
    }

    [HttpHandler(SceneType.RouterManager, "/health/ready")]
    public class HttpReadinessHandler: IHttpHandler
    {
        // 仅在启动完成、未停机且核心拓扑存在时报告就绪。
        public async ETTask Handle(Scene scene, HttpListenerContext context)
        {
            bool ready = StartupCoordinator.Instance.IsReady &&
                    !ShutdownCoordinator.Instance.IsRequested &&
                    StartSceneConfigCategory.Instance.Realms.Count > 0 &&
                    StartSceneConfigCategory.Instance.Routers.Count > 0;
            context.Response.StatusCode = ready ? (int)HttpStatusCode.OK : (int)HttpStatusCode.ServiceUnavailable;
            string status = ready ? "ready" : "not_ready";
            string response = $"{{\"status\":\"{status}\",\"realms\":{StartSceneConfigCategory.Instance.Realms.Count}," +
                    $"\"routers\":{StartSceneConfigCategory.Instance.Routers.Count}}}";
            HttpHelper.TextResponse(context, response, "application/json; charset=utf-8");
            await ETTask.CompletedTask;
        }
    }

    [HttpHandler(SceneType.RouterManager, "/metrics")]
    public class HttpMetricsHandler: IHttpHandler
    {
        // 只向回环地址暴露 Prometheus 指标，避免公网监听器泄露运行数据。
        public async ETTask Handle(Scene scene, HttpListenerContext context)
        {
            // RouterManager also serves public routing endpoints; never expose metrics through that wildcard listener.
            if(context.Request.RemoteEndPoint==null||!IPAddress.IsLoopback(context.Request.RemoteEndPoint.Address))
            {
                context.Response.StatusCode=(int)HttpStatusCode.Forbidden;
                HttpHelper.TextResponse(context,"forbidden","text/plain");
                return;
            }
            HttpHelper.TextResponse(context, OperationalMetrics.Instance.ExportPrometheus(), "text/plain; version=0.0.4; charset=utf-8");
            await ETTask.CompletedTask;
        }
    }
}
