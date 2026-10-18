using System;
using System.Collections.Generic;
using System.Linq;

namespace ET.Server
{
    public static class DistributedTopologyPolicy
    {
        // 拒绝尚无一致性保障的多 Location 或未共享准入的多 Realm 拓扑。
        public static void Validate(Options options, IReadOnlyCollection<StartSceneConfig> scenes)
        {
            if (options == null || scenes == null) throw new InvalidOperationException("Server topology is unavailable");
            if (options.GateKeyRpcTimeoutMilliseconds is < 250 or > 5000)
                throw new InvalidOperationException("Gate key RPC timeout must be between 250 and 5000 milliseconds");
            int locations = scenes.Count(scene => scene.Type == SceneType.Location);
            if (locations != 1) throw new InvalidOperationException("Exactly one Location is supported until consistent failover is implemented");

            foreach (IGrouping<int, StartSceneConfig> zone in scenes.Where(scene => scene.Type is SceneType.Realm or SceneType.Gate or SceneType.Map).GroupBy(scene => scene.Zone))
            {
                int realms = zone.Count(scene => scene.Type == SceneType.Realm);
                if (realms < 1) throw new InvalidOperationException($"Gameplay zone {zone.Key} has no Realm");
                if (realms > 1 && options.SharedLoginAdmissionEnabled != 1)
                {
                    throw new InvalidOperationException($"Gameplay zone {zone.Key} has multiple Realm scenes without shared login admission");
                }
            }
        }
    }
}
