namespace ET
{
    /// <summary>
    /// 本质上 [Code] 标记的类都是"二级扫描器"——它们自身被 CreateCode() 自动创建后，又会在各自的 Awake() 中继续扫描 CodeTypes 里的其他特性标记类型来构建子系统。
    ///整个启动链路：
    ///CodeLoader.Awake()
    ///→ CodeTypes.Awake(assemblies[])     // 扫描所有程序集，按特性分类存储类型
    ///→ CodeTypes.CreateCode()            // 找到所有 [Code] 类，实例化并 Awake
    ///        → EntitySystemSingleton.Awake() // 扫描 [EntitySystem] 类，注册组件系统
    ///        → EventSystem.Awake()           // 扫描 [Event] 类，注册事件处理器
    ///        → MessageDispatcher.Awake()     // 扫描消息Handler，注册消息分发
    ///这样设计的好处是：热重载时只需重新调用 CreateCode()，所有子系统就会基于新的 Hotfix.dll 重建，无需手动逐个重新注册。
    /// </summary>
    public class CodeAttribute: BaseAttribute
    {

    }
}