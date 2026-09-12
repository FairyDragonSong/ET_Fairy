using System;
using System.Collections.Generic;

namespace ET
{
    [EntitySystemOf(typeof(CoroutineLockComponent))]
    public static partial class CoroutineLockComponentSystem
    {
        [EntitySystem]
        public static void Awake(this CoroutineLockComponent self)
        {
        }
        
        [EntitySystem]
        public static void Update(this CoroutineLockComponent self)
        {
            // 循环过程中会有对象继续加入队列
            while (self.nextFrameRun.Count > 0)
            {
                (int coroutineLockType, long key, int count, long queueInstanceId) = self.nextFrameRun.Dequeue();
                self.Notify(coroutineLockType, key, count, queueInstanceId);
            }
        }

        public static void RunNextCoroutine(this CoroutineLockComponent self, int coroutineLockType, long key, int level)
        {
            // 一个协程队列一帧处理超过100个,说明比较多了,打个warning,检查一下是否够正常
            if (level == 100)
            {
                Log.Warning($"too much coroutine level: {coroutineLockType} {key}");
            }

            if (self.IsDisposed) return;
            var queue = self.GetChild<CoroutineLockQueueType>(coroutineLockType)?.Get(key);
            if (queue == null || queue.IsDisposed) return;
            self.nextFrameRun.Enqueue((coroutineLockType, key, level, queue.InstanceId));
        }

        public static async ETTask<CoroutineLock> Wait(this CoroutineLockComponent self, int coroutineLockType, long key, int time = 60000)
        {
            if (self.IsDisposed) throw new ObjectDisposedException(nameof(CoroutineLockComponent));
            CoroutineLockQueueType coroutineLockQueueType = self.GetChild<CoroutineLockQueueType>(coroutineLockType) ?? self.AddChildWithId<CoroutineLockQueueType>(coroutineLockType);
            return await coroutineLockQueueType.Wait(key, time);
        }

        private static void Notify(this CoroutineLockComponent self, int coroutineLockType, long key, int level, long queueInstanceId)
        {
            CoroutineLockQueueType coroutineLockQueueType = self.GetChild<CoroutineLockQueueType>(coroutineLockType);
            if (coroutineLockQueueType == null)
            {
                return;
            }
            var queue = coroutineLockQueueType.Get(key);
            if (queue == null || queue.IsDisposed || queue.InstanceId != queueInstanceId) return;
            coroutineLockQueueType.Notify(key, level);
        }
    }
    
    [ComponentOf(typeof(Scene))]
    public class CoroutineLockComponent: Entity, IAwake, IScene, IUpdate
    {
        public Fiber Fiber { get; set; }
        public SceneType SceneType { get; set; }
        
        public readonly Queue<(int, long, int, long)> nextFrameRun = new();
    }
}
