using System.IO.Compression;
using System.Text;
using Platform.Modules.Importation;

namespace Platform.IntegrationTests;

/// <summary>Aparece como ignorado, e nao como aprovado, quando nao ha um ClamAV real configurado em HUB_TEST_CLAMAV.</summary>
public sealed class RequiresClamAvFactAttribute : FactAttribute
{
    public RequiresClamAvFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HUB_TEST_CLAMAV")))
        {
            Skip = "Defina HUB_TEST_CLAMAV=host:porta para rodar contra um ClamAV real.";
        }
    }
}

public sealed class ClamAvRealTests
{
    // Arquivo de teste padrao da EICAR, inofensivo. Montado aqui para o codigo-fonte nao ser sinalizado por antivirus.
    private static byte[] Eicar() => Encoding.ASCII.GetBytes(string.Concat(
        "X5O!P%@AP[4\\PZX54(P^)7CC)7}$",
        "EICAR-STANDARD-ANTIVIRUS",
        "-TEST-FILE!$H+H*"));

    private static ClamAvScanner Scanner()
    {
        var parts = Environment.GetEnvironmentVariable("HUB_TEST_CLAMAV")!.Split(':');
        return new ClamAvScanner(parts[0], int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
    }

    [RequiresClamAvFact]
    public async Task Real_scanner_detects_the_eicar_test_file()
    {
        var result = await Scanner().ScanAsync(new MemoryStream(Eicar()), CancellationToken.None);

        Assert.Equal(ScanVerdict.Infected, result.Verdict);
        Assert.Contains("Eicar", result.Signature, StringComparison.OrdinalIgnoreCase);
    }

    [RequiresClamAvFact]
    public async Task Real_scanner_detects_the_test_file_inside_a_zip()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = archive.CreateEntry("user_images/1.jpg").Open();
            entry.Write(Eicar());
        }

        var result = await Scanner().ScanAsync(buffer, CancellationToken.None);

        Assert.Equal(ScanVerdict.Infected, result.Verdict);
    }

    [RequiresClamAvFact]
    public async Task Real_scanner_passes_a_clean_file()
    {
        var result = await Scanner().ScanAsync(new MemoryStream("id;name\n1;Ana\n"u8.ToArray()), CancellationToken.None);

        Assert.Equal(ScanVerdict.Clean, result.Verdict);
    }
}
