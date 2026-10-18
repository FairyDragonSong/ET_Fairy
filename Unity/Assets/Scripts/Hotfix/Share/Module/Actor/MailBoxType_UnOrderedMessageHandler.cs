namespace ET
{
    [Invoke((long)MailBoxType.UnOrderedMessage)]
    public class MailBoxType_UnOrderedMessageHandler: AInvokeHandler<MailBoxInvoker>
    {
        public override void Handle(MailBoxInvoker args)
        {
            HandleAsync(args).Coroutine();
        }
        
        // 处理无需返回值的异步 Actor 消息。
        internal static async ETTask HandleAsync(MailBoxInvoker args)
        {
            MailBoxComponent mailBoxComponent = args.MailBoxComponent;
            
            MessageObject messageObject = args.MessageObject;
            
#if DOTNET
            bool observe = mailBoxComponent.IScene.SceneType == SceneType.Map;
            var metrics = observe ? Server.OperationalMetrics.Instance : null;
            bool failed = false;
            long started = observe ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            metrics?.StartMapMailbox(false);
            try
            {
#endif
                await MessageDispatcher.Instance.Handle(mailBoxComponent.Parent, args.FromAddress, messageObject);
#if DOTNET
            }
            catch
            {
                failed = true;
                throw;
            }
            finally
            {
                metrics?.FinishMapMailbox(false, failed);
                if (observe) metrics?.RecordHandlerDuration(Server.HandlerLatency.MapUnorderedMailbox,
                    System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
#endif
        }
    }
}
