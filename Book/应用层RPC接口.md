# 应用层 RPC 接口

业务代码优先使用 `ApplicationRpc`，不直接选择 `ProcessInnerSender`、`ProcessOuterSender` 或 `MessageLocationSender`。

## 固定服务地址

适用于已经知道 `ActorId` 且不会迁移的服务：

```csharp
ServiceAddress target = ServiceAddress.From(matchSceneConfig.ActorId);
RpcCallResult<Match2G_Match> result = await root.CallService<Match2G_Match>(target, request);

if (!result.IsSuccess)
{
    Log.Error($"call failed: {result.FailureKind}, error: {result.Error}, message: {result.Message}");
    return;
}

using Match2G_Match response = result.Response;
```

单向消息使用：

```csharp
root.NotifyService(target, message);
```

## 可迁移实体地址

适用于 Player、Unit、GateSession 等通过 Location 服务定位的实体：

```csharp
EntityAddress target = EntityAddress.From(LocationType.Unit, unitId);
RpcCallResult<M2C_TestResponse> result = await root.CallEntity<M2C_TestResponse>(target, request);
```

成功响应仍遵循 ET `MessageObject` 的生命周期规则；使用完成后应通过 `using` 或 `Dispose()` 回收。

单向 Location 消息使用：

```csharp
root.NotifyEntity(target, message);
```

## 错误处理

`RpcCallResult<T>` 将常见失败归类为：无效地址、目标不存在、超时、远端错误、响应类型不匹配和基础设施异常。业务失败不会要求调用方捕获 `RpcException`。

ET 底层目前使用固定的 Actor RPC 超时，因此这一层暂不暴露不能真实生效的自定义超时参数。
