using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace ET;

public static class SecretLeasePolicy
{
    private const long MaximumSecretBytes = 1024 * 1024;

    [StaticField]
    private static readonly string[] Variables =
    [
        "ET_SECRET_LEASE_FILE", "ET_SECRET_LEASE_SIGNATURE_FILE", "ET_SECRET_LEASE_TRUST_FILE",
        "ET_SECRET_LEASE_SIGNING_KEY_ID", "ET_DEPLOYMENT_ID",
    ];

    public static void ValidateConfigured(bool production)
    {
        bool required = Environment.GetEnvironmentVariable("ET_SECRET_LEASE_REQUIRED") == "1";
        int configured = 0;
        foreach (string variable in Variables)
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable))) ++configured;
        }
        if (configured != 0 && configured != Variables.Length)
            throw new InvalidOperationException("Secret lease configuration is incomplete");
        if (production && required && configured != Variables.Length)
            throw new InvalidOperationException("Production Secret lease is required");
        if (configured == Variables.Length) _ = ReadAndValidate();
    }

    public static void AssertFileAuthorized(string file)
    {
        if (Environment.GetEnvironmentVariable("ET_SECRET_LEASE_REQUIRED") != "1" &&
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ET_SECRET_LEASE_FILE"))) return;
        LeaseResult lease = ReadAndValidate();
        string requested = Path.GetFullPath(file);
        if (!lease.Files.Contains(requested))
            throw new InvalidOperationException("Secret file is not authorized by the active lease");
    }

    private static LeaseResult ReadAndValidate()
    {
        string rootValue = Environment.GetEnvironmentVariable("ET_SECRET_ROOT");
        if (string.IsNullOrWhiteSpace(rootValue) || !Path.IsPathFullyQualified(rootValue))
            throw new InvalidOperationException("Secret lease requires a managed Secret root");
        string root = Path.GetFullPath(rootValue).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string leaseFile = ResolveInfrastructureFile(root, "ET_SECRET_LEASE_FILE");
        string signatureFile = ResolveInfrastructureFile(root, "ET_SECRET_LEASE_SIGNATURE_FILE");
        string trustFile = ResolveInfrastructureFile(root, "ET_SECRET_LEASE_TRUST_FILE");
        byte[] leaseBytes = ReadBounded(leaseFile, 262144); byte[] signature = ReadBounded(signatureFile, 8192);
        string trustPem = System.Text.Encoding.UTF8.GetString(ReadBounded(trustFile, 16384));
        using RSA rsa = RSA.Create();
        try { rsa.ImportFromPem(trustPem); }
        catch (CryptographicException) { throw new InvalidOperationException("Secret lease trust key is invalid"); }
        if (rsa.KeySize < 3072 || trustPem.Contains("PRIVATE KEY", StringComparison.Ordinal) ||
            !rsa.VerifyData(leaseBytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new InvalidOperationException("Secret lease signature is not trusted");
        string signingFingerprint = Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
        using JsonDocument document = JsonDocument.Parse(leaseBytes, new JsonDocumentOptions { MaxDepth = 8 });
        JsonElement value = document.RootElement;
        string leaseId = value.GetProperty("LeaseId").GetString();
        string provider = value.GetProperty("Provider").GetString();
        int generation = value.GetProperty("Generation").GetInt32();
        if (value.GetProperty("SchemaVersion").GetInt32() != 1 || value.GetProperty("Product").GetString() != "ET.Server" ||
            value.GetProperty("ArtifactType").GetString() != "SecretLease" ||
            !IsIdentifier(leaseId) || string.IsNullOrWhiteSpace(provider) || provider.Length > 128 || generation < 1 ||
            value.GetProperty("DeploymentId").GetString() != Environment.GetEnvironmentVariable("ET_DEPLOYMENT_ID") ||
            value.GetProperty("SigningKeyId").GetString() != Environment.GetEnvironmentVariable("ET_SECRET_LEASE_SIGNING_KEY_ID") ||
            value.GetProperty("SigningPublicKeySha256").GetString() != signingFingerprint)
            throw new InvalidOperationException("Secret lease identity is invalid");
        DateTimeOffset issued = DateTimeOffset.Parse(value.GetProperty("IssuedAtUtc").GetString());
        DateTimeOffset expires = DateTimeOffset.Parse(value.GetProperty("ExpiresAtUtc").GetString());
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (issued > now.AddMinutes(5) || expires <= now || expires <= issued || expires - issued > TimeSpan.FromHours(24))
            throw new InvalidOperationException("Secret lease validity window is invalid");
        JsonElement entries = value.GetProperty("Secrets");
        if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() is < 1 or > 64)
            throw new InvalidOperationException("Secret lease inventory is invalid");
        HashSet<string> files = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (JsonElement entry in entries.EnumerateArray())
        {
            string relative = entry.GetProperty("RelativePath").GetString();
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathFullyQualified(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidOperationException("Secret lease path is invalid");
            string path = Path.GetFullPath(Path.Combine(root, relative)); EnsureWithin(root, path); RejectReparse(root, path);
            FileInfo info = new(path); long expectedSize = entry.GetProperty("Size").GetInt64(); string expectedHash = entry.GetProperty("Sha256").GetString();
            if (!files.Add(path) || !info.Exists || expectedSize is <= 0 or > MaximumSecretBytes || info.Length != expectedSize || expectedHash?.Length != 64 ||
                !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expectedHash), SHA256.HashData(File.ReadAllBytes(path))))
                throw new InvalidOperationException("Secret lease content does not match inventory");
        }
        return new LeaseResult(files);
    }

    private static string ResolveInfrastructureFile(string root, string variable)
    {
        string value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) throw new InvalidOperationException("Secret lease path is invalid");
        string path = Path.GetFullPath(value); EnsureWithin(root, path); RejectReparse(root, path); return path;
    }
    private static byte[] ReadBounded(string path, int maximum)
    {
        FileInfo info = new(path); if (!info.Exists || info.Length <= 0 || info.Length > maximum) throw new InvalidOperationException("Secret lease file is missing or invalid");
        return File.ReadAllBytes(path);
    }
    private static void EnsureWithin(string root, string path)
    {
        string prefix = root + Path.DirectorySeparatorChar; StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(prefix, comparison)) throw new InvalidOperationException("Secret lease path escapes managed root");
    }
    private static bool IsIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length is < 3 or > 128 || !char.IsLetterOrDigit(value[0])) return false;
        foreach (char character in value)
        {
            if (!char.IsLetterOrDigit(character) && character is not '.' and not '_' and not ':' and not '-') return false;
        }
        return true;
    }
    private static void RejectReparse(string root, string path)
    {
        if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Secret lease root is unsafe");
        DirectoryInfo current = new FileInfo(path).Directory;
        while (current != null && !current.FullName.Equals(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        { if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Secret lease path contains a reparse point"); current = current.Parent; }
        if (current == null || (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)) throw new InvalidOperationException("Secret lease path is unsafe");
    }
    private sealed record LeaseResult(HashSet<string> Files);
}
