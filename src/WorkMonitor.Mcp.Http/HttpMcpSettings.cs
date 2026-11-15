using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace WorkMonitor.Mcp.Http;

public sealed class HttpMcpSettings : IDisposable
{
    public const string Section = "WorkMonitor:HttpMcp";
    public required string TenantId { get; init; }
    public required string ClientId { get; init; }
    public required string RequiredScope { get; init; }
    public required string DataDirectory { get; init; }
    public required IReadOnlyList<string> AllowedHosts { get; init; }
    public string Authority => $"https://login.microsoftonline.com/{TenantId}/v2.0";
    public string ApiScope => $"api://{ClientId}/{RequiredScope}";
    internal X509Certificate2? Certificate { get; init; }
    internal string? ClientSecret { get; init; }

    public static HttpMcpSettings Load(IConfiguration configuration)
    {
        var section = configuration.GetSection(Section);
        var allowedHosts = (configuration["AllowedHosts"] ?? "localhost;127.0.0.1;[::1]")
            .Split(';', StringSplitOptions.TrimEntries);
        if (configuration.GetSection("AllowedHosts").GetChildren().Any() ||
            allowedHosts.Any(host => !IsExactHost(host)))
            throw new InvalidOperationException(
                "AllowedHosts must contain only exact DNS host names or IP addresses separated by semicolons, without schemes, ports, empty entries, or wildcards. Use loopback hosts locally and approved exact host names in production.");
        string RequiredGuid(string name)
            => Guid.TryParse(section[name], out var value) && value != Guid.Empty
                ? value.ToString("D")
                : throw new InvalidOperationException($"{Section}:{name} must be a non-empty GUID.");
        var tenantId = RequiredGuid("TenantId");
        var clientId = RequiredGuid("ClientId");
        var scope = section["RequiredScope"];
        if (string.IsNullOrWhiteSpace(scope) || scope.Length > 128 ||
            scope.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-' or '.')))
            throw new InvalidOperationException($"{Section}:RequiredScope must be one delegated scope name.");
        var directory = section["DataDirectory"];
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            throw new InvalidOperationException($"{Section}:DataDirectory must be an absolute host-owned catalog directory.");
        var certificatePath = section["CertificatePath"];
        var secret = section["ClientSecret"];
        if (string.IsNullOrWhiteSpace(certificatePath) == string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException(
                $"Configure exactly one confidential-client credential: {Section}:CertificatePath (preferred) or ClientSecret.");
        X509Certificate2? certificate = null;
        if (!string.IsNullOrWhiteSpace(certificatePath))
        {
            if (!Path.IsPathFullyQualified(certificatePath))
                throw new InvalidOperationException($"{Section}:CertificatePath must be an absolute mounted certificate path.");
            try
            {
                certificate = new X509Certificate2(certificatePath, section["CertificatePassword"],
                    X509KeyStorageFlags.EphemeralKeySet);
                using var key = certificate.GetRSAPrivateKey();
                if (key == null || key.KeySize < 2048 || certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow ||
                    certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
                    throw new CryptographicException();
            }
            catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
            {
                certificate?.Dispose();
                throw new InvalidOperationException(
                    $"{Section}:CertificatePath must contain a currently valid RSA certificate with an accessible private key. Check the mounted file and password.");
            }
        }
        return new HttpMcpSettings
        {
            TenantId = tenantId, ClientId = clientId, RequiredScope = scope, DataDirectory = directory,
            Certificate = certificate, ClientSecret = secret, AllowedHosts = allowedHosts
        };
    }

    private static bool IsExactHost(string host)
        => host is not ("0.0.0.0" or "[::]") && (Uri.CheckHostName(host) switch
        {
            UriHostNameType.Dns => host.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '.'),
            UriHostNameType.IPv4 => true,
            UriHostNameType.IPv6 => IPAddress.TryParse(host, out var address) &&
                !address.Equals(IPAddress.IPv6Any) &&
                string.Equals(host, $"[{address}]", StringComparison.OrdinalIgnoreCase),
            _ => false
        });

    public void Dispose() => Certificate?.Dispose();
}
