using System.Net;

namespace ET.Server
{
    [Invoke((long)SceneType.RouterManager)]
    [FriendOf(typeof(HttpComponent))]
    public class FiberInit_RouterManager: AInvokeHandler<FiberInit, ETTask>
    {
        public override async ETTask Handle(FiberInit fiberInit)
        {
            Scene root = fiberInit.Fiber.Root;
            StartSceneConfig startSceneConfig = StartSceneConfigCategory.Instance.Get((int)root.Id);
            root.AddComponent<GracefulShutdownComponent>();
            HttpComponent http=root.AddComponent<HttpComponent, string>(NetworkExposurePolicy.ResolveRouterManagerPrefix(Options.Instance,startSceneConfig.Port));
            if(http.Listener==null||!http.Listener.IsListening)throw new System.InvalidOperationException("RouterManager HTTP listener failed to start");

            await ETTask.CompletedTask;
        }
    }
}
