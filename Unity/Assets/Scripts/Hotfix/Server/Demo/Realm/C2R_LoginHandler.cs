using System;
using System.Net;


namespace ET.Server
{
	[MessageSessionHandler(SceneType.Realm)]
	public class C2R_LoginHandler : MessageSessionHandler<C2R_Login, R2C_Login>
	{
		protected override async ETTask Run(Session session, C2R_Login request, R2C_Login response)
		{
			long started=System.Diagnostics.Stopwatch.GetTimestamp();
			bool outcomeRecorded=false;
			try
			{
			OperationalMetrics.Instance.RecordLoginAttempt();
			string traceId = $"{session.Id:x}-{request.RpcId}";
			string ipAddress = session.RemoteAddress?.Address.ToString() ?? "unknown";
			if (!session.Root().GetComponent<LoginRateLimiterComponent>().TryAcquire(
					ipAddress,
					request.Account,
					out long retryAfterMilliseconds))
			{
				response.Error = ErrorCore.ERR_LoginRateLimited;
				response.Message = "登录请求过于频繁，请稍后重试";
				OperationalMetrics.Instance.RecordLoginFailure(LoginFailureKind.Rejected);
				outcomeRecorded=true;
				Log.Warning($"event=login_rejected trace_id={traceId} reason=rate_limited ip_key={ipAddress.GetLongHashCode()} retry_after_ms={retryAfterMilliseconds}");
				return;
			}

			SharedLoginAdmissionResult sharedAdmission = await SharedLoginAdmissionStore.TryAcquire(
				session.Root(),
				session.Root().GetComponent<SharedLoginAdmissionComponent>(),
				ipAddress,
				request.Account);
			if (!sharedAdmission.IsAvailable)
			{
				response.Error = ErrorCore.ERR_AccountStorageUnavailable;
				response.Message = "账号服务暂时不可用";
				OperationalMetrics.Instance.RecordLoginFailure(LoginFailureKind.ServiceError);
				outcomeRecorded = true;
				Log.Error($"event=login_rejected trace_id={traceId} reason=shared_rate_limit_unavailable");
				return;
			}
			if (!sharedAdmission.IsAccepted)
			{
				response.Error = ErrorCore.ERR_LoginRateLimited;
				response.Message = "登录请求过于频繁，请稍后重试";
				OperationalMetrics.Instance.RecordLoginFailure(LoginFailureKind.Rejected);
				outcomeRecorded = true;
				Log.Warning($"event=login_rejected trace_id={traceId} reason=shared_rate_limited ip_key={ipAddress.GetLongHashCode()} retry_after_ms={sharedAdmission.RetryAfterMilliseconds}");
				return;
			}

			AccountAuthenticationResult authenticationResult = await AccountAuthentication.Authenticate(
				session.Root(),
				AccountAuthenticationRequest.Password(request.Account, request.Password));
			if (!authenticationResult.IsSuccess)
			{
				response.Error = authenticationResult.Error;
				response.Message = authenticationResult.Message;
				OperationalMetrics.Instance.RecordLoginFailure(authenticationResult.Error==ErrorCore.ERR_AccountStorageUnavailable?LoginFailureKind.ServiceError:LoginFailureKind.Rejected);
				outcomeRecorded=true;
				Log.Warning($"event=login_rejected trace_id={traceId} reason=authentication error={response.Error}");
				return;
			}

			StartSceneConfig config = null;
			G2R_GetLoginKey g2RGetLoginKey = null;
			RpcCallResult<G2R_GetLoginKey> lastResult = default;
			int gateCount = RealmGateAddressHelper.GetGateCount(session.Zone());
			
			for (int attempt = 0; attempt < gateCount; ++attempt)
			{
				StartSceneConfig candidate = RealmGateAddressHelper.GetGate(session.Zone(), authenticationResult.LoginName, attempt);
				R2G_GetLoginKey requestKey = R2G_GetLoginKey.Create();
				requestKey.Account = authenticationResult.LoginName;
				requestKey.AccountStateVersion = authenticationResult.StateVersion;
				lastResult = await session.Root().CallService<G2R_GetLoginKey>(ServiceAddress.From(candidate.ActorId), requestKey);
				if (lastResult.IsSuccess)
				{
					config = candidate;
					g2RGetLoginKey = lastResult.Response;
					break;
				}
				lastResult.Response?.Dispose();
				if (!RealmGateAddressHelper.ShouldTryNext(lastResult.FailureKind, lastResult.Error)) break;
				Log.Warning($"event=gate_key_retry trace_id={traceId} failed_gate={candidate.Id} attempt={attempt + 1} failure={lastResult.FailureKind}");
			}
			if (g2RGetLoginKey == null)
			{
				response.Error = lastResult.Error == 0 ? ErrorCore.ERR_RpcFail : lastResult.Error;
				response.Message = lastResult.Message;
				OperationalMetrics.Instance.RecordLoginFailure(LoginFailureKind.ServiceError);
				outcomeRecorded = true;
				Log.Error($"event=login_rejected trace_id={traceId} reason=all_gate_key_rpc_failed error={response.Error}");
				return;
			}

			using (g2RGetLoginKey)
			{

				response.Address = config.InnerIPPort.ToString();
				response.Key = g2RGetLoginKey.Key;
				response.GateId = g2RGetLoginKey.GateId;
				OperationalMetrics.Instance.RecordLoginSuccess();
				outcomeRecorded=true;
				Log.Info($"event=login_succeeded trace_id={traceId} gate_id={response.GateId}");
			}
			
			CloseSession(session).Coroutine();
			}
			catch
			{
				if(!outcomeRecorded)OperationalMetrics.Instance?.RecordLoginFailure(LoginFailureKind.ServiceError);
				throw;
			}
			finally
			{
				OperationalMetrics.Instance?.RecordHandlerDuration(HandlerLatency.RealmLogin,(System.Diagnostics.Stopwatch.GetTimestamp()-started)*1000.0/System.Diagnostics.Stopwatch.Frequency);
			}
		}

		private async ETTask CloseSession(Session session)
		{
			await session.Root().GetComponent<TimerComponent>().WaitAsync(1000);
			session.Dispose();
		}
	}
}
