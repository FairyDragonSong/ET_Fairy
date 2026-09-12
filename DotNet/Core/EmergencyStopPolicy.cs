using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ET;

public sealed class EmergencyStopEnvelope
{
    public int SchemaVersion { get; set; }
    public string KeyId { get; set; }
    public string Payload { get; set; }
    public string Signature { get; set; }
}

public sealed class EmergencyStopCommand
{
    public int SchemaVersion { get; set; }
    public string Action { get; set; }
    public string DeploymentId { get; set; }
    public string OperationId { get; set; }
    public string ReasonCode { get; set; }
    public long IssuedAt { get; set; }
    public long ExpiresAt { get; set; }
}

public sealed class EmergencyStopTrustEntry
{
    public string KeyId { get; set; }
    public string PublicKeyPem { get; set; }
    public string DeploymentId { get; set; }
    public long NotBefore { get; set; }
    public long NotAfter { get; set; }
}

public sealed record EmergencyStopSignal(string OperationId, string ReasonCode);

public static class EmergencyStopPolicy
{
    public static void ValidateTrust(string trustJson, string expectedDeployment, long now)
    {
        try
        {
            _ = ReadTrust(trustJson, expectedDeployment, now);
        }
        catch (Exception error) when (error is JsonException or CryptographicException or InvalidOperationException or ArgumentException)
        {
            throw new InvalidOperationException("Emergency stop trust is invalid");
        }
    }

