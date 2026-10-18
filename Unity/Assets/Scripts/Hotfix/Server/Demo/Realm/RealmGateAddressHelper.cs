using System.Collections.Generic;


namespace ET.Server
{
	public static partial class RealmGateAddressHelper
	{
		public static StartSceneConfig GetGate(int zone, string account)
		{
			return GetGate(zone, account, 0);
		}

		// 获取Gate。
		public static StartSceneConfig GetGate(int zone, string account, int attempt)
		{
			List<StartSceneConfig> zoneGates = StartSceneConfigCategory.Instance.Gates[zone];
			if (zoneGates.Count == 0) throw new System.InvalidOperationException($"Zone {zone} has no Gate");
			int index = GetCandidateIndex(zoneGates.Count, account, attempt);
			return zoneGates[index];
		}

		// 获取当前分区可用的 Gate 数量。
		public static int GetGateCount(int zone) => StartSceneConfigCategory.Instance.Gates[zone].Count;

		// 按账号稳定散列计算第 N 个 Gate 候选。
		public static int GetCandidateIndex(int gateCount, string account, int attempt)
		{
			if (gateCount <= 0 || string.IsNullOrEmpty(account) || attempt < 0 || attempt >= gateCount)
				throw new System.InvalidOperationException("Gate candidate attempt is outside the bounded candidate set");
			ulong hash = (ulong)account.GetLongHashCode();
			return (int)((hash + (ulong)attempt) % (ulong)gateCount);
		}

		// 判断当前 RPC 失败是否适合切换到下一个 Gate。
		public static bool ShouldTryNext(RpcFailureKind failureKind, int error) =>
			failureKind is RpcFailureKind.TargetNotFound or RpcFailureKind.Timeout or RpcFailureKind.InfrastructureError ||
			(failureKind == RpcFailureKind.RemoteError && error == ErrorCore.ERR_AccountStorageUnavailable);
	}
}
