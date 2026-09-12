using Luban;
using System;
using System.Collections.Generic;
using System.IO;

namespace ET
{
    [Invoke]
    public class GetAllConfigBytes : AInvokeHandler<ConfigLoader.GetAllConfigBytes, ETTask<Dictionary<Type, ByteBuf>>>
    {
        public override async ETTask<Dictionary<Type, ByteBuf>> Handle(ConfigLoader.GetAllConfigBytes args)
        {
            Dictionary<Type, ByteBuf> output = new Dictionary<Type, ByteBuf>();
            List<string> startConfigs = new()
            {
                "StartMachineConfigCategory", "StartProcessConfigCategory", "StartSceneConfigCategory", "StartZoneConfigCategory"
            };
            HashSet<Type> configTypes = CodeTypes.Instance.GetTypes(typeof(ConfigAttribute));
            foreach (Type configType in configTypes)
            {
                string configFilePath;
                if (startConfigs.Contains(configType.Name))
                {
                    configFilePath = $"../Config/Excel/s/{Options.Instance.StartConfig}/{configType.Name}.bytes";
                }
                else
                {
                    configFilePath = $"../Config/Excel/s/{configType.Name}.bytes";
                }

                ByteBuf configBytes = new ByteBuf(File.ReadAllBytes(configFilePath));
                if (configType.Name == "StartMachineConfigCategory" &&
                    Environment.GetEnvironmentVariable("ET_GAMEPLAY_LOOPBACK") == "1")
                {
                    if (Options.Instance.Develop != 1 || Environment.GetEnvironmentVariable("ET_MONGO_DATABASE") != "ET_LOCAL_GAMEPLAY")
                        throw new InvalidOperationException("Loopback test configuration requires the isolated gameplay environment");
                    ByteBuf local = new ByteBuf();
                    int count = configBytes.ReadSize();
                    local.WriteSize(count);
                    for (int i = 0; i < count; ++i)
                    {
                        local.WriteInt(configBytes.ReadInt());
                        configBytes.ReadString(); configBytes.ReadString();
                        local.WriteString("127.0.0.1"); local.WriteString("127.0.0.1");
                        local.WriteString(configBytes.ReadString());
                    }
                    configBytes = local;
                }
                output[configType] = configBytes;
            }

            await ETTask.CompletedTask;
            return output;
        }
    }

    [Invoke]
    public class GetOneConfigBytes : AInvokeHandler<ConfigLoader.GetOneConfigBytes, ByteBuf>
    {
        public override ByteBuf Handle(ConfigLoader.GetOneConfigBytes args)
        {
            return new ByteBuf(File.ReadAllBytes($"../Config/Excel/s/{args.ConfigName}.bytes"));
        }
    }
}
