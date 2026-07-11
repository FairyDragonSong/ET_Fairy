using System;

namespace ET
{
    /// <summary>
    /// 每个Config单例的基类
    /// 用于生命周期管理
    /// </summary>
    public interface IConfig
    {
        /// <summary>
        ///Model中的所有配置初始化完毕后的调用
        ///Ref的初始化
        ///具体做了什么：
        ///假设你的 Excel 配置表中有这样的关系：
        ///怪物表(UnitConfig)    技能表(SkillConfig)
        ///SkillId = 1001 (只存了ID) Id = 1001, Name = "火球术"
        ///ResolveRef() 的作用就是把这种"只存了 ID"的引用，替换成对实际配置对象的直接引用指针。这样运行时访问 unit.Skill 就能直接拿到 SkillConfig 对象，不用每次都去查表。
        ///执行时序很重要：
        ///1. 所有配置表先各自反序列化（LoadOneConfig / LoadOneInThread）
        ///2. 全部加载完毕后，统一调用 ResolveRef()
        ///必须等所有表都加载完才能解析引用——因为 A 表可能引用 B 表的数据，如果 B 还没加载就会找不到。
        ///当前项目中像 StartSceneConfig.ResolveRef() 是空方法（没有跨表引用），而有跨表引用的配置表，Luban 会自动生成查找并赋值的代码。
        /// </summary>
        void ResolveRef();
    }

    public interface IConfigSystem: ISystemType
    {
        //所有配置初始化完毕后的调用
        //解决需要在Hotfix中初始化其他数据时使用
        void Initialized(IConfig data);
    }

    [EntitySystem]
    public abstract class ConfigSystem<T>: SystemObject, IConfigSystem where T : IConfig
    {
        Type ISystemType.Type()
        {
            return typeof (T);
        }

        Type ISystemType.SystemType()
        {
            return typeof (IConfigSystem);
        }

        int ISystemType.GetInstanceQueueIndex()
        {
            return InstanceQueueIndex.None;
        }

        void IConfigSystem.Initialized(IConfig data)
        {
            this.Initialized((T)data);
        }

        protected abstract void Initialized(T self);
    }
}