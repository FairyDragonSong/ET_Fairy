namespace ET
{
    /// <summary>
    /// 同一个 Actor 的消息必须等上一条完整处理完毕，下一条才能继续：
    //  消息 A 开始
    //         消息 A await 数据库
    //         消息 B 等待
    //  消息 A 完成
    //         消息 B 开始
    /// </summary>
    [Invoke((long)MailBoxType.OrderedMessage)]
    public class MailBoxType_OrderedMessageHandler: AInvokeHandler<MailBoxInvoker>
    {
        public override void Handle(MailBoxInvoker args)
        {
            HandleInner(args).Coroutine();
        }

        private static async ETTask HandleInner(MailBoxInvoker args)
        {
            MailBoxComponent mailBoxComponent = args.MailBoxComponent;
            
            MessageObject messageObject = args.MessageObject;

            Fiber fiber = mailBoxComponent.Fiber();
            if (fiber.IsDisposed)
            {
                return;
            }

            long instanceId = mailBoxComponent.InstanceId;
#if DOTNET
            bool observe = mailBoxComponent.IScene.SceneType == SceneType.Map;
            long started = observe ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            try
            {
#endif
            using (await fiber.Root.GetComponent<CoroutineLockComponent>().Wait(CoroutineLockType.Mailbox, mailBoxComponent.ParentInstanceId))
            {
                if (mailBoxComponent.InstanceId != instanceId)
                {
                    if (messageObject is IRequest request)
                    {
                        IResponse resp = MessageHelper.CreateResponse(request.GetType(), request.RpcId, ErrorCore.ERR_NotFoundActor);
                        mailBoxComponent.Root().GetComponent<ProcessInnerSender>().Reply(args.FromAddress, resp);
                    }
                    return;
                }
                await MessageDispatcher.Instance.Handle(mailBoxComponent.Parent, args.FromAddress, messageObject);
            }
#if DOTNET
            }
            finally
            {
                if (observe) Server.OperationalMetrics.Instance?.RecordHandlerDuration(Server.HandlerLatency.MapOrderedMailbox,
                    System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
#endif
        }
    }
}
