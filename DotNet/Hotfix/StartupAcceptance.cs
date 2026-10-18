using System;

namespace ET.Server;

[ConsoleHandler("startup-check")]
public sealed class StartupAcceptance : IConsoleHandler
{
    // 等待启动流程结束后输出机器可识别的就绪状态。
    public async ETTask Run(Fiber fiber, ModeContex context, string content)
    {
        await fiber.WaitFrameFinish();
        Console.WriteLine(StartupCoordinator.Instance.IsReady ? "STARTUP_READY" : "STARTUP_NOT_READY");
        context.Parent.RemoveComponent<ModeContex>();
    }
}
