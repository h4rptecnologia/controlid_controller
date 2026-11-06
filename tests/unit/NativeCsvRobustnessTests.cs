using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Platform.Modules.Importation;

namespace Platform.UnitTests;

/// <summary>Entrada aleatoria com semente fixa: uma falha aqui se repete. O resultado precisa ser sempre um status, nunca excecao.</summary>
public sealed class NativeCsvRobustnessTests
{
    private const string Valid = "users\nid;registration;name;begin_time;end_time\n1;A1;Ana;0;100\n2;A2;\"Silva; Bia\";5;4\ncards\nid;value;user_id\n3;18446744073709551615;1\n4;42;9\ntemplates\nid;template;user_id\n5;QUJD;1\n";

    private static readonly string[] Allowed = [ParserStatuses.ParsedByDocumentation, ParserStatuses.NeedsManualMapping, ParserStatuses.Unreadable];

    private static MemoryStream Zip(string name, byte[] content)
    {
        var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var stream = archive.CreateEntry(name).Open();
            stream.Write(content);
        }

        return buffer;
    }

    private static async Task AssertHandledAsync(byte[] content, string name)
    {
        var zipped = await SourceAnalyzer.AnalyzeAsync(Zip(name, content), DetectedTypes.Zip, new CsvLimits(), CancellationToken.None);
        var bare = await SourceAnalyzer.AnalyzeAsync(new MemoryStream(content), DetectedTypes.DelimitedText, new CsvLimits(), CancellationToken.None);

        Assert.Contains(zipped.ParserStatus, Allowed);
        Assert.Contains(bare.ParserStatus, Allowed);
    }

    [Theory]
    [InlineData("users.csv")]
    [InlineData("sync.csv")]
    [InlineData("backup.csv")]
    [InlineData("logs.csv")]
    public async Task Corrupted_valid_files_always_end_in_a_status(string name)
    {
        var random = new Random(20261106);
        var original = Encoding.UTF8.GetBytes(Valid);
        byte[] alphabet = [(byte)';', (byte)',', (byte)'\t', (byte)'"', (byte)'\n', (byte)'\r', 0, 0xC3, 0xFF, 0xFE, (byte)'=', (byte)'-'];

        for (var iteration = 0; iteration < 400; iteration++)
        {
            var content = (byte[])original.Clone();
            for (var change = random.Next(1, 12); change > 0; change--)
            {
                content[random.Next(content.Length)] = random.Next(2) == 0 ? alphabet[random.Next(alphabet.Length)] : (byte)random.Next(256);
            }

            await AssertHandledAsync(content[..random.Next(content.Length + 1)], name);
        }
    }

    [Fact]
    public async Task Random_bytes_always_end_in_a_status()
    {
        var random = new Random(20261107);
        for (var iteration = 0; iteration < 400; iteration++)
        {
            var content = new byte[random.Next(0, 2048)];
            random.NextBytes(content);

            await AssertHandledAsync(content, "users.csv");
        }
    }

    [Fact]
    public async Task Corrupted_file_never_echoes_a_marker_into_the_analysis()
    {
        var random = new Random(20261108);
        var original = Encoding.UTF8.GetBytes("users\nid;name;MARCADORC\n1;MARCADORV;MARCADORE\ncards\nid;value\nMARCADORI;MARCADORX\n");

        for (var iteration = 0; iteration < 400; iteration++)
        {
            var content = (byte[])original.Clone();
            for (var change = random.Next(1, 6); change > 0; change--)
            {
                // Mantem as letras do marcador: troca so os separadores, para ele cair em lugares inesperados do layout.
                var position = random.Next(content.Length);
                if (content[position] is (byte)';' or (byte)'\n')
                {
                    content[position] = random.Next(2) == 0 ? (byte)';' : (byte)'\n';
                }
            }

            var result = await SourceAnalyzer.AnalyzeAsync(Zip("users.csv", content), DetectedTypes.Zip, new CsvLimits(), CancellationToken.None);

            Assert.DoesNotContain("MARCADOR", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Marker_that_looks_like_an_identifier_never_reaches_the_analysis()
    {
        var csv = Encoding.UTF8.GetBytes("users\nid;name\n1;Ana\nmarcador_pessoal\nmarcador_a;marcador_b\n1;2\n");

        var result = await SourceAnalyzer.AnalyzeAsync(Zip("users.csv", csv), DetectedTypes.Zip, new CsvLimits(), CancellationToken.None);

        Assert.DoesNotContain("marcador", JsonSerializer.Serialize(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Field_at_the_size_limit_does_not_exhaust_the_section_detection()
    {
        var big = new string('a', 60_000);

        var stopwatch = Stopwatch.StartNew();
        var result = await SourceAnalyzer.AnalyzeAsync(Zip("users.csv", Encoding.UTF8.GetBytes($"{big}\n{big};{big}\n{big}\n")), DetectedTypes.Zip, new CsvLimits(), CancellationToken.None);

        Assert.Contains(result.ParserStatus, Allowed);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Large_file_finishes_with_exact_counts()
    {
        var csv = new StringBuilder("users\nid;registration;name\n");
        for (var i = 1; i <= 200_000; i++)
        {
            csv.Append(i).Append(";R").Append(i).Append(";Pessoa ").Append(i).Append('\n');
        }

        csv.Append("cards\nid;value;user_id\n");
        for (var i = 1; i <= 200_000; i++)
        {
            csv.Append(i).Append(';').Append(ulong.MaxValue - (ulong)i).Append(';').Append(i).Append('\n');
        }

        var result = await SourceAnalyzer.AnalyzeAsync(Zip("users.csv", Encoding.UTF8.GetBytes(csv.ToString())), DetectedTypes.Zip, new CsvLimits(), CancellationToken.None);

        Assert.Equal(ParserStatuses.ParsedByDocumentation, result.ParserStatus);
        Assert.Equal(400_000, result.DataRows);
        Assert.All(result.Native!.Objects, o => Assert.Equal(0, o.RowsWithErrors));
        Assert.Empty(result.Native.PendingReferences);
    }
}
