using System;
using System.Security.Cryptography;
using System.Text;

namespace ET.Server;

public static class AccountPasswordEnvelope
{
    // 解密客户端密码并立即转换为不可逆哈希。
    public static PasswordHashData DecryptAndHash(string encrypted,string privateKeyPem)
    {
        byte[] ciphertext=Convert.FromBase64String(encrypted);
        using RSA rsa=RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        if(rsa.KeySize<4096 || ciphertext.Length!=rsa.KeySize/8)throw new InvalidOperationException("Invalid password envelope");
        byte[] plaintext=rsa.Decrypt(ciphertext,RSAEncryptionPadding.OaepSHA256);
        try
        {
            string password=new UTF8Encoding(false,true).GetString(plaintext);
            if(!AccountInputValidator.IsPasswordValid(password))throw new InvalidOperationException("Invalid password policy");
            return PasswordHasher.Hash(password);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    // 计算加密信封摘要，用于审计关联而不记录明文。
    public static string Digest(string encrypted) => Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(encrypted)));
}
