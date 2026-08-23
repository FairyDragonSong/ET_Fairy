using System;
using System.Net;

namespace ET.Server
{
    [HttpHandler(SceneType.RouterManager, "/health/live")]
    public class HttpLivenessHandler: IHttpHandler
    {
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
        public async ETTask Handle(Scene scene, HttpListenerContext context)
        {
            bool ready = StartSceneConfigCategory.Instance.Realms.Count > 0 &&
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
        public async ETTask Handle(Scene scene, HttpListenerContext context)
        {
            HttpHelper.TextResponse(context, OperationalMetrics.Instance.ExportPrometheus(), "text/plain; version=0.0.4; charset=utf-8");
            await ETTask.CompletedTask;
        }
    }
}
