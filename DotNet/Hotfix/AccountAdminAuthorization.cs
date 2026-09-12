using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ET.Server;

public static class AccountAdminAuthorization
{
    public static AccountAdminCommand Verify(string envelopeJson,string trustJson,long now)
    {
        if(string.IsNullOrEmpty(envelopeJson)||envelopeJson.Length>32768||string.IsNullOrEmpty(trustJson)||trustJson.Length>131072)
            throw new InvalidOperationException("Invalid administration envelope");
        var options=new JsonSerializerOptions { UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,MaxDepth=8 };
        var envelope=JsonSerializer.Deserialize<AccountAdminEnvelope>(envelopeJson,options);
        var operators=JsonSerializer.Deserialize<AccountAdminOperator[]>(trustJson,options);
        var matches=operators?.Where(o=>o.Id==envelope?.OperatorId).ToArray();
        if(matches==null||matches.Length!=1)throw new InvalidOperationException("Unknown operator");
        var identity=matches[0];
        if(identity.ExpiresAt<=now||string.IsNullOrEmpty(identity.PublicKeyPem)||identity.PublicKeyPem.Contains("PRIVATE KEY",StringComparison.Ordinal))
            throw new InvalidOperationException("Operator trust invalid");
        byte[] payload=Convert.FromBase64String(envelope.Payload);
        byte[] signature=Convert.FromBase64String(envelope.Signature);
        using RSA rsa=RSA.Create();
        rsa.ImportFromPem(identity.PublicKeyPem);
        if(rsa.KeySize<3072||!rsa.VerifyData(payload,signature,HashAlgorithmName.SHA256,RSASignaturePadding.Pss))
            throw new InvalidOperationException("Invalid operator signature");
        var command=JsonSerializer.Deserialize<AccountAdminCommand>(payload,options);
        if(command==null||command.OperatorId!=identity.Id||command.Database!=identity.Database||
            string.IsNullOrWhiteSpace(command.DeploymentId)||command.DeploymentId!=identity.DeploymentId||command.Zone!=identity.Zone||command.Zone<=0||
            command.AccountId<=0||command.ConfirmAccountId!=command.AccountId||command.ExpectedVersion<0||command.ExpectedVersion==long.MaxValue||
            command.IssuedAt<now-300||command.IssuedAt>now+30||command.ExpiresAt<=now||command.ExpiresAt>command.IssuedAt+300||
            !Guid.TryParseExact(command.OperationId,"N",out _)||string.IsNullOrEmpty(command.ReasonCode)||command.ReasonCode.Length>64||
            (command.Action!="disable"&&command.Action!="enable"&&command.Action!="revoke"&&command.Action!="close"&&command.Action!="reset-password")||
            (command.Action=="reset-password" ? string.IsNullOrEmpty(command.EncryptedPassword) ||
                (command.PasswordKeyId!=null&&!ProductionSecretPolicy.IsSafeKeyId(command.PasswordKeyId)) :
                command.EncryptedPassword!=null||command.PasswordKeyId!=null)||
            identity.Actions==null||!identity.Actions.Contains(command.Action,StringComparer.Ordinal))
            throw new InvalidOperationException("Administration command not authorized");
        return command;
    }
}
