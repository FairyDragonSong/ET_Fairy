namespace ET.Server
{
    /// <summary>
    /// 应用层 RPC 失败类别。它将 ET 底层错误码和异常归纳为便于业务判断的几种情况。
    /// 具体 ET 错误码仍保存在 RpcCallResult.Error 中。
    /// </summary>
    public enum RpcFailureKind
    {
        /// <summary>调用成功，没有失败。</summary>
        None,
        /// <summary>传入了默认 ActorId、0 实体 ID 或越界的 LocationType。</summary>
        InvalidAddress,
        /// <summary>Location 中不存在目标，或者目标 Actor 已被销毁。</summary>
        TargetNotFound,
        /// <summary>RPC 在 ET 底层规定的时间内没有收到响应。</summary>
        Timeout,
        /// <summary>远端 Handler 正常返回了非 0 错误码。</summary>
        RemoteError,
        /// <summary>实际响应类型与 CallService/CallEntity 指定的泛型响应类型不一致。</summary>
        ResponseTypeMismatch,
        /// <summary>组件缺失、路由异常等非远端业务错误。</summary>
        InfrastructureError,
    }

    /// <summary>
    /// 应用层 RPC 调用结果。
    /// 将响应、错误分类、ET 错误码和耗时集中返回，使正常的远端失败不再要求业务代码捕获 RpcException。
    /// 成功响应通常是池化 MessageObject，使用完毕后仍应按照 ET 规则调用 Dispose 或使用 using。
    /// </summary>
    /// <typeparam name="TResponse">该请求期望收到的具体响应类型。</typeparam>
    public readonly struct RpcCallResult<TResponse> where TResponse : class, IResponse
    {
        /// <summary>响应错误码为 0 且类型正确时为 true。</summary>
        public bool IsSuccess { get; }

        /// <summary>
        /// 具体响应对象。成功时一定存在；远端返回业务错误时也可能存在。
        /// 地址、超时或基础设施失败时通常为 null。
        /// </summary>
        public TResponse Response { get; }

        /// <summary>归一化后的失败类别，成功时为 None。</summary>
        public RpcFailureKind FailureKind { get; }

        /// <summary>ET 原始错误码。基础设施异常无法取得错误码时为 0。</summary>
        public int Error { get; }

        /// <summary>远端响应消息或本地捕获到的异常信息。</summary>
        public string Message { get; }

        /// <summary>从发起调用到得到结果所经过的毫秒数。</summary>
        public long ElapsedMilliseconds { get; }

        private RpcCallResult(
            bool isSuccess,
            TResponse response,
            RpcFailureKind failureKind,
            int error,
            string message,
            long elapsedMilliseconds)
        {
            this.IsSuccess = isSuccess;
            this.Response = response;
            this.FailureKind = failureKind;
            this.Error = error;
            this.Message = message;
            this.ElapsedMilliseconds = elapsedMilliseconds;
        }

        /// <summary>
        /// 创建成功结果。主要供 ApplicationRpc 内部将 ET 响应转换为应用层结果。
        /// </summary>
        public static RpcCallResult<TResponse> Success(TResponse response, long elapsedMilliseconds)
        {
            return new RpcCallResult<TResponse>(true, response, RpcFailureKind.None, response.Error, response.Message, elapsedMilliseconds);
        }

        /// <summary>
        /// 创建失败结果。远端已经返回响应但 Error 非 0 时，可通过 response 保留原始响应。
        /// </summary>
        public static RpcCallResult<TResponse> Failure(
            RpcFailureKind failureKind,
            int error,
            string message,
            long elapsedMilliseconds,
            TResponse response = null)
        {
            return new RpcCallResult<TResponse>(false, response, failureKind, error, message, elapsedMilliseconds);
        }

        /// <summary>
        /// 兼容 ET 原有异常式 Call 用法的渐进迁移辅助方法。
        /// 成功时返回响应；需要抛出的 ET RPC 错误会重新抛出 RpcException。
        /// 新业务代码建议优先检查 IsSuccess 和 FailureKind，以明确处理远端失败。
        /// </summary>
        public TResponse ResponseOrThrow()
        {
            if (this.IsSuccess || (this.Response != null && !ErrorCore.IsRpcNeedThrowException(this.Error)))
            {
                return this.Response;
            }

            throw new RpcException(this.Error, this.Message ?? this.FailureKind.ToString());
        }
    }
}
