using System;

namespace ET.Server
{
    [Invoke((long)SceneType.BenchmarkClient)]
    public class NetComponentOnReadInvoker_BenchmarkClient: AInvokeHandler<NetComponentOnRead>
    {
        public override void Handle(NetComponentOnRead args)
        {
            Session session = args.Session;
            object message = args.Message;
#if DOTNET
            GameplayProbe probe = session.Scene().GetComponent<GameplayProbe>();
            if (probe != null)
            {
                if (message is Match2G_NotifyMatchSuccess) ++probe.Matches;
                if (message is Room2C_Start) ++probe.Starts;
                if (message is G2C_Reconnect reconnect)
                {
                    ++probe.Reconnects;
                    probe.ReconnectUnits = reconnect.UnitInfos.Count;
                    probe.ReconnectStartTime = reconnect.StartTime;
                }
            }
#endif
            // 根据消息接口判断是不是Actor消息，不同的接口做不同的处理,比如需要转发给Chat Scene，可以做一个IChatMessage接口
            switch (message)
            {
                case IResponse response:
                {
                    session.OnResponse(response);
                    break;
                }
            }
        }
    }
}
