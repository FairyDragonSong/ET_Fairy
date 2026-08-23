using System;
using System.Linq;

namespace ET.Server
{
    [EntitySystemOf(typeof(GracefulShutdownComponent))]
    [FriendOf(typeof(GracefulShutdownComponent))]
    public static partial class GracefulShutdownComponentSystem
    {
        [EntitySystem]
        private static void Awake(this GracefulShutdownComponent self)
        {
            ShutdownCoordinator.Instance.RegisterParticipant();
        }

        [EntitySystem]
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
        private static void Destroy(this GracefulShutdownComponent self)
        {
            if (!self.Reported)
            {
                self.Reported = true;
                ShutdownCoordinator.Instance?.CompleteParticipant(false);
            }
        }

        private static async ETTask Drain(this GracefulShutdownComponent self)
        {
            bool success = true;
            Scene root = self.Root();
            try
            {
                root.RemoveComponent<NetComponent>();
                UnitComponent unitComponent = root.GetComponent<UnitComponent>();
                if (unitComponent != null)
                {
                    Unit[] units = unitComponent.Children.Values.OfType<Unit>().ToArray();
                    foreach (Unit unit in units)
                    {
                        RoleSaveComponent roleSaveComponent = unit.GetComponent<RoleSaveComponent>();
                        if (roleSaveComponent == null)
                        {
                            continue;
                        }

                        bool saved = false;
                        for (int retry = 0; retry < 3 && !saved; ++retry)
                        {
                            saved = await roleSaveComponent.SaveNow();
                            if (!saved)
                            {
                                await root.GetComponent<TimerComponent>().WaitAsync(200);
                            }
                        }
                        success &= saved;
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
    }
}
