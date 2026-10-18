using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ET.Server;

public static class SelfPasswordCipher
{
    private const string Purpose="ET.PasswordChange.v1";
    // 加密自助改密请求，避免密码明文传输。
    public static string Encrypt(SelfPasswordChange request,string trustedPublicKeyPem)
        => Encrypt(request,null,trustedPublicKeyPem);

    // 加密自助改密请求，避免密码明文传输。
    public static string Encrypt(SelfPasswordChange request,string keyId,string trustedPublicKeyPem)
        => EncryptPayload(request,keyId,trustedPublicKeyPem,Purpose);

    // 加密自助注销请求并绑定请求用途。
    public static string EncryptClosure(SelfAccountClosure request,string trustedPublicKeyPem)
        => EncryptClosure(request,null,trustedPublicKeyPem);

    // 加密自助注销请求并绑定请求用途。
    public static string EncryptClosure(SelfAccountClosure request,string keyId,string trustedPublicKeyPem)
        => EncryptPayload(request,keyId,trustedPublicKeyPem,"ET.AccountClosure.v1");

    private static string EncryptPayload<T>(T request,string keyId,string trustedPublicKeyPem,string purpose)
    {
        if(keyId!=null&&!ProductionSecretPolicy.IsSafeKeyId(keyId))throw new InvalidOperationException("Invalid password encryption key id");
        using RSA rsa=RSA.Create();rsa.ImportFromPem(trustedPublicKeyPem);
        if(rsa.KeySize<4096)throw new InvalidOperationException("Password encryption key too small");
        byte[] key=RandomNumberGenerator.GetBytes(32),nonce=RandomNumberGenerator.GetBytes(12),tag=new byte[16];
        byte[] plaintext=JsonSerializer.SerializeToUtf8Bytes(request),ciphertext=new byte[plaintext.Length];
        try
        {
            using var aes=new AesGcm(key,16);
            aes.Encrypt(nonce,plaintext,ciphertext,tag,AssociatedData(purpose,keyId));
            return JsonSerializer.Serialize(new PasswordChangeCipher {KeyId=keyId,Key=Convert.ToBase64String(rsa.Encrypt(key,RSAEncryptionPadding.OaepSHA256)),Nonce=Convert.ToBase64String(nonce),Ciphertext=Convert.ToBase64String(ciphertext),Tag=Convert.ToBase64String(tag)});
        }
        finally {CryptographicOperations.ZeroMemory(key);CryptographicOperations.ZeroMemory(plaintext);}
    }

    // 从加密信封中读取密钥标识。
    public static string GetKeyId(string envelope)
    {
        if(string.IsNullOrEmpty(envelope)||envelope.Length>16384)throw new InvalidOperationException("Invalid password envelope");
        var options=new JsonSerializerOptions {MaxDepth=4,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
        string keyId=JsonSerializer.Deserialize<PasswordChangeCipher>(envelope,options)?.KeyId;
        if(keyId!=null&&!ProductionSecretPolicy.IsSafeKeyId(keyId))throw new InvalidOperationException("Invalid password encryption key id");
        return keyId;
    }

    // 解密并校验自助改密信封。
    public static SelfPasswordChange Decrypt(string envelope,string privateKeyPem)
    {
        var request=DecryptPayload<SelfPasswordChange>(envelope,privateKeyPem,Purpose);
        long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if(request==null||!Guid.TryParseExact(request.OperationId,"N",out _)||request.ExpiresAt<=now||request.ExpiresAt>now+300||
            !AccountInputValidator.IsPasswordValid(request.OldPassword)||!AccountInputValidator.IsPasswordValid(request.NewPassword)||request.OldPassword==request.NewPassword)
            throw new InvalidOperationException("Invalid password change");
        return request;
    }

    // 解密并校验自助注销信封。
    public static SelfAccountClosure DecryptClosure(string envelope,string privateKeyPem)
    {
        var request=DecryptPayload<SelfAccountClosure>(envelope,privateKeyPem,"ET.AccountClosure.v1");
        long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if(request==null||!Guid.TryParseExact(request.OperationId,"N",out _)||request.ExpiresAt<=now||request.ExpiresAt>now+300||
            !AccountInputValidator.TryNormalizeLoginName(request.LoginName,out string normalized)||normalized!=request.LoginName||
            request.ConfirmLoginName!=request.LoginName||!AccountInputValidator.IsPasswordValid(request.Password))
            throw new InvalidOperationException("Invalid account closure");
        return request;
    }

    private static T DecryptPayload<T>(string envelope,string privateKeyPem,string purpose)
    {
        if(string.IsNullOrEmpty(envelope)||envelope.Length>16384)throw new InvalidOperationException("Invalid password envelope");
        var options=new JsonSerializerOptions {MaxDepth=4,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
        var data=JsonSerializer.Deserialize<PasswordChangeCipher>(envelope,options);
        using RSA rsa=RSA.Create();rsa.ImportFromPem(privateKeyPem);
        if(rsa.KeySize<4096)throw new InvalidOperationException("Password encryption key too small");
        byte[] key=rsa.Decrypt(Convert.FromBase64String(data.Key),RSAEncryptionPadding.OaepSHA256);
        byte[] ciphertext=Convert.FromBase64String(data.Ciphertext),plaintext=new byte[ciphertext.Length];
        try
        {
            if(key.Length!=32)throw new InvalidOperationException("Invalid password envelope key");
            using var aes=new AesGcm(key,16);
            aes.Decrypt(Convert.FromBase64String(data.Nonce),ciphertext,Convert.FromBase64String(data.Tag),plaintext,AssociatedData(purpose,data.KeyId));
            return JsonSerializer.Deserialize<T>(plaintext,options);
        }
        finally {CryptographicOperations.ZeroMemory(key);CryptographicOperations.ZeroMemory(plaintext);}
    }

    // 构造绑定请求用途与密钥版本的附加认证数据。
    private static byte[] AssociatedData(string purpose,string keyId) => Encoding.UTF8.GetBytes(keyId==null?purpose:purpose+"|"+keyId);
}
