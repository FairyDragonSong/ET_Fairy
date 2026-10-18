namespace ET.Server
{
    [ConsoleHandler(ConsoleMode.Shutdown)]
    public class ShutdownConsoleHandler: IConsoleHandler
    {
        // 将控制台停机命令转交给统一停机协调器。
        public async ETTask Run(Fiber fiber, ModeContex contex, string content)
        {
            ShutdownCoordinator.Instance.Request();
            await ETTask.CompletedTask;
        }
    }
}
