using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Platform.UnitTests;

/// <summary>Traduz restricoes irrenunciaveis do AGENTS.md em falha de build.</summary>
public sealed partial class RepositoryGuardTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ControlIdHub.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositorio nao encontrada.");
    }

    private static IEnumerable<string> SourceFiles(string relativeDir, params string[] extensions)
    {
        var dir = Path.Combine(Root, relativeDir);
        if (!Directory.Exists(dir))
        {
            return [];
        }

        return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => extensions.Contains(Path.GetExtension(f)))
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj" or "node_modules" or "dist"));
    }

    [Fact]
    public void Fcgi_routes_only_exist_inside_the_controlid_module()
    {
        var offenders = SourceFiles("src", ".cs")
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}DeviceIntegration.ControlId{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains(".fcgi", StringComparison.OrdinalIgnoreCase));

        Assert.Empty(offenders);
    }

    [Fact]
    public void Browser_code_never_references_device_routes()
    {
        var offenders = SourceFiles("web/src", ".ts", ".tsx")
            .Where(f => File.ReadAllText(f).Contains(".fcgi", StringComparison.OrdinalIgnoreCase));

        Assert.Empty(offenders);
    }

    [Fact]
    public void Browser_code_never_uses_web_storage()
    {
        // Sessao so em cookie HttpOnly (docs/02-arquitetura.md).
        var offenders = SourceFiles("web/src", ".ts", ".tsx")
            .Where(f => File.ReadAllText(f).Contains("localStorage", StringComparison.Ordinal)
                || File.ReadAllText(f).Contains("sessionStorage", StringComparison.Ordinal));

        Assert.Empty(offenders);
    }

    [Fact]
    public void Tls_validation_is_never_disabled()
    {
        // Unica excecao: o pin compara o certificado inteiro com o SHA-256 cadastrado, sem aceitar outro.
        var offenders = SourceFiles("src", ".cs")
            .Where(f => Path.GetFileName(f) != "DeviceTlsPolicy.cs")
            .Where(f => TlsBypass().IsMatch(File.ReadAllText(f)));

        Assert.Empty(offenders);

        // Certificado de gateway: aceito no handshake e conferido contra a identidade em cada requisicao.
        var clientValidation = SourceFiles("src", ".cs")
            .Where(f => File.ReadAllText(f).Contains("ClientCertificateValidation", StringComparison.Ordinal))
            .Select(Path.GetFileName);
        Assert.Equal(["EdgeListener.cs"], clientValidation);
    }

    [Fact]
    public void Pinned_certificate_check_only_accepts_an_exact_match()
    {
        var source = File.ReadAllText(SourceFiles("src", ".cs").Single(f => Path.GetFileName(f) == "DeviceTlsPolicy.cs"));

        Assert.Contains("CryptographicOperations.FixedTimeEquals(actual, pin)", source, StringComparison.Ordinal);
        Assert.Single(TlsCallback().Matches(source));
        Assert.DoesNotContain("=> true", source, StringComparison.Ordinal);
        Assert.DoesNotContain("return true;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Vendor_secrets_never_get_mapped_from_system_information()
    {
        var offenders = SourceFiles("src", ".cs")
            .Where(f => File.ReadAllText(f).Contains("iDCloud_code\"", StringComparison.Ordinal));

        Assert.Empty(offenders);
    }

    [Fact]
    public void Modules_only_depend_on_shared_projects()
    {
        var violations = new List<string>();
        foreach (var project in SourceFiles("src/Modules", ".csproj"))
        {
            var references = XDocument.Load(project).Descendants("ProjectReference")
                .Select(r => r.Attribute("Include")?.Value ?? string.Empty);
            violations.AddRange(references
                .Where(r => !r.Replace('\\', '/').Contains("/Shared/", StringComparison.Ordinal))
                .Select(r => $"{Path.GetFileName(project)} -> {r}"));
        }

        Assert.Empty(violations);
    }

    [GeneratedRegex(@"ServerCertificateCustomValidationCallback|DangerousAcceptAnyServerCertificateValidator|RemoteCertificateValidationCallback|TrustServerCertificate\s*=\s*true", RegexOptions.IgnoreCase)]
    private static partial Regex TlsBypass();

    [GeneratedRegex(@"RemoteCertificateValidationCallback\s*=")]
    private static partial Regex TlsCallback();
}
