using System.IO.Compression;
using System.Text;
using Platform.Modules.Importation;

namespace Platform.UnitTests;

public sealed class SafeCsvReaderTests
{
    private static readonly CsvLimits Limits = new();

    private static async Task<List<IReadOnlyList<string>>> Rows(string text, char delimiter = ';', CsvLimits? limits = null)
    {
        var rows = new List<IReadOnlyList<string>>();
        await foreach (var row in SafeCsvReader.ReadRowsAsync(new StringReader(text), delimiter, limits ?? Limits, CancellationToken.None))
        {
            rows.Add(row);
        }

        return rows;
    }

    private static Task<char> Delimiter(string text) =>
        SafeCsvReader.DetectDelimiterAsync(new StringReader(text), Limits, CancellationToken.None);

    // Valores preservados

    [Fact]
    public async Task Numbers_and_leading_zeros_stay_exactly_as_written()
    {
        var rows = await Rows("cartao;cpf;id\n18446744073709551615;00123456789;9007199254740993\n");

        Assert.Equal(["18446744073709551615", "00123456789", "9007199254740993"], rows[1]);
    }

    [Fact]
    public async Task Spaces_are_not_trimmed()
    {
        Assert.Equal([" a ", "  b"], (await Rows(" a ;  b\n"))[0]);
    }

    // Formato

    [Fact]
    public async Task Quoted_fields_keep_delimiters_newlines_and_doubled_quotes()
    {
        var rows = await Rows("\"a;b\";\"linha1\nlinha2\";\"diz \"\"oi\"\"\"\n");

        Assert.Equal(["a;b", "linha1\nlinha2", "diz \"oi\""], rows[0]);
    }

    [Theory]
    [InlineData("a;b\nc;d\n")]
    [InlineData("a;b\r\nc;d\r\n")]
    [InlineData("a;b\rc;d\r")]
    [InlineData("a;b\nc;d")]
    public async Task All_line_endings_work(string text)
    {
        var rows = await Rows(text);

        Assert.Equal(2, rows.Count);
        Assert.Equal(["c", "d"], rows[1]);
    }

    [Fact]
    public async Task Empty_cells_and_trailing_delimiter_are_kept()
    {
        Assert.Equal(["a", "", "c", ""], (await Rows("a;;c;\n"))[0]);
    }

    [Fact]
    public async Task Blank_lines_are_ignored()
    {
        Assert.Equal(2, (await Rows("a;b\n\n\nc;d\n")).Count);
    }

    [Fact]
    public async Task Byte_order_mark_does_not_stick_to_the_first_column()
    {
        Assert.Equal("id", (await Rows("\uFEFFid;name\n"))[0][0]);
    }

    // Entrada hostil

    [Fact]
    public async Task Unterminated_quote_is_refused()
    {
        var error = await Assert.ThrowsAsync<CsvFormatException>(() => Rows("a;\"sem fim\n"));
        Assert.Equal(CsvErrors.UnterminatedQuote, error.Code);
    }

    [Fact]
    public async Task Text_after_closing_quote_is_refused()
    {
        var error = await Assert.ThrowsAsync<CsvFormatException>(() => Rows("\"a\"b;c\n"));
        Assert.Equal(CsvErrors.InvalidQuote, error.Code);
    }

    [Fact]
    public async Task Too_many_columns_are_refused()
    {
        var error = await Assert.ThrowsAsync<CsvFormatException>(() => Rows("a;b;c;d\n", limits: new CsvLimits { MaxColumns = 3 }));
        Assert.Equal(CsvErrors.TooManyColumns, error.Code);
    }

    [Fact]
    public async Task Field_too_long_is_refused_even_inside_quotes()
    {
        var limits = new CsvLimits { MaxFieldChars = 8 };

        Assert.Equal(CsvErrors.FieldTooLong, (await Assert.ThrowsAsync<CsvFormatException>(() => Rows(new string('a', 9) + "\n", limits: limits))).Code);
        Assert.Equal(CsvErrors.FieldTooLong, (await Assert.ThrowsAsync<CsvFormatException>(() => Rows("\"" + new string('a', 9) + "\"\n", limits: limits))).Code);
    }

    [Fact]
    public async Task Too_many_rows_are_refused()
    {
        var error = await Assert.ThrowsAsync<CsvFormatException>(() => Rows("a\nb\nc\n", limits: new CsvLimits { MaxRows = 2 }));
        Assert.Equal(CsvErrors.TooManyRows, error.Code);
    }

    // Fórmulas

