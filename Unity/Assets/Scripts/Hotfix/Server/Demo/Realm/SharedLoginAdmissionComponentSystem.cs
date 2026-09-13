namespace ET.Server
{
    [EntitySystemOf(typeof(SharedLoginAdmissionComponent))]
    [FriendOf(typeof(SharedLoginAdmissionComponent))]
    public static partial class SharedLoginAdmissionComponentSystem
    {
        [EntitySystem]
        private static void Awake(this SharedLoginAdmissionComponent self)
        {
            self.Enabled = Options.Instance.SharedLoginAdmissionEnabled == 1;
            if (self.Enabled)
            {
                self.HmacKey = ProductionSecretPolicy.ReadSharedLoginAdmissionKey(Options.Instance.Develop == 0);
            }
        }

        [EntitySystem]
        private static void Destroy(this SharedLoginAdmissionComponent self)
        {
            if (self.HmacKey != null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(self.HmacKey);
            self.HmacKey = null;
            self.Enabled = false;
        }
    }
}
