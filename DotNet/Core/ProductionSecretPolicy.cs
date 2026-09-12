using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace ET;

public static class ProductionSecretPolicy
{
    private const string SecretRootVariable = "ET_SECRET_ROOT";

    public static void Validate(Options options)
    {
        if (options == null)
        {
            throw new InvalidOperationException("Server options are unavailable");
        }

        if (options.Develop != 0)
        {
            return;
        }

        bool metricsConfigured = HasValue("ET_METRICS_PORT") || HasValue("ET_METRICS_TOKEN_FILE") || HasValue("ET_METRICS_PREVIOUS_TOKEN_FILE");
        string[] adminVariables =
        [
            "ET_ACCOUNT_ADMIN_TRUST_FILE", "ET_ACCOUNT_ADMIN_MONGO_URI", "ET_ACCOUNT_ADMIN_MONGO_URI_FILE",
            "ET_ACCOUNT_ADMIN_DATABASE", "ET_ACCOUNT_ADMIN_ZONE", "ET_ACCOUNT_ADMIN_DEPLOYMENT_ID",
        ];
        bool administrationConfigured = adminVariables.Any(HasValue);

        if (HasValue("ET_ACCOUNT_PASSWORD_PRIVATE_KEY_FILE") || HasValue("ET_ACCOUNT_PASSWORD_KEYRING_FILE"))
        {
            ValidatePasswordSources(true);
        }

        if (metricsConfigured)
        {
            if (!int.TryParse(Environment.GetEnvironmentVariable("ET_METRICS_PORT"), out int port) || port < 1024 || port > 65535)
            {
                throw new InvalidOperationException("Production metrics configuration is incomplete");
            }

            string token = ReadMetricsToken("ET_METRICS_TOKEN_FILE", true);
            if (HasValue("ET_METRICS_PREVIOUS_TOKEN_FILE"))
            {
                string previousToken = ReadMetricsToken("ET_METRICS_PREVIOUS_TOKEN_FILE", true);
                if (CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(token), System.Text.Encoding.ASCII.GetBytes(previousToken)))
                {
                    throw new InvalidOperationException("Production metrics rotation credentials must be distinct");
                }
            }
        }

