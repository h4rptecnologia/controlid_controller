using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Platform.Modules.DeviceIntegration.ControlId;

/// <summary>
/// Como confiar no equipamento. O guia de hardening do iDFace preve certificado autoassinado pelo terminal ou
/// fornecido pelo integrador, entao a validacao padrao sozinha nao basta. Nao existe modo "aceitar qualquer".
/// Este e o unico arquivo do repositorio autorizado a definir validacao de certificado (RepositoryGuardTests).
/// </summary>
public sealed class DeviceTlsPolicy
{
    private DeviceTlsPolicy(string mode, byte[]? pinnedSha256, X509Certificate2? privateRoot)
    {
        Mode = mode;
        PinnedSha256 = pinnedSha256;
        PrivateRoot = privateRoot;
    }

    internal string Mode { get; }

    internal byte[]? PinnedSha256 { get; }

    internal X509Certificate2? PrivateRoot { get; }

    /// <summary>Cadeia publica do sistema operacional, com verificacao de nome.</summary>
    public static DeviceTlsPolicy SystemTrust() => new(nameof(SystemTrust), null, null);

    /// <summary>CA privada da instalacao. Cadeia e nome sao verificados.</summary>
    public static DeviceTlsPolicy PrivateCa(X509Certificate2 root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return new DeviceTlsPolicy(nameof(PrivateCa), null, root);
    }

    /// <summary>
    /// Exatamente este certificado, identificado pelo SHA-256 do DER. Serve para o autoassinado do terminal, cujo
    /// nome pode nao corresponder ao endereco: a identidade vem da igualdade byte a byte. Vencido e recusado.
    /// </summary>
    public static DeviceTlsPolicy PinnedCertificate(string sha256Hex)
    {
        var pin = Convert.FromHexString(sha256Hex.Replace(":", string.Empty, StringComparison.Ordinal));
        return pin.Length == SHA256.HashSizeInBytes
            ? new DeviceTlsPolicy(nameof(PinnedCertificate), pin, null)
            : throw new ArgumentException("O pin deve ser um SHA-256.", nameof(sha256Hex));
    }

    internal void Apply(SslClientAuthenticationOptions options)
    {
        options.EnabledSslProtocols = System.Security.Authentication.SslProtocols.None; // Padrao seguro do sistema.
        options.CertificateRevocationCheckMode = X509RevocationMode.NoCheck; // LAN isolada, sem acesso a CRL/OCSP.

        if (PrivateRoot is not null)
        {
            var chain = new X509ChainPolicy
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust,
                RevocationMode = X509RevocationMode.NoCheck,
            };
            chain.CustomTrustStore.Add(PrivateRoot);
            options.CertificateChainPolicy = chain;
        }
        else if (PinnedSha256 is not null)
        {
            var pin = PinnedSha256;
            options.RemoteCertificateValidationCallback = (_, certificate, _, _) => MatchesPin(certificate, pin);
        }
    }

    private static bool MatchesPin(X509Certificate? certificate, byte[] pin)
    {
        if (certificate is null)
        {
            return false;
        }

        var actual = SHA256.HashData(certificate.GetRawCertData());
        if (!CryptographicOperations.FixedTimeEquals(actual, pin))
        {
            return false;
        }

        using var parsed = new X509Certificate2(certificate);
        var now = DateTime.Now;
        return parsed.NotBefore <= now && now <= parsed.NotAfter;
    }
}