    [Theory]
    [InlineData("=1+1", true)]
    [InlineData("=HYPERLINK(\"http://x\")", true)]
    [InlineData("+cmd", true)]
    [InlineData("@SUM(A1)", true)]
    [InlineData("-cmd", true)]
    [InlineData("-5", false)]
    [InlineData("-1", false)]
    [InlineData("+55", false)]
    [InlineData("Ana", false)]
    [InlineData("", false)]
    public void Formula_detection(string value, bool expected)
    {
        Assert.Equal(expected, SafeCsvReader.LooksLikeFormula(value));
    }

    // Delimitador

    [Theory]
    [InlineData("a;b;c\n1;2;3\n", ';')]
    [InlineData("a,b,c\n1,2,3\n", ',')]
    [InlineData("a\tb\n1\t2\n", '\t')]
    [InlineData("nome;cidade\n\"Silva, Ana\";\"Rio, RJ\"\n", ';')]
    public async Task Consistent_delimiter_is_detected(string text, char expected)
    {
        Assert.Equal(expected, await Delimiter(text));
    }

    [Theory]
    [InlineData("a,b;c,d\n1,2;3,4\n")] // Dois candidatos coerentes
    [InlineData("so uma coluna\noutra linha\n")]
    [InlineData("a;b\n1;2;3\n")] // Metade das linhas com outra largura
    public async Task Doubt_about_the_delimiter_is_not_guessed(string text)
    {
        var error = await Assert.ThrowsAsync<CsvFormatException>(() => Delimiter(text));
        Assert.Equal(CsvErrors.AmbiguousDelimiter, error.Code);
    }

    // Codificacao

    private static async Task<string> Encoding(byte[] bytes) =>
        (await SafeCsvReader.DetectEncodingAsync(new MemoryStream(bytes), CancellationToken.None)).Name;

    [Fact]
    public async Task Valid_utf8_is_detected()
    {
        Assert.Equal("utf-8", await Encoding(System.Text.Encoding.UTF8.GetBytes("nome;São Paulo\n")));
    }

    [Fact]
    public async Task Windows_1252_is_detected_when_bytes_are_not_utf8()
    {
        System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        Assert.Equal("windows-1252", await Encoding(System.Text.Encoding.GetEncoding(1252).GetBytes("nome;São Paulo\n")));
    }

    [Fact]
    public async Task Invalid_sequence_late_in_the_file_is_still_noticed()
    {
        var bytes = new byte[100_000];
        Array.Fill(bytes, (byte)'a');
        bytes[^1] = 0xE3;

        Assert.Equal("windows-1252", await Encoding(bytes));
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x61, 0x00 })]
    [InlineData(new byte[] { 0xFE, 0xFF, 0x00, 0x61 })]
    public async Task Utf16_is_refused(byte[] bytes)
    {
        var error = await Assert.ThrowsAsync<CsvFormatException>(() => Encoding(bytes));
        Assert.Equal(CsvErrors.UnsupportedEncoding, error.Code);
    }
}

