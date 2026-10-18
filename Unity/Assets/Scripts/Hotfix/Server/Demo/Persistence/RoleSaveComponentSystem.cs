using System;

namespace ET.Server
{
    [EntitySystemOf(typeof(RoleSaveComponent))]
    [FriendOf(typeof(RoleSaveComponent))]
    public static partial class RoleSaveComponentSystem
    {
        private const int SaveInterval = 60_000;

        [Invoke(TimerInvokeType.RolePeriodicSave)]
        public class RolePeriodicSaveTimer: ATimer<RoleSaveComponent>
        {
            // 执行当前处理器的主流程。
            protected override void Run(RoleSaveComponent self)
            {
                self.SaveNow().Coroutine();
            }
        }

        [EntitySystem]
        // 初始化当前组件的运行状态。
        private static void Awake(this RoleSaveComponent self)
        {
            self.Timer = self.Root().GetComponent<TimerComponent>().NewRepeatedTimer(
                SaveInterval,
                TimerInvokeType.RolePeriodicSave,
                self);
        }

        [EntitySystem]
        // 释放当前组件持有的状态与未完成工作。
        private static void Destroy(this RoleSaveComponent self)
        {
            self.Root().GetComponent<TimerComponent>()?.Remove(ref self.Timer);
        }

        // 立即保存当前角色状态，并报告是否成功。
        public static async ETTask<bool> SaveNow(this RoleSaveComponent self)
        {
            if (self.Saving || self.IsDisposed)
            {
                return false;
            }

            self.Saving = true;
            long roleId = self.GetParent<Unit>()?.Id ?? 0;
            try
            {
                Unit unit = self.GetParent<Unit>();
                bool success = await RoleRepository.SaveProgress(unit.Root(), unit.Id, unit.Scene().Name, unit.Position);
                OperationalMetrics.Instance.RecordRoleSave(success);
                return success;
            }
            catch (Exception e)
            {
                Log.Error($"role periodic save error: {roleId}\n{e}");
                return false;
            }
            finally
            {
                if (!self.IsDisposed)
                {
                    self.Saving = false;
                }
            }
        }
    }
}
