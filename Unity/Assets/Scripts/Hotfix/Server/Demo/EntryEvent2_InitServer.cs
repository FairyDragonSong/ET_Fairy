using System;
using System.Net;

namespace ET.Server
{
    [Event(SceneType.Main)]
    public class EntryEvent2_InitServer: AEvent<Scene, EntryEvent2>
    {
        protected override async ETTask Run(Scene root, EntryEvent2 args)
        {
            switch (Options.Instance.AppType)
            {
                case AppType.Server:
                {
                    DistributedTopologyPolicy.Validate(Options.Instance, StartSceneConfigCategory.Instance.DataList);
                    int process = root.Fiber.Process;
                    StartProcessConfig startProcessConfig = StartProcessConfigCategory.Instance.Get(process);
                    if (startProcessConfig.Port != 0)
                    {
                        await CreateRequiredFiber(ConstFiberId.NetInner, 0, SceneType.NetInner, "NetInner");
                    }

                    // 根据配置创建纤程
                    var processScenes = StartSceneConfigCategory.Instance.GetByProcess(process);
                    foreach (StartSceneConfig startConfig in processScenes)
                    {
                        await CreateRequiredFiber(startConfig.Id, startConfig.Zone, startConfig.Type, startConfig.Name);
                    }

                    break;
                }
                case AppType.Watcher:
                {
                    root.AddComponent<WatcherComponent>();
                    break;
                }
                case AppType.GameTool:
                {
                    break;
                }
            }

            if (Options.Instance.Console == 1)
            {
                root.AddComponent<ConsoleComponent>();
            }
        }

        private static async ETTask CreateRequiredFiber(int fiberId, int zone, SceneType sceneType, string name)
        {
            try
            {
                await FiberManager.Instance.Create(SchedulerType.ThreadPool, fiberId, zone, sceneType, name);
            }
            catch
            {
                StartupCoordinator.Instance.MarkFailed();
                throw;
            }
        }
    }
}