public sealed class SourceAnalyzerTests
{
    private static MemoryStream Zip(params (string Name, byte[] Content)[] entries)
    {
        var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var stream = archive.CreateEntry(name).Open();
                stream.Write(content);
            }
        }

        return buffer;
    }

    private static Task<SourceAnalysis> Analyze(Stream content, string type = DetectedTypes.Zip) =>
        SourceAnalyzer.AnalyzeAsync(content, type, new CsvLimits(), CancellationToken.None);

    private static byte[] Text(string value) => Encoding.UTF8.GetBytes(value);

    [Theory]
    [InlineData("users.csv", SourceFormats.Users)]
    [InlineData("sync.csv", SourceFormats.Sync)]
    [InlineData("backup.csv", SourceFormats.Backup)]
    public async Task Documented_structure_is_recognized_but_still_needs_manual_mapping(string name, string format)
    {
        using var zip = Zip((name, Text("id;name\n1;MARCADORPESSOAL\n")), ("user_images/1.jpg", [1, 2, 3]));

        var result = await Analyze(zip);

        Assert.Equal(format, result.SourceFormat);
        Assert.Equal(ParserStatuses.NeedsManualMapping, result.ParserStatus);
        Assert.Equal(1, result.ImageEntries);
        Assert.Equal(2, result.ColumnCount);
        Assert.Equal(1, result.DataRows);
    }

    [Fact]
    public async Task No_cell_value_appears_in_the_analysis()
    {
        using var zip = Zip(("users.csv", Text("id;name\n1;MARCADORPESSOAL\n")));

        var result = await Analyze(zip);

        Assert.DoesNotContain("MARCADORPESSOAL", System.Text.Json.JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task First_row_of_a_file_without_header_does_not_leak_into_the_analysis()
    {
        using var csv = new MemoryStream(Text("MARCADORNOME;MARCADORPIN\nOutra;Pessoa\n"));

        var json = System.Text.Json.JsonSerializer.Serialize(await Analyze(csv, DetectedTypes.DelimitedText));

        Assert.DoesNotContain("MARCADOR", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ambiguous_delimiter_goes_to_manual_mapping_not_to_unreadable()
    {
        using var csv = new MemoryStream(Text("a,b;c,d\n1,2;3,4\n"));

        var result = await Analyze(csv, DetectedTypes.DelimitedText);

        Assert.Equal(ParserStatuses.NeedsManualMapping, result.ParserStatus);
        Assert.Equal(CsvErrors.AmbiguousDelimiter, result.Problem);
        Assert.Null(result.Delimiter);
    }

    [Fact]
    public async Task Analysis_that_exceeds_the_time_limit_is_marked_unreadable()
    {
        using var csv = new HangingStream();

        var result = await SourceAnalyzer.AnalyzeAsync(csv, DetectedTypes.DelimitedText, new CsvLimits { MaxDuration = TimeSpan.FromMilliseconds(50) }, CancellationToken.None);

        Assert.Equal(ParserStatuses.Unreadable, result.ParserStatus);
        Assert.Equal(CsvErrors.Timeout, result.Problem);
    }

    [Fact]
    public async Task Cancellation_by_the_caller_is_not_mistaken_for_a_bad_file()
    {
        using var csv = new MemoryStream(Text("a;b\n1;2\n"));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SourceAnalyzer.AnalyzeAsync(csv, DetectedTypes.DelimitedText, new CsvLimits(), cancelled.Token));
    }

    [Theory]
    [InlineData("users.csv", "extra.txt")]
    [InlineData("pasta/users.csv", null)]
    [InlineData("USERS.CSV", null)]
    [InlineData("outro.csv", null)]
    public async Task Anything_outside_the_documented_structure_is_unknown(string first, string? second)
    {
        var entries = new List<(string, byte[])> { (first, Text("a;b\n1;2\n")) };
        if (second is not null)
        {
            entries.Add((second, Text("x")));
        }

        using var zip = Zip(entries.ToArray());

        Assert.Equal(SourceFormats.UnknownArchive, (await Analyze(zip)).SourceFormat);
    }

    [Fact]
    public async Task Plain_text_is_tabular_and_reports_structure()
    {
        using var csv = new MemoryStream(Text("id;nome\n1;=HYPERLINK(1)\n2;Ana;a mais\n"));

        var result = await Analyze(csv, DetectedTypes.DelimitedText);

        Assert.Equal(SourceFormats.Tabular, result.SourceFormat);
        Assert.Equal("utf-8", result.Encoding);
        Assert.Equal(";", result.Delimiter);
        Assert.Equal(2, result.DataRows);
        Assert.Equal(1, result.RowsWithDifferentWidth);
        Assert.Equal(1, result.FormulaLikeCells);
    }

    [Fact]
    public async Task Unreadable_csv_is_marked_and_keeps_the_reason_without_content()
    {
        using var csv = new MemoryStream(Text("a;b\n1;2\n\"MARCADOR sem fechar;x\n"));

        var result = await Analyze(csv, DetectedTypes.DelimitedText);

        Assert.Equal(ParserStatuses.Unreadable, result.ParserStatus);
        Assert.NotNull(result.Problem);
        Assert.DoesNotContain("MARCADOR", System.Text.Json.JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stream_stays_usable_after_analyzing_plain_text()
    {
        using var csv = new MemoryStream(Text("a;b\n1;2\n"));

        await Analyze(csv, DetectedTypes.DelimitedText);

        csv.Position = 0;
        Assert.Equal((byte)'a', csv.ReadByte());
    }
}

/// <summary>Conteudo que nunca termina de chegar: so o limite de tempo encerra a leitura.</summary>
internal sealed class HangingStream : MemoryStream
{
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return 0;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return 0;
    }
}

public sealed class HubPackageTests
{
    private const string Password = "senha-longa-de-teste";

    private static async Task<byte[]> Seal(byte[] content, string password = Password)
    {
        using var output = new MemoryStream();
        await HubPackage.SealAsync(new MemoryStream(content), output, password, CancellationToken.None);
        return output.ToArray();
    }

    private static async Task<byte[]> Open(byte[] package, string password = Password)
    {
        using var output = new MemoryStream();
        await HubPackage.OpenAsync(new MemoryStream(package), output, password, CancellationToken.None);
        return output.ToArray();
    }

    private static string HeaderOf(byte[] package)
    {
        var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(package.AsSpan(8));
        return Encoding.UTF8.GetString(package.AsSpan(12, length));
    }

    private static byte[] WithHeader(byte[] package, string header)
    {
        var oldLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(package.AsSpan(8));
        var bytes = Encoding.UTF8.GetBytes(header);
        var result = new byte[12 + bytes.Length + package.Length - 12 - oldLength];
        package.AsSpan(0, 8).CopyTo(result);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(8), bytes.Length);
        bytes.CopyTo(result, 12);
        package.AsSpan(12 + oldLength).CopyTo(result.AsSpan(12 + bytes.Length));
        return result;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3 * 1024 * 1024 + 17)]
    public async Task Round_trip(int size)
    {
        var content = new byte[size];
        new Random(size).NextBytes(content);

        Assert.Equal(content, await Open(await Seal(content)));
    }

    [Fact]
    public async Task Package_does_not_reveal_content_nor_password()
    {
        var package = await Seal(Encoding.UTF8.GetBytes("MARCADORPESSOAL"));
        var text = Encoding.Latin1.GetString(package);

        Assert.DoesNotContain("MARCADORPESSOAL", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, text, StringComparison.Ordinal);
        Assert.True(HubPackage.HasMagic(package));
    }

    [Fact]
    public async Task Same_content_and_password_produce_different_packages()
    {
        var a = await Seal([1, 2, 3]);
        var b = await Seal([1, 2, 3]);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task Wrong_password_and_tampering_fail_with_the_same_message()
    {
        var package = await Seal(new byte[4096]);

        var wrong = await Assert.ThrowsAsync<PackageOpenException>(() => Open(package, "outra-senha-qualquer"));

        var tampered = (byte[])package.Clone();
        tampered[^1] ^= 1;
        var changed = await Assert.ThrowsAsync<PackageOpenException>(() => Open(tampered));

        Assert.Equal(wrong.Message, changed.Message);
        Assert.Equal("Arquivo inválido ou senha incorreta.", wrong.Message);
    }

    [Fact]
    public async Task Any_change_in_the_header_fails_even_if_still_valid()
    {
        var package = await Seal(new byte[64]);
        var altered = HeaderOf(package).Replace("\"version\":1", "\"version\":1 ", StringComparison.Ordinal);

        await Assert.ThrowsAsync<PackageOpenException>(() => Open(WithHeader(package, altered)));
    }

    [Theory]
    [InlineData("\"memoryKiB\":65536", "\"memoryKiB\":8")] // Abaixo do piso
    [InlineData("\"memoryKiB\":65536", "\"memoryKiB\":99999999")] // Acima do teto: esgotaria a memoria
    [InlineData("\"passes\":3", "\"passes\":1")]
    [InlineData("\"passes\":3", "\"passes\":100000")]
    [InlineData("\"parallelism\":1", "\"parallelism\":64")]
    [InlineData("\"algorithm\":\"argon2id\"", "\"algorithm\":\"md5\"")]
    [InlineData("\"version\":1", "\"version\":2")]
    public async Task Forged_key_derivation_parameters_are_refused(string from, string to)
    {
        var package = await Seal(new byte[64]);
        var header = HeaderOf(package);
        Assert.Contains(from, header, StringComparison.Ordinal);

        var started = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<PackageOpenException>(() => Open(WithHeader(package, header.Replace(from, to, StringComparison.Ordinal))));

        // Recusado antes de derivar: um cabecalho forjado nao consome CPU nem memoria.
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(1), $"Demorou {started.Elapsed}.");
    }

    // Pacote legitimo, com a senha certa: so a politica de parametros pode recusar.
    [Theory]
    [InlineData(32 * 1024, 3)] // Memoria abaixo do piso
    [InlineData(64 * 1024, 2)] // Passes abaixo do piso
    [InlineData(257 * 1024, 3)] // Memoria acima do teto
    [InlineData(64 * 1024, 11)] // Passes acima do teto
    public async Task Correctly_sealed_package_outside_the_policy_is_refused(int memoryKiB, int passes)
    {
        using var output = new MemoryStream();
        await HubPackage.SealAsync(new MemoryStream([1, 2, 3]), output, Password, memoryKiB, passes, CancellationToken.None);

        await Assert.ThrowsAsync<PackageOpenException>(() => Open(output.ToArray()));
    }

    [Fact]
    public async Task Correctly_sealed_package_at_the_policy_ceiling_opens()
    {
        using var output = new MemoryStream();
        await HubPackage.SealAsync(new MemoryStream([1, 2, 3]), output, Password, 256 * 1024, 3, CancellationToken.None);

        Assert.Equal(new byte[] { 1, 2, 3 }, await Open(output.ToArray()));
    }

    [Fact]
    public async Task Parameters_inside_the_allowed_range_but_changed_still_fail()
    {
        var package = await Seal(new byte[64]);
        var altered = HeaderOf(package).Replace("\"passes\":3", "\"passes\":4", StringComparison.Ordinal);

        await Assert.ThrowsAsync<PackageOpenException>(() => Open(WithHeader(package, altered)));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 2, 3 })]
    public async Task Garbage_fails_closed(byte[] package)
    {
        await Assert.ThrowsAsync<PackageOpenException>(() => Open(package));
    }

    // Vetores gerados com argon2-cffi (implementacao de referencia), memoria em KiB. Fixam a unidade de memoria e o
    // formato: a sobrecarga com string do NSec produz outra saida, e os 64 MiB do cabecalho nao podem ser mentira.
    [Theory]
    [InlineData(65536, 3, "48ad96f27b215939a378ba8cd90e00111e716264d85d803cd0034f3cf031249d")]
    [InlineData(131072, 4, "f70882b4f8d1135d8065f3a5962347f282220623e275eebdc463f8b325566b56")]
    public void Key_derivation_matches_the_reference_implementation(int memoryKiB, int passes, string expected)
    {
        var salt = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();

        Assert.Equal(expected, Convert.ToHexStringLower(HubPackage.Derive("senha-de-teste-ç", salt, memoryKiB, passes)));
    }

    [Fact]
    public async Task Same_password_typed_with_another_unicode_form_still_opens()
    {
        var composed = "senha-de-acesso-ç";
        var decomposed = "senha-de-acesso-ç";
        Assert.NotEqual(composed, decomposed);

        Assert.Equal(new byte[] { 7 }, await Open(await Seal([7], composed), decomposed));
    }

    [Fact]
    public async Task Malformed_password_text_fails_like_a_wrong_password()
    {
        var package = await Seal([1]);

        await Assert.ThrowsAsync<PackageOpenException>(() => Open(package, "senha-invalida-\uD800"));
    }

    [Fact]
    public async Task Header_without_required_fields_fails_the_same_way()
    {
        var package = await Seal(new byte[64]);
        var kdfOnly = "{\"version\":1,\"kdf\":{\"algorithm\":\"argon2id\",\"memoryKiB\":65536,\"passes\":3,\"parallelism\":1}}";

        await Assert.ThrowsAsync<PackageOpenException>(() => Open(WithHeader(package, kdfOnly)));
        await Assert.ThrowsAsync<PackageOpenException>(() => Open(WithHeader(package, "null")));
        await Assert.ThrowsAsync<PackageOpenException>(() => Open(WithHeader(package, "{nao e json")));
    }

    [Fact]
    public async Task Package_hidden_inside_a_zip_is_refused_whatever_its_name()
    {
        var package = await Seal([1, 2, 3]);
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = archive.CreateEntry("foto.jpg").Open();
            entry.Write(package);
        }

        var result = await FileInspector.InspectAsync(zip, new InspectionLimits(), CancellationToken.None);

        Assert.Equal(RejectionCodes.NestedArchive, result.RejectionCode);
    }

    [Fact]
    public async Task Huge_declared_header_is_refused_before_allocating()
    {
        var package = await Seal([1]);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(package.AsSpan(8), int.MaxValue);

        await Assert.ThrowsAsync<PackageOpenException>(() => Open(package));
    }

    [Fact]
    public async Task Truncated_package_fails()
    {
        var package = await Seal(new byte[4096]);

        await Assert.ThrowsAsync<PackageOpenException>(() => Open(package[..^20]));
    }

    [Theory]
    [InlineData("curta")]
    [InlineData("")]
    public async Task Weak_password_is_refused_when_sealing(string password)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Seal([1], password));
    }

    [Fact]
    public async Task Inspector_recognizes_the_package_by_content_without_opening_it()
    {
        var package = await Seal([1, 2, 3]);

        var result = await FileInspector.InspectAsync(new MemoryStream(package), new InspectionLimits(), CancellationToken.None);

        Assert.Equal(DetectedTypes.HubPackage, result.DetectedType);
        Assert.Null(result.RejectionCode);
    }
}
