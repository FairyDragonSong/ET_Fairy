using System;
using System.Security.Cryptography;
using System.Text;

namespace ET.Server
{
    public readonly struct PasswordHashData
    {
        public PasswordHashScheme Scheme { get; }

        public string Salt { get; }

        public string Hash { get; }

        public int Iterations { get; }

        public PasswordHashData(PasswordHashScheme scheme, string salt, string hash, int iterations)
        {
            this.Scheme = scheme;
            this.Salt = salt;
            this.Hash = hash;
            this.Iterations = iterations;
        }
    }

    public static class PasswordHasher
    {
        public const int DefaultIterations = 600_000;
        private const int SaltSize = 16;
        private const int HashSize = 32;
        private const int MinimumAcceptedIterations = 100_000;
        private const int MaximumAcceptedIterations = 1_000_000;

        public static PasswordHashData Hash(string password)
        {
            byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
            byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
            try
            {
                byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
                    passwordBytes,
                    salt,
                    DefaultIterations,
                    HashAlgorithmName.SHA256,
                    HashSize);
                try
                {
                    return new PasswordHashData(
                        PasswordHashScheme.Pbkdf2Sha256,
                        Convert.ToBase64String(salt),
                        Convert.ToBase64String(hash),
                        DefaultIterations);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(hash);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(passwordBytes);
                CryptographicOperations.ZeroMemory(salt);
            }
        }

        public static bool Verify(
            string password,
            PasswordHashScheme scheme,
            string saltText,
            string hashText,
            int iterations)
        {
            if (scheme != PasswordHashScheme.Pbkdf2Sha256 ||
                iterations < MinimumAcceptedIterations ||
                iterations > MaximumAcceptedIterations ||
                string.IsNullOrEmpty(saltText) ||
                string.IsNullOrEmpty(hashText))
            {
                SimulateVerification(password);
                return false;
            }

            byte[] salt;
            byte[] expectedHash;
            try
            {
                salt = Convert.FromBase64String(saltText);
                expectedHash = Convert.FromBase64String(hashText);
            }
            catch (FormatException)
            {
                SimulateVerification(password);
                return false;
            }

            if (salt.Length < SaltSize || expectedHash.Length != HashSize)
            {
                CryptographicOperations.ZeroMemory(salt);
                CryptographicOperations.ZeroMemory(expectedHash);
                SimulateVerification(password);
                return false;
            }

            byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
            byte[] actualHash = null;
            try
            {
                actualHash = Rfc2898DeriveBytes.Pbkdf2(
                    passwordBytes,
                    salt,
                    iterations,
                    HashAlgorithmName.SHA256,
                    expectedHash.Length);
                return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(passwordBytes);
                CryptographicOperations.ZeroMemory(salt);
                CryptographicOperations.ZeroMemory(expectedHash);
                if (actualHash != null)
                {
                    CryptographicOperations.ZeroMemory(actualHash);
                }
            }
        }

        public static void SimulateVerification(string password)
        {
            byte[] passwordBytes = Encoding.UTF8.GetBytes(password ?? string.Empty);
            byte[] salt = new byte[SaltSize];
            byte[] hash = null;
            try
            {
                hash = Rfc2898DeriveBytes.Pbkdf2(
                    passwordBytes,
                    salt,
                    DefaultIterations,
                    HashAlgorithmName.SHA256,
                    HashSize);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(passwordBytes);
                CryptographicOperations.ZeroMemory(salt);
                if (hash != null)
                {
                    CryptographicOperations.ZeroMemory(hash);
                }
            }
        }
    }
}