    public static EmergencyStopSignal Verify(string envelopeJson, string trustJson, string expectedDeployment, long now)
    {
        try
        {
            if (!IsSafeIdentifier(expectedDeployment, 3, 80)) throw new InvalidOperationException();
            EmergencyStopEnvelope envelope = JsonSerializer.Deserialize<EmergencyStopEnvelope>(envelopeJson,
                new JsonSerializerOptions { MaxDepth = 6 });
            if (envelope?.SchemaVersion != 1 || !ProductionSecretPolicy.IsSafeKeyId(envelope.KeyId) ||
                string.IsNullOrWhiteSpace(envelope.Payload) || string.IsNullOrWhiteSpace(envelope.Signature))
                throw new InvalidOperationException();

            EmergencyStopTrustEntry[] trust = ReadTrust(trustJson, expectedDeployment, now);
            EmergencyStopTrustEntry identity = trust.SingleOrDefault(entry => entry.KeyId == envelope.KeyId);
            if (identity == null || identity.NotBefore > now + 300 || identity.NotAfter <= now)
                throw new InvalidOperationException();

            byte[] payload = Convert.FromBase64String(envelope.Payload);
            byte[] signature = Convert.FromBase64String(envelope.Signature);
            if (payload.Length is < 32 or > 16384 || signature.Length is < 256 or > 2048) throw new InvalidOperationException();
            using RSA rsa = RSA.Create();
            rsa.ImportFromPem(identity.PublicKeyPem);
            if (rsa.KeySize < 3072 || !rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                throw new InvalidOperationException();

            EmergencyStopCommand command = JsonSerializer.Deserialize<EmergencyStopCommand>(payload,
                new JsonSerializerOptions { MaxDepth = 4 });
            if (command?.SchemaVersion != 1 || command.Action != "stop" || command.DeploymentId != expectedDeployment ||
                !Guid.TryParseExact(command.OperationId, "N", out _) || !IsSafeIdentifier(command.ReasonCode, 3, 64) ||
                command.IssuedAt > now + 300 || command.ExpiresAt <= now || command.ExpiresAt <= command.IssuedAt ||
                command.ExpiresAt - command.IssuedAt > 86400)
                throw new InvalidOperationException();
            return new EmergencyStopSignal(command.OperationId, command.ReasonCode);
        }
        catch (Exception error) when (error is JsonException or FormatException or CryptographicException or InvalidOperationException or ArgumentException)
        {
            throw new InvalidOperationException("Emergency stop command is invalid");
        }
    }

    private static EmergencyStopTrustEntry[] ReadTrust(string trustJson, string expectedDeployment, long now)
    {
        if (!IsSafeIdentifier(expectedDeployment, 3, 80)) throw new InvalidOperationException();
        EmergencyStopTrustEntry[] trust = JsonSerializer.Deserialize<EmergencyStopTrustEntry[]>(trustJson,
            new JsonSerializerOptions { MaxDepth = 6 });
        if (trust == null || trust.Length is < 1 or > 4 || trust.Any(entry => entry == null) ||
            trust.GroupBy(entry => entry.KeyId, StringComparer.Ordinal).Any(group => group.Count() != 1))
            throw new InvalidOperationException();
        bool active = false;
        foreach (EmergencyStopTrustEntry entry in trust)
        {
            if (!ProductionSecretPolicy.IsSafeKeyId(entry.KeyId) || entry.DeploymentId != expectedDeployment ||
                entry.NotAfter <= now || entry.NotAfter <= entry.NotBefore || entry.NotBefore > now + 86400 ||
                string.IsNullOrWhiteSpace(entry.PublicKeyPem) || entry.PublicKeyPem.Contains("PRIVATE KEY", StringComparison.Ordinal))
                throw new InvalidOperationException();
            using RSA rsa = RSA.Create();
            rsa.ImportFromPem(entry.PublicKeyPem);
            if (rsa.KeySize < 3072) throw new InvalidOperationException();
            active |= entry.NotBefore <= now + 300;
        }
        if (!active) throw new InvalidOperationException();
        return trust;
    }

    private static bool IsSafeIdentifier(string value, int minimum, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length >= minimum && value.Length <= maximum &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
}

public sealed class EmergencyStopMonitor
{
    private readonly string commandFile;
    private readonly string trustFile;
    private readonly string deploymentId;
    private readonly bool requireManagedRoot;
    private long nextCheckTicks;

    private EmergencyStopMonitor(string commandFile, string trustFile, string deploymentId, bool requireManagedRoot)
    {
        this.commandFile = commandFile;
        this.trustFile = trustFile;
        this.deploymentId = deploymentId;
        this.requireManagedRoot = requireManagedRoot;
    }

    public static EmergencyStopMonitor FromEnvironment(Options options)
    {
        string command = Environment.GetEnvironmentVariable("ET_EMERGENCY_STOP_FILE");
        string trust = Environment.GetEnvironmentVariable("ET_EMERGENCY_STOP_TRUST_FILE");
        string deployment = Environment.GetEnvironmentVariable("ET_DEPLOYMENT_ID");
        bool any = !string.IsNullOrWhiteSpace(command) || !string.IsNullOrWhiteSpace(trust) || !string.IsNullOrWhiteSpace(deployment);
        if (!any) return new EmergencyStopMonitor(null, null, null, false);
        if (string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(trust) || string.IsNullOrWhiteSpace(deployment) ||
            !Path.IsPathFullyQualified(command) || !Path.IsPathFullyQualified(trust))
            throw new InvalidOperationException("Emergency stop configuration is incomplete");
        string fullCommand = Path.GetFullPath(command);
        string commandDirectory = Path.GetDirectoryName(fullCommand);
        if (string.IsNullOrWhiteSpace(commandDirectory) || !Directory.Exists(commandDirectory))
            throw new InvalidOperationException("Emergency stop command directory does not exist");
        bool production = options?.Develop == 0;
        if (production)
        {
            string application = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (fullCommand.StartsWith(application, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Emergency stop command must be outside the application directory");
            for (DirectoryInfo directory = new(commandDirectory); directory != null; directory = directory.Parent)
            {
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Emergency stop command directory cannot use reparse points");
            }
        }
        string trustJson = ProductionSecretPolicy.ReadManagedFile(Path.GetFullPath(trust), 131072, production);
        EmergencyStopPolicy.ValidateTrust(trustJson, deployment, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        return new EmergencyStopMonitor(fullCommand, Path.GetFullPath(trust), deployment, production);
    }

    public EmergencyStopSignal Check(bool force = false)
    {
        if (this.commandFile == null) return null;
        long nowTicks = Environment.TickCount64;
        if (!force && nowTicks < this.nextCheckTicks) return null;
        this.nextCheckTicks = nowTicks + 1000;
        if (!File.Exists(this.commandFile)) return null;
        string command = ProductionSecretPolicy.ReadManagedFile(this.commandFile, 65536, false);
        string trust = ProductionSecretPolicy.ReadManagedFile(this.trustFile, 131072, this.requireManagedRoot);
        return EmergencyStopPolicy.Verify(command, trust, this.deploymentId, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }
}
