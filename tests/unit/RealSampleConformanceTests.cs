using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Platform.Modules.Importation;

namespace Platform.UnitTests;

/// <summary>Aparece como ignorado, e nao como aprovado, enquanto nao houver arquivos reais em HUB_TEST_NATIVE_SAMPLES.</summary>
public sealed class RequiresRealSamplesFactAttribute : FactAttribute
{
    public RequiresRealSamplesFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(RealSampleSuite.Variable)))
        {
            Skip = $"Defina {RealSampleSuite.Variable} com a pasta das amostras. Veja tests/native-samples/README.md.";
        }
    }
}

public sealed record RealSampleExpectation(string File, string Line, string Model, string Firmware, string SourceFormat, Dictionary<string, long> Objects);

/// <summary>Confere o resultado do parser contra o que uma pessoa anotou em manifest.json. Devolve so nomes e contagens.</summary>
public static class RealSampleSuite
{
    public const string Variable = "HUB_TEST_NATIVE_SAMPLES";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<List<string>> CheckAsync(string directory)
    {
        var failures = new List<string>();
        var manifest = Path.Combine(directory, "manifest.json");
        if (!File.Exists(manifest))
        {
            return [$"manifest.json nao encontrado em {directory}"];
        }

        var samples = JsonSerializer.Deserialize<List<RealSampleExpectation>>(await File.ReadAllTextAsync(manifest), Json) ?? [];
        if (samples.Count == 0)
        {
            return ["manifest.json sem amostras: nada foi verificado"];
        }

        foreach (var sample in samples)
        {
            var label = $"{sample.File} ({sample.Line} {sample.Model} {sample.Firmware})";
            await using var content = new MemoryStream(await File.ReadAllBytesAsync(Path.Combine(directory, sample.File)));
            var inspection = await FileInspector.InspectAsync(content, new InspectionLimits(), CancellationToken.None);
            if (inspection.RejectionCode is { } code)
            {
                failures.Add($"{label}: recusado na inspecao ({code})");
                continue;
            }

            var analysis = await SourceAnalyzer.AnalyzeAsync(content, inspection.DetectedType!, new CsvLimits(), CancellationToken.None);
            if (analysis.SourceFormat != sample.SourceFormat)
            {
                failures.Add($"{label}: formato {analysis.SourceFormat}, esperado {sample.SourceFormat}");
            }

            if (analysis.ParserStatus != ParserStatuses.ParsedByDocumentation || analysis.Native is null)
            {
                failures.Add($"{label}: nao interpretado ({analysis.ParserStatus}, {analysis.Problem})");
                continue;
            }

            foreach (var (name, rows) in sample.Objects)
            {
                var actual = analysis.Native.Objects.FirstOrDefault(o => o.Name == name);
                if (actual?.Rows != rows)
                {
                    failures.Add($"{label}: {name} com {actual?.Rows ?? 0} linhas, esperado {rows}");
                }
                else if (actual.RowsWithErrors > 0)
                {
                    failures.Add($"{label}: {name} com {actual.RowsWithErrors} linhas com erro: {string.Join(",", actual.Errors.Keys)}");
                }
            }

            if (analysis.Native.UnknownSections > 0)
            {
                failures.Add($"{label}: {analysis.Native.UnknownSections} secoes fora da documentacao");
            }
        }

        return failures;
    }
}

public sealed class RealSampleConformanceTests
{
    [RequiresRealSamplesFact]
    public async Task Real_equipment_files_match_what_was_annotated()
    {
        var failures = await RealSampleSuite.CheckAsync(Environment.GetEnvironmentVariable(RealSampleSuite.Variable)!);

        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }
}

/// <summary>Testa a propria suite: sem isto, uma suite que nunca falha passaria por prova quando as amostras chegarem.</summary>
public sealed class RealSampleSuiteTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("hub-samples-").FullName;

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private void WriteZip(string name, string csvName, string csv)
    {
        using var archive = ZipFile.Open(Path.Combine(directory, name), ZipArchiveMode.Create);
        using var stream = archive.CreateEntry(csvName).Open();
        stream.Write(Encoding.UTF8.GetBytes(csv));
    }

    private void WriteManifest(string json) => File.WriteAllText(Path.Combine(directory, "manifest.json"), json);

    private const string Expectation = """[{"file":"a.zip","line":"v5","model":"iDFlex","firmware":"x","sourceFormat":"controlid_users_zip","objects":{"users":2}}]""";

    [Fact]
    public async Task Matching_file_has_no_failures()
    {
        WriteZip("a.zip", "users.csv", "users\nid;name\n1;Ana\n2;Bia\n");
        WriteManifest(Expectation);

        Assert.Empty(await RealSampleSuite.CheckAsync(directory));
    }

    [Theory]
    [InlineData("users.csv", "users\nid;name\n1;Ana\n", "1 linhas")]
    [InlineData("sync.csv", "users\nid;name\n1;Ana\n2;Bia\n", "formato")]
    [InlineData("users.csv", "colunas;que;mudaram\n1;2;3\n", "nao interpretado")]
    [InlineData("users.csv", "users\nid;name\nx;Ana\n2;Bia\n", "invalid_integer")]
    [InlineData("users.csv", "users\nid;name\n1;Ana\n2;Bia\nnova\na;b\n1;2\n", "secoes fora")]
    public async Task Any_difference_from_the_annotation_is_a_failure(string csvName, string csv, string expected)
    {
        WriteZip("a.zip", csvName, csv);
        WriteManifest(Expectation);

        var failures = await RealSampleSuite.CheckAsync(directory);

        Assert.Contains(failures, f => f.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Failure_messages_do_not_carry_cell_values()
    {
        WriteZip("a.zip", "users.csv", "users\nid;name\nMARCADOR;Ana\n");
        WriteManifest(Expectation);

        Assert.DoesNotContain(await RealSampleSuite.CheckAsync(directory), f => f.Contains("MARCADOR", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("[]")]
    public async Task Missing_or_empty_manifest_is_a_failure_not_a_pass(string? manifest)
    {
        if (manifest is not null)
        {
            WriteManifest(manifest);
        }

        Assert.NotEmpty(await RealSampleSuite.CheckAsync(directory));
    }
}