        if (administrationConfigured)
        {
            foreach (string required in new[]
                     {
                         "ET_ACCOUNT_ADMIN_TRUST_FILE", "ET_ACCOUNT_ADMIN_DATABASE", "ET_ACCOUNT_ADMIN_ZONE",
                         "ET_ACCOUNT_ADMIN_DEPLOYMENT_ID",
                     })
            {
                if (!HasValue(required))
                {
                    throw new InvalidOperationException("Production administration configuration is incomplete");
                }
            }

            bool directUri = HasValue("ET_ACCOUNT_ADMIN_MONGO_URI");
            bool fileUri = HasValue("ET_ACCOUNT_ADMIN_MONGO_URI_FILE");
            if (directUri == fileUri || !int.TryParse(Environment.GetEnvironmentVariable("ET_ACCOUNT_ADMIN_ZONE"), out int zone) || zone <= 0)
            {
                throw new InvalidOperationException("Production administration configuration is incomplete");
            }

            string database = Environment.GetEnvironmentVariable("ET_ACCOUNT_ADMIN_DATABASE");
            string deployment = Environment.GetEnvironmentVariable("ET_ACCOUNT_ADMIN_DEPLOYMENT_ID");
            string trust = ReadManagedEnvironmentFile("ET_ACCOUNT_ADMIN_TRUST_FILE", 131072, true);
            ValidateAdministrationTrust(trust, database, deployment, zone);
            string administrationUri = Environment.GetEnvironmentVariable("ET_ACCOUNT_ADMIN_MONGO_URI");
            if (fileUri)
            {
                administrationUri = ReadManagedEnvironmentFile("ET_ACCOUNT_ADMIN_MONGO_URI_FILE", 8192, true).Trim();
            }
            ValidateAdministrationUri(administrationUri);
        }
    }

    public static void ValidatePasswordLogin(Options options)
    {
        if (options?.Develop != 0 || options.PasswordLoginEnabled != 1)
        {
            return;
        }

        ValidatePasswordSources(true);
    }

    public static string ReadPasswordPrivateKey(string keyId, bool requireManagedRoot)
    {
        bool legacy = HasValue("ET_ACCOUNT_PASSWORD_PRIVATE_KEY_FILE");
        bool ring = HasValue("ET_ACCOUNT_PASSWORD_KEYRING_FILE");
        if (legacy == ring)
        {
            throw new InvalidOperationException("Exactly one password decryption key source is required");
        }

        if (legacy)
        {
            if (!string.IsNullOrEmpty(keyId)) throw new InvalidOperationException("Password encryption key id is unsupported by the configured legacy key");
            string privateKey = ReadManagedEnvironmentFile("ET_ACCOUNT_PASSWORD_PRIVATE_KEY_FILE", 16384, requireManagedRoot);
            ValidatePasswordPrivateKey(privateKey);
            return privateKey;
        }

        string json = ReadManagedEnvironmentFile("ET_ACCOUNT_PASSWORD_KEYRING_FILE", 65536, requireManagedRoot);
        return ReadPasswordKeyRing(json, keyId, true);
    }

    public static string ReadMetricsToken(string variable, bool requireManagedRoot)
    {
        string token = ReadManagedEnvironmentFile(variable, 128, requireManagedRoot).Trim();
        if (token.Length != 64 || token.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidOperationException("Production metrics credential is invalid");
        }
        return token;
    }

    public static bool IsSafeKeyId(string keyId) => !string.IsNullOrWhiteSpace(keyId) && keyId.Length is >= 3 and <= 64 &&
        keyId.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    public static string ReadManagedEnvironmentFile(string variable, int maximumBytes, bool requireManagedRoot)
    {
        string path = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Required Secret file is not configured");
        }

        return ReadManagedFile(path, maximumBytes, requireManagedRoot);
    }

    public static string ReadManagedFile(string path, int maximumBytes, bool requireManagedRoot)
    {
        if (string.IsNullOrWhiteSpace(path) || maximumBytes <= 0 || !Path.IsPathFullyQualified(path))
        {
            throw new InvalidOperationException("Secret file path is invalid");
        }

        string fullPath = Path.GetFullPath(path);
        if (requireManagedRoot)
        {
            string rootValue = Environment.GetEnvironmentVariable(SecretRootVariable);
            if (string.IsNullOrWhiteSpace(rootValue) || !Path.IsPathFullyQualified(rootValue))
            {
                throw new InvalidOperationException("Production Secret root is not configured");
            }

            string root = Path.GetFullPath(rootValue).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string applicationRoot = Directory.GetParent(Path.GetFullPath(AppContext.BaseDirectory))?.FullName ?? AppContext.BaseDirectory;
            if (!Directory.Exists(root) || IsReparsePoint(root) || IsWithin(root, applicationRoot))
            {
                throw new InvalidOperationException("Production Secret root is unsafe");
            }

            string relative = Path.GetRelativePath(root, fullPath);
            if (relative == "." || Path.IsPathRooted(relative) || relative == ".." ||
                relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Secret file escapes the managed root");
            }

            RejectReparsePath(root, fullPath);
        }

        FileInfo info = new(fullPath);
        if (!info.Exists || info.Length == 0 || info.Length > maximumBytes || IsReparsePoint(fullPath))
        {
            throw new InvalidOperationException("Secret file is missing or invalid");
        }

        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode unsafePermissions = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                                             UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            if ((File.GetUnixFileMode(fullPath) & unsafePermissions) != 0)
            {
                throw new InvalidOperationException("Secret file permissions are too broad");
            }
        }

        using FileStream stream = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    private static void ValidatePasswordPrivateKey(string pem)
    {
        using RSA rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(pem);
            _ = rsa.ExportParameters(true);
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("Password encryption private key is invalid");
        }

        if (rsa.KeySize < 4096)
        {
            throw new InvalidOperationException("Password encryption private key must be RSA-4096 or stronger");
        }
    }

    private static void ValidatePasswordSources(bool requireManagedRoot)
    {
        bool legacy = HasValue("ET_ACCOUNT_PASSWORD_PRIVATE_KEY_FILE");
        bool ring = HasValue("ET_ACCOUNT_PASSWORD_KEYRING_FILE");
        if (legacy == ring) throw new InvalidOperationException("Exactly one password decryption key source is required");
        if (legacy)
        {
            ValidatePasswordPrivateKey(ReadManagedEnvironmentFile("ET_ACCOUNT_PASSWORD_PRIVATE_KEY_FILE", 16384, requireManagedRoot));
            return;
        }
        string json = ReadManagedEnvironmentFile("ET_ACCOUNT_PASSWORD_KEYRING_FILE", 65536, requireManagedRoot);
        _ = ReadPasswordKeyRing(json, null, false);
    }

    private static string ReadPasswordKeyRing(string json, string requestedKeyId, bool select)
    {
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 6 });
        JsonElement root = document.RootElement;
        if (root.GetProperty("SchemaVersion").GetInt32() != 1) throw new InvalidOperationException("Password key ring schema is unsupported");
        string activeKeyId = root.GetProperty("ActiveKeyId").GetString();
        string legacyKeyId = root.TryGetProperty("LegacyKeyId", out JsonElement legacyElement) && legacyElement.ValueKind != JsonValueKind.Null
            ? legacyElement.GetString() : null;
        JsonElement keys = root.GetProperty("Keys");
        if (!IsSafeKeyId(activeKeyId) || (legacyKeyId != null && !IsSafeKeyId(legacyKeyId)) ||
            keys.ValueKind != JsonValueKind.Array || keys.GetArrayLength() is < 1 or > 4)
            throw new InvalidOperationException("Password key ring metadata is invalid");

        Dictionary<string, string> privateKeys = new(StringComparer.Ordinal);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (JsonElement item in keys.EnumerateArray())
        {
            string id = item.GetProperty("KeyId").GetString();
            string pem = item.GetProperty("PrivateKeyPem").GetString();
            long notBefore = item.GetProperty("NotBefore").GetInt64();
            long notAfter = item.GetProperty("NotAfter").GetInt64();
            if (!IsSafeKeyId(id) || !privateKeys.TryAdd(id, pem) || notBefore > now + 300 || notAfter <= now || notAfter <= notBefore)
                throw new InvalidOperationException("Password key ring entry is invalid");
            ValidatePasswordPrivateKey(pem);
        }
        if (!privateKeys.ContainsKey(activeKeyId) || (legacyKeyId != null && !privateKeys.ContainsKey(legacyKeyId)))
            throw new InvalidOperationException("Password key ring selection is invalid");
        if (!select) return string.Empty;
        string selected = string.IsNullOrEmpty(requestedKeyId) ? legacyKeyId : requestedKeyId;
        if (selected == null || !privateKeys.TryGetValue(selected, out string privateKey))
            throw new InvalidOperationException("Password encryption key id is unknown");
        return privateKey;
    }

    private static void ValidateAdministrationTrust(string json, string expectedDatabase, string expectedDeployment, int expectedZone)
    {
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("Administration trust is empty");
        }

        HashSet<string> identities = new(StringComparer.Ordinal);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            string id = item.GetProperty("Id").GetString();
            string publicKey = item.GetProperty("PublicKeyPem").GetString();
            string database = item.GetProperty("Database").GetString();
            string deployment = item.GetProperty("DeploymentId").GetString();
            int zone = item.GetProperty("Zone").GetInt32();
            long expiresAt = item.GetProperty("ExpiresAt").GetInt64();
            JsonElement actions = item.GetProperty("Actions");
            if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || !identities.Add(id) || expiresAt <= now ||
                database != expectedDatabase || deployment != expectedDeployment || zone != expectedZone ||
                string.IsNullOrWhiteSpace(publicKey) || publicKey.Contains("PRIVATE KEY", StringComparison.Ordinal) ||
                actions.ValueKind != JsonValueKind.Array || actions.GetArrayLength() == 0 ||
                actions.EnumerateArray().Any(action => action.ValueKind != JsonValueKind.String ||
                    action.GetString() is not ("enable" or "disable" or "revoke" or "close" or "reset-password")))
            {
                throw new InvalidOperationException("Administration trust identity is invalid");
            }

            using RSA rsa = RSA.Create();
            rsa.ImportFromPem(publicKey);
            if (rsa.KeySize < 3072)
            {
                throw new InvalidOperationException("Administration trust key must be RSA-3072 or stronger");
            }
        }
    }

    private static void ValidateAdministrationUri(string connectionString)
    {
        MongoDB.Driver.MongoClientSettings settings;
        try
        {
            settings = MongoDB.Driver.MongoClientSettings.FromConnectionString(connectionString);
        }
        catch (Exception error) when (error is ArgumentException or FormatException)
        {
            throw new InvalidOperationException("Administration database configuration is invalid");
        }
        if (!settings.UseTls || settings.AllowInsecureTls || settings.Credential == null ||
            string.IsNullOrWhiteSpace(settings.ReplicaSetName))
        {
            throw new InvalidOperationException("Administration database requires authenticated verified TLS replica set");
        }
    }

    private static bool HasValue(string variable) => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable));

    private static bool IsReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool IsWithin(string candidate, string parent)
    {
        string normalizedParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return normalizedCandidate.StartsWith(normalizedParent, comparison);
    }

    private static void RejectReparsePath(string root, string file)
    {
        DirectoryInfo current = new FileInfo(file).Directory;
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        while (current != null && !current.FullName.Equals(root, comparison))
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("Secret path contains a reparse point");
            }
            current = current.Parent;
        }
        if (current == null)
        {
            throw new InvalidOperationException("Secret file escapes the managed root");
        }
    }
}
