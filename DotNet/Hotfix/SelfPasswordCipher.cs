using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ET.Server;

public static class SelfPasswordCipher
{
    private const string Purpose="ET.PasswordChange.v1";
    public static string Encrypt(SelfPasswordChange request,string trustedPublicKeyPem)
    {
        using RSA rsa=RSA.Create();rsa.ImportFromPem(trustedPublicKeyPem);
        if(rsa.KeySize<4096)throw new InvalidOperationException("Password encryption key too small");
        byte[] key=RandomNumberGenerator.GetBytes(32),nonce=RandomNumberGenerator.GetBytes(12),tag=new byte[16];
        byte[] plaintext=JsonSerializer.SerializeToUtf8Bytes(request),ciphertext=new byte[plaintext.Length];
        try
        {
            using var aes=new AesGcm(key,16);
            aes.Encrypt(nonce,plaintext,ciphertext,tag,Encoding.UTF8.GetBytes(Purpose));
            return JsonSerializer.Serialize(new PasswordChangeCipher {Key=Convert.ToBase64String(rsa.Encrypt(key,RSAEncryptionPadding.OaepSHA256)),Nonce=Convert.ToBase64String(nonce),Ciphertext=Convert.ToBase64String(ciphertext),Tag=Convert.ToBase64String(tag)});
        }
        finally {CryptographicOperations.ZeroMemory(key);CryptographicOperations.ZeroMemory(plaintext);}
    }

    public static SelfPasswordChange Decrypt(string envelope,string privateKeyPem)
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
            aes.Decrypt(Convert.FromBase64String(data.Nonce),ciphertext,Convert.FromBase64String(data.Tag),plaintext,Encoding.UTF8.GetBytes(Purpose));
            var request=JsonSerializer.Deserialize<SelfPasswordChange>(plaintext,options);
            long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if(request==null||!Guid.TryParseExact(request.OperationId,"N",out _)||request.ExpiresAt<=now||request.ExpiresAt>now+300||
                !AccountInputValidator.IsPasswordValid(request.OldPassword)||!AccountInputValidator.IsPasswordValid(request.NewPassword)||request.OldPassword==request.NewPassword)
                throw new InvalidOperationException("Invalid password change");
            return request;
        }
        finally {CryptographicOperations.ZeroMemory(key);CryptographicOperations.ZeroMemory(plaintext);}
    }
}
