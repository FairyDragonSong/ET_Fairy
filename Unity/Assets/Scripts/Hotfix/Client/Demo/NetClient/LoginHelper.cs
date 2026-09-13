namespace ET.Client
{
    public static class LoginHelper
    {
        public static async ETTask Login(Scene root, string account, string password)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            bool succeeded = false;
            try
            {
                root.RemoveComponent<ClientSenderComponent>();

                ClientSenderComponent clientSenderComponent = root.AddComponent<ClientSenderComponent>();

                long playerId = await clientSenderComponent.LoginAsync(account, password);

                root.GetComponent<PlayerComponent>().MyId = playerId;

                await EventSystem.Instance.PublishAsync(root, new LoginFinish());
                succeeded = true;
            }
            finally
            {
                ClientExperienceTelemetry.RecordSince(ClientExperienceStage.Login, started, succeeded);
            }
        }
    }
}
