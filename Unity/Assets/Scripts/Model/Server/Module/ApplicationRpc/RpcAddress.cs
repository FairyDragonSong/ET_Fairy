namespace ET.Server
{
    /// <summary>
    /// 固定服务地址。
    /// 用于目标 ActorId 已知且不会发生位置迁移的服务间通信，例如 Gate 调用 Match、Map 或 RoomRoot。
    /// 应配合 ApplicationRpc.CallService 或 ApplicationRpc.NotifyService 使用。
    /// </summary>
    public readonly struct ServiceAddress
    {
        /// <summary>
        /// ET 底层用于定位进程、Fiber 和 Actor 实例的完整地址。
        /// 应用层通常不需要拆解它，只需通过 From 创建 ServiceAddress。
        /// </summary>
        public ActorId ActorId { get; }

        private ServiceAddress(ActorId actorId)
        {
            this.ActorId = actorId;
        }

        /// <summary>
        /// 将已有 ActorId 包装为固定服务地址。
        /// 该方法不查询 Location，也不会验证目标 Actor 当前是否存在。
        /// </summary>
        /// <param name="actorId">目标服务或固定 Actor 的地址。</param>
        /// <returns>供应用层服务通信接口使用的地址。</returns>
        public static ServiceAddress From(ActorId actorId)
        {
            return new ServiceAddress(actorId);
        }

        /// <summary>
        /// 地址是否包含有效的非默认 ActorId。
        /// </summary>
        public bool IsValid => this.ActorId != default;

        public override string ToString()
        {
            return this.ActorId.ToString();
        }
    }

    /// <summary>
    /// 可迁移实体地址。
    /// 使用稳定的 LocationType 和 EntityId 表示业务实体，不要求调用方知道实体当前所在的进程和 Fiber。
    /// 适用于 Unit、Player、GateSession 等已注册到 Location 服务的实体。
    /// </summary>
    public readonly struct EntityAddress
    {
        /// <summary>
        /// Location 表类型，用于区分 Unit、Player、GateSession 等不同的地址空间。
        /// </summary>
        public int LocationType { get; }

        /// <summary>
        /// 实体在对应 Location 表中的稳定业务 ID。
        /// </summary>
        public long EntityId { get; }

        private EntityAddress(int locationType, long entityId)
        {
            this.LocationType = locationType;
            this.EntityId = entityId;
        }

        /// <summary>
        /// 根据 Location 类型和实体 ID 创建可迁移实体地址。
        /// 创建地址不会立即访问 Location 服务，实际查询发生在 CallEntity 或 NotifyEntity 中。
        /// </summary>
        /// <param name="locationType">LocationType 中定义的实体类型。</param>
        /// <param name="entityId">实体的稳定业务 ID，不能为 0。</param>
        /// <returns>供应用层实体通信接口使用的地址。</returns>
        public static EntityAddress From(int locationType, long entityId)
        {
            return new EntityAddress(locationType, entityId);
        }

        /// <summary>
        /// Location 类型在有效范围内且实体 ID 非 0 时返回 true。
        /// </summary>
        public bool IsValid => this.LocationType >= 0 && this.LocationType < ET.Server.LocationType.Max && this.EntityId != 0;

        public override string ToString()
        {
            return $"LocationType={this.LocationType}, EntityId={this.EntityId}";
        }
    }
}
