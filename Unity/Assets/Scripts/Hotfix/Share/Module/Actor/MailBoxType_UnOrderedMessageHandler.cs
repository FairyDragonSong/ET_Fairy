namespace ET
{
    [Invoke((long)MailBoxType.UnOrderedMessage)]
    public class MailBoxType_UnOrderedMessageHandler: AInvokeHandler<MailBoxInvoker>
    {
        public override void Handle(MailBoxInvoker args)
        {
            HandleAsync(args).Coroutine();
        }
        
        private static async ETTask HandleAsync(MailBoxInvoker args)
        {
            MailBoxComponent mailBoxComponent = args.MailBoxComponent;
            
            MessageObject messageObject = args.MessageObject;
            
#if DOTNET
            bool observe = mailBoxComponent.IScene.SceneType == SceneType.Map;
            long started = observe ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            try
            {
#endif
                await MessageDispatcher.Instance.Handle(mailBoxComponent.Parent, args.FromAddress, messageObject);
#if DOTNET
            }
            finally
            {
                if (observe) Server.OperationalMetrics.Instance?.RecordHandlerDuration(Server.HandlerLatency.MapUnorderedMailbox,
                    System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
#endif
        }
    }
}
