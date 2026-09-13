using System;

namespace ET.Server
{
    /// <summary>
    /// 面向业务层的统一通信门面。
    /// 业务代码只需判断目标是“固定服务”还是“可迁移实体”，不再直接选择 MessageSender、
    /// ProcessInnerSender、ProcessOuterSender 或 MessageLocationSenderComponent。
    /// 本类只包装调用语义、结果和错误分类，不改变 ET 原有的序列化、路由、Mailbox 与 Location 机制。
    /// </summary>
    public static class ApplicationRpc
    {
        /// <summary>
        /// 向 ActorId 已知且位置固定的服务发送单向消息，不等待响应。
        /// 底层使用 MessageSender.Send，并自动选择同进程 Fiber 队列或跨进程网络通道。
        /// 适用于帧消息、状态通知等不需要确认结果的通信。
        /// </summary>
        /// <param name="root">当前业务 Scene。</param>
        /// <param name="target">通过 ServiceAddress.From 创建的固定服务地址。</param>
        /// <param name="message">要发送的普通 ET 消息。</param>
        /// <exception cref="ArgumentException">目标地址是默认 ActorId 时抛出。</exception>
        public static void NotifyService(this Scene root, ServiceAddress target, IMessage message)
        {
            if (!target.IsValid)
            {
                throw new ArgumentException("Service address is invalid", nameof(target));
            }

            root.GetComponent<MessageSender>().Send(target.ActorId, message);
        }

        /// <summary>
        /// 向 ActorId 已知且位置固定的服务发起 RPC，并返回指定类型的统一调用结果。
        /// 底层使用 MessageSender.Call；同进程和跨进程路由由 ET 自动决定。
        /// 调用失败通常体现在返回值中，不要求调用方捕获 RpcException。
        /// </summary>
        /// <typeparam name="TResponse">请求对应的具体响应类型。</typeparam>
        /// <param name="root">当前业务 Scene。</param>
        /// <param name="target">固定服务地址。</param>
        /// <param name="request">实现 IRequest 的请求消息。</param>
        /// <returns>包含响应、失败类别、ET 错误码和耗时的调用结果。</returns>
        public static async ETTask<RpcCallResult<TResponse>> CallService<TResponse>(
            this Scene root,
            ServiceAddress target,
            IRequest request) where TResponse : class, IResponse
        {
            long startTime = TimeInfo.Instance.ServerNow();
            if (!target.IsValid)
            {
                return Failure<TResponse>(RpcFailureKind.InvalidAddress, 0, "Service address is invalid", startTime);
            }

            try
            {
                IResponse response = await root.GetComponent<MessageSender>().Call(target.ActorId, request, needException: false);
                return ToResult<TResponse>(response, startTime);
            }
            catch (RpcException e)
            {
                return Failure<TResponse>(Classify(e.Error), e.Error, e.Message, startTime);
            }
            catch (Exception e)
            {
                return Failure<TResponse>(RpcFailureKind.InfrastructureError, 0, e.ToString(), startTime);
            }
        }

