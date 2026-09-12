using System;

namespace ET
{
    [EntitySystemOf(typeof(CoroutineLock))]
    public static partial class CoroutineLockSystem
    {
        [EntitySystem]
        private static void Awake(this CoroutineLock self, int type, long k, int count)
        {
            self.type = type;
            self.key = k;
            self.level = count;
        }
        
        [EntitySystem]
        private static void Destroy(this CoroutineLock self)
        {
            // Do not wake a future queue with the same key when this queue is being destroyed.
            if (self.Parent != null && !self.Parent.IsDisposed)
            {
                self.Scene<CoroutineLockComponent>().RunNextCoroutine(self.type, self.key, self.level + 1);
            }
            self.type = CoroutineLockType.None;
            self.key = 0;
            self.level = 0;
        }
    }
    
    [ChildOf(typeof(CoroutineLockQueue))]
    public class CoroutineLock: Entity, IAwake<int, long, int>, IDestroy
    {
        public int type;
        public long key;
        public int level;
    }
}
