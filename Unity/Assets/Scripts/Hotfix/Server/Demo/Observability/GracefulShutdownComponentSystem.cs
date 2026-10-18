using System;
using System.Collections.Generic;
using System.Linq;

namespace ET.Server
{
    [EntitySystemOf(typeof(GracefulShutdownComponent))]
    [FriendOf(typeof(GracefulShutdownComponent))]
    public static partial class GracefulShutdownComponentSystem
    {
        [EntitySystem]
        // 初始化当前组件的运行状态。
        private static void Awake(this GracefulShutdownComponent self)
        {
            ShutdownCoordinator.Instance.RegisterParticipant();
        }

        [EntitySystem]
        // 发现停机请求后只启动一次异步排空流程。
        private static void Update(this GracefulShutdownComponent self)
        {
            if (!ShutdownCoordinator.Instance.IsRequested || self.Started)
            {
                return;
            }
            self.Started = true;
            self.Drain().Coroutine();
        }

        [EntitySystem]
        // 释放当前组件持有的状态与未完成工作。
        private static void Destroy(this GracefulShutdownComponent self)
        {
            if (!self.Reported)
            {
                self.Reported = true;
                ShutdownCoordinator.Instance?.CompleteParticipant(false);
            }
        }

        // 等待所有在途会话日志写入完成。
        private static async ETTask Drain(this GracefulShutdownComponent self)
        {
            bool success = true;
            Scene root = self.Root();
            try
            {
                root.RemoveComponent<NetComponent>();
#if DOTNET
                success &= await AccountSessionJournal.Drain(root);
#endif
                UnitComponent unitComponent = root.GetComponent<UnitComponent>();
                if (unitComponent != null)
                {
                    Unit[] units = unitComponent.Children.Values.OfType<Unit>().ToArray();
                    // Bound database pressure per scene; never launch one task per online role.
                    const int batchSize = 16;
                    for (int offset = 0; offset < units.Length; offset += batchSize)
                    {
                        int count = Math.Min(batchSize, units.Length - offset);
                        bool[] results = new bool[count];
                        List<ETTask> tasks = new List<ETTask>(count);
                        for (int index = 0; index < count; ++index)
                        {
                            tasks.Add(SaveForShutdown(root, units[offset + index], results, index));
                        }
                        await ETTaskHelper.WaitAll(tasks);
                        foreach (bool saved in results)
                        {
                            success &= saved;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                success = false;
                Log.Error($"event=graceful_shutdown_failed scene={root.Name} fiber={root.Fiber.Id}\n{e}");
            }
            finally
            {
                if (!self.IsDisposed && !self.Reported)
                {
                    self.Reported = true;
                    ShutdownCoordinator.Instance.CompleteParticipant(success);
                    Log.Info($"event=graceful_shutdown_completed scene={root.Name} fiber={root.Fiber.Id} success={success}");
                }
            }
        }

        // 在停机阶段保存单个角色并记录结果。
        private static async ETTask SaveForShutdown(Scene root, Unit unit, bool[] results, int index)
        {
            // WaitAll does not propagate child exceptions: record failures inside each task.
            try
            {
                RoleSaveComponent saver = unit.GetComponent<RoleSaveComponent>();
                if (saver == null)
                {
                    results[index] = true;
                    return;
                }
                for (int retry = 0; retry < 3; ++retry)
                {
                    if (await saver.SaveNow())
                    {
                        results[index] = true;
                        return;
                    }
                    if (retry < 2)
                    {
                        await root.GetComponent<TimerComponent>().WaitAsync(200);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error($"event=graceful_shutdown_role_failed scene={root.Name}\n{e}");
            }
        }
    }
}
