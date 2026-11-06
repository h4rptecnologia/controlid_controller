using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Platform.EdgeGateway;

/// <summary>
/// Identidade da instalacao. A chave privada e gerada aqui e nunca sai desta maquina; a central so conhece o hash
/// da chave publica. Protecao em repouso: permissao de arquivo. Em producao deve ficar em TPM ou no cofre do SO.
/// </summary>
public static class GatewayIdentity
{
    private static readonly TimeSpan Validity = TimeSpan.FromDays(365);

    public static X509Certificate2 LoadOrCreate(string path)
    {
        if (File.Exists(path))
        {
            return X509CertificateLoader.LoadPkcs12FromFile(path, null);
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=controlid-hub-gateway", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], false)); // clientAuth
        var now = DateTimeOffset.UtcNow;
        using var created = request.CreateSelfSigned(now.AddMinutes(-5), now.Add(Validity));

        var pfx = created.Export(X509ContentType.Pfx);
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var file = new FileStream(path, options))
            {
                file.Write(pfx);
            }

            return X509CertificateLoader.LoadPkcs12(pfx, null);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
    }
}