        public static async ETTask<RpcCallResult<TResponse>> CallServiceWithTimeout<TResponse>(
            this Scene root,
            ServiceAddress target,
            IRequest request,
            long timeoutMilliseconds) where TResponse : class, IResponse
        {
            if (timeoutMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
            ETTask<RpcCallResult<TResponse>> completion = ETTask<RpcCallResult<TResponse>>.Create(true);
            bool settled = false;
            long started = TimeInfo.Instance.ServerNow();

            async ETTask Invoke()
            {
                RpcCallResult<TResponse> result = await root.CallService<TResponse>(target, request);
                if (settled)
                {
                    if (result.Response is IDisposable disposable) disposable.Dispose();
                    return;
                }
                settled = true;
                completion.SetResult(result);
            }

            async ETTask Timeout()
            {
                await root.GetComponent<TimerComponent>().WaitAsync(timeoutMilliseconds);
                if (settled) return;
                settled = true;
                completion.SetResult(RpcCallResult<TResponse>.Failure(
                    RpcFailureKind.Timeout,
                    ErrorCore.ERR_Timeout,
                    "Service RPC exceeded its bounded caller timeout",
                    TimeInfo.Instance.ServerNow() - started));
            }

            Invoke().Coroutine();
            Timeout().Coroutine();
            return await completion;
        }

        /// <summary>
        /// 向通过 Location 定位的实体发送 ILocationMessage 单向消息。
        /// 适用于 Unit 等可能在进程或 Fiber 之间迁移的实体；底层会查询并缓存其当前 ActorId，
        /// 找不到 Actor 时遵循 ET Location 消息原有的重试与错误处理规则。
        /// </summary>
        /// <param name="root">当前业务 Scene。</param>
        /// <param name="target">由 LocationType 和实体 ID 构成的地址。</param>
        /// <param name="message">实现 ILocationMessage 的消息。</param>
        public static void NotifyEntity(this Scene root, EntityAddress target, ILocationMessage message)
        {
            if (!target.IsValid)
            {
                throw new ArgumentException("Entity address is invalid", nameof(target));
            }

            root.GetComponent<MessageLocationSenderComponent>()
                .Get(target.LocationType)
                .Send(target.EntityId, message);
        }

        /// <summary>
        /// 通过 Location 中的稳定实体身份发送普通 IMessage。
        /// 典型用途是 Map 或 Room 通过 GateSession Location 向客户端发送消息。
        /// 与 ILocationMessage 重载不同，此重载采用 ET 的普通消息快速路径，不执行 Actor 不存在后的迁移重试；
        /// 调用方应确保目标已经注册到对应 Location 表。
        /// </summary>
        public static void NotifyEntity(this Scene root, EntityAddress target, IMessage message)
        {
            if (!target.IsValid)
            {
                throw new ArgumentException("Entity address is invalid", nameof(target));
            }

            root.GetComponent<MessageLocationSenderComponent>()
                .Get(target.LocationType)
                .Send(target.EntityId, message);
        }

        /// <summary>
        /// 向可迁移实体发起无法在编译期确定响应类型的 Location RPC。
        /// 主要用于 Gate 转发客户端 ILocationRequest 等动态协议分发场景。
        /// 普通业务代码如果知道响应类型，应优先使用泛型 CallEntity&lt;TResponse&gt;。
        /// </summary>
        /// <returns>响应以 IResponse 暴露的统一调用结果。</returns>
        public static ETTask<RpcCallResult<IResponse>> CallEntity(
            this Scene root,
            EntityAddress target,
            ILocationRequest request)
        {
            return root.CallEntity<IResponse>(target, request);
        }

        /// <summary>
        /// 向通过 Location 定位的可迁移实体发起强类型 RPC。
        /// 底层使用 MessageLocationSenderComponent：先按 LocationType 和 EntityId 获取当前 ActorId，
        /// 再通过 MessageSender 调用；实体迁移导致旧 ActorId 失效时沿用 ET 原有刷新与重试逻辑。
        /// </summary>
        /// <typeparam name="TResponse">请求对应的具体响应类型。</typeparam>
        /// <param name="root">当前业务 Scene。</param>
        /// <param name="target">可迁移实体地址。</param>
        /// <param name="request">实现 ILocationRequest 的请求消息。</param>
        /// <returns>包含响应、失败类别、ET 错误码和耗时的调用结果。</returns>
        public static async ETTask<RpcCallResult<TResponse>> CallEntity<TResponse>(
            this Scene root,
            EntityAddress target,
            ILocationRequest request) where TResponse : class, IResponse
        {
            long startTime = TimeInfo.Instance.ServerNow();
            if (!target.IsValid)
            {
                return Failure<TResponse>(RpcFailureKind.InvalidAddress, 0, "Entity address is invalid", startTime);
            }

            try
            {
                IResponse response = await root.GetComponent<MessageLocationSenderComponent>()
                    .Get(target.LocationType)
                    .Call(target.EntityId, request);
                return ToResult<TResponse>(response, startTime);
            }
            catch (RpcException e)
            {
                return Failure<TResponse>(Classify(e.Error), e.Error, e.Message, startTime);
            }
            catch (Exception e)
            {
                return Failure<TResponse>(RpcFailureKind.InfrastructureError, 0, e.ToString(), startTime);
            }
        }

        private static RpcCallResult<TResponse> ToResult<TResponse>(IResponse response, long startTime)
            where TResponse : class, IResponse
        {
            long elapsed = TimeInfo.Instance.ServerNow() - startTime;
            if (response is not TResponse typedResponse)
            {
                string actualType = response?.GetType().FullName ?? "null";
                return RpcCallResult<TResponse>.Failure(
                    RpcFailureKind.ResponseTypeMismatch,
                    response?.Error ?? 0,
                    $"Expected response {typeof(TResponse).FullName}, but received {actualType}",
                    elapsed);
            }

            if (typedResponse.Error != 0)
            {
                return RpcCallResult<TResponse>.Failure(
                    Classify(typedResponse.Error),
                    typedResponse.Error,
                    typedResponse.Message,
                    elapsed,
                    typedResponse);
            }

            return RpcCallResult<TResponse>.Success(typedResponse, elapsed);
        }

        private static RpcCallResult<TResponse> Failure<TResponse>(
            RpcFailureKind kind,
            int error,
            string message,
            long startTime) where TResponse : class, IResponse
        {
            return RpcCallResult<TResponse>.Failure(
                kind,
                error,
                message,
                TimeInfo.Instance.ServerNow() - startTime);
        }

        private static RpcFailureKind Classify(int error)
        {
            return error switch
            {
                ErrorCore.ERR_NotFoundActor => RpcFailureKind.TargetNotFound,
                ErrorCore.ERR_MessageTimeout => RpcFailureKind.Timeout,
                _ => RpcFailureKind.RemoteError,
            };
        }
    }
}
