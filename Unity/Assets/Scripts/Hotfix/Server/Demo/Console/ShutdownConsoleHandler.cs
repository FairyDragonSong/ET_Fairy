namespace ET.Server
{
    [ConsoleHandler(ConsoleMode.Shutdown)]
    public class ShutdownConsoleHandler: IConsoleHandler
    {
        public async ETTask Run(Fiber fiber, ModeContex contex, string content)
        {
            ShutdownCoordinator.Instance.Request();
            await ETTask.CompletedTask;
        }
    }
}
