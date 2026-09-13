namespace ET.Server
{
    [ComponentOf(typeof(Scene))]
    public sealed class SharedLoginAdmissionComponent: Entity, IAwake, IDestroy
    {
        [MongoDB.Bson.Serialization.Attributes.BsonIgnore]
        public byte[] HmacKey;

        public bool Enabled;
    }
}
