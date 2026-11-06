using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Platform.Modules.Importation;

namespace Platform.UnitTests;

/// <summary>
/// Os arquivos destes testes sao derivados da documentacao, nao de equipamento: provam que o parser segue o que esta
/// escrito, nao que o firmware grava assim. A prova com arquivo real fica em <see cref="RealSampleConformanceTests"/>.
/// </summary>
public sealed class NativeCsvParserTests
{
    private const string UsersHeader = "id;registration;name;password;salt;user_type_id;begin_time;end_time;image_timestamp;last_access";

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

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

    private static byte[] Text(string value) => Encoding.UTF8.GetBytes(value);

    private static Task<SourceAnalysis> AnalyzeZip(string name, string csv, params (string, byte[])[] more)
    {
        var entries = new List<(string, byte[])> { (name, Text(csv)) };
        entries.AddRange(more);
        return SourceAnalyzer.AnalyzeAsync(Zip(entries.ToArray()), DetectedTypes.Zip, new CsvLimits(), CancellationToken.None);
    }

    private static Task<SourceAnalysis> AnalyzeCsv(string csv) =>
        SourceAnalyzer.AnalyzeAsync(new MemoryStream(Text(csv)), DetectedTypes.DelimitedText, new CsvLimits(), CancellationToken.None);

    private static Task<NativeLayout?> Layout(string csv, string? flat = null) =>
        NativeCsvParser.DetectLayoutAsync(() => Task.FromResult<TextReader>(new StringReader(csv)), flat, new CsvLimits(), CancellationToken.None);

    private static async Task<(NativeParseSummary Summary, List<NativeRecord> Records)> Parse(string csv, string? flat = null, CsvLimits? limits = null)
    {
        var layout = await Layout(csv, flat);
        Assert.NotNull(layout);
        var records = new List<NativeRecord>();
        var summary = await NativeCsvParser.ParseAsync(
            new StringReader(csv),
            layout,
            limits ?? new CsvLimits(),
            (record, _) =>
            {
                records.Add(record);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);
        return (summary, records);
    }

    private static NativeObjectSummary Of(NativeParseSummary summary, string name) => summary.Objects.Single(o => o.Name == name);

    // Reconhecimento

    [Theory]
    [InlineData("users.csv", SourceFormats.Users)]
    [InlineData("sync.csv", SourceFormats.Sync)]
    [InlineData("backup.csv", SourceFormats.Backup)]
    public async Task Each_documented_export_with_sections_is_parsed(string name, string format)
    {
        var result = await AnalyzeZip(name, $"users\n{UsersHeader}\n1;A1;Ana;;;;0;0;0;0\n\ncards\nid;value;user_id\n5;18446744073709551615;1\n");

        Assert.Equal(format, result.SourceFormat);
        Assert.Equal(ParserStatuses.ParsedByDocumentation, result.ParserStatus);
        Assert.Equal(NativeCsvParser.ParserId, result.Native!.ParserId);
        Assert.Equal(1, Of(result.Native, "users").Rows);
        Assert.Equal(1, Of(result.Native, "cards").Rows);
        Assert.Equal(2, result.DataRows);
        Assert.Null(result.Problem);
    }

    [Fact]
    public async Task Users_file_may_be_a_single_table()
    {
        var result = await AnalyzeZip("users.csv", $"{UsersHeader}\n1;A1;Ana;;;;0;0;0;0\n");

        Assert.Equal(ParserStatuses.ParsedByDocumentation, result.ParserStatus);
        Assert.Equal(1, Of(result.Native!, "users").Rows);
    }

    [Fact]
    public async Task Sync_is_never_read_as_a_single_table_because_it_has_many_objects()
    {
        var result = await AnalyzeZip("sync.csv", $"{UsersHeader}\n1;A1;Ana;;;;0;0;0;0\n");

        Assert.Equal(ParserStatuses.NeedsManualMapping, result.ParserStatus);
        Assert.Equal(AnalysisProblems.NativeLayoutNotRecognized, result.Problem);
        Assert.Null(result.Native);
    }

    [Theory]
    [InlineData(';')]
    [InlineData(',')]
    [InlineData('\t')]
    public async Task Delimiter_comes_from_the_structure(char delimiter)
    {
        var csv = "users\nid;name;registration\n1;Ana, Maria;A1\n".Replace(';', delimiter == ';' ? ';' : delimiter);
        if (delimiter != ';')
        {
            csv = csv.Replace("Ana, Maria", "Ana", StringComparison.Ordinal);
        }

        Assert.Equal(delimiter, (await Layout(csv))!.Delimiter);
    }

    [Fact]
    public async Task Comma_inside_a_value_does_not_confuse_the_delimiter()
    {
        var (summary, records) = await Parse("users\nid;name\n1;Silva, Ana\n");

        Assert.Equal("Silva, Ana", records.Single().Fields["name"]);
        Assert.Equal(0, Of(summary, "users").RowsWithErrors);
    }

    [Theory]
    [InlineData("nome;matricula\nAna;A1\n")]
    [InlineData("users\nnome;matricula\nAna;A1\n")]
    [InlineData("users\nid;id;name\n1;1;Ana\n")]
    [InlineData("users\nid\n1\n")]
    [InlineData("")]
    public async Task Content_that_does_not_match_the_documentation_is_not_guessed(string csv)
    {
        var result = await AnalyzeZip("users.csv", csv);

        Assert.Equal(SourceFormats.Users, result.SourceFormat);
        Assert.Equal(ParserStatuses.NeedsManualMapping, result.ParserStatus);
        Assert.Null(result.Native);
    }

    [Fact]
    public async Task Structure_that_fits_more_than_one_delimiter_is_not_chosen()
    {
        // Secao desconhecida de uma coluna: nada no inicio do arquivo distingue os delimitadores.
        Assert.Null(await Layout("secao_nova\ncoluna\nvalor\n"));
    }

    [Theory]
    [InlineData("name;salt\nAna;x\n", "users")]
    [InlineData("user_type_id;begin_time\n1;0\n", "users")]
    [InlineData("id;time\n1;2\n", "access_logs")]
    [InlineData("id;event\n1;2\n", "access_logs")]
    public async Task Single_table_needs_the_columns_that_identify_the_object(string csv, string flat)
    {
        Assert.Null(await Layout(csv, flat));
    }

    [Fact]
    public async Task Word_followed_by_a_lone_field_name_does_not_open_a_section()
    {
        var (summary, _) = await Parse("users\nid;name\n1;Ana\ncards\nid\n5\n");

        Assert.Single(summary.Objects);
        Assert.Equal(4, Of(summary, "users").Rows);
    }

    [Fact]
    public async Task Object_with_a_single_documented_field_may_have_a_single_column()
    {
        var (summary, _) = await Parse("users\nid;name\n1;Ana\ncontingency_card_access_rules\naccess_rule_id\n5\n");

        Assert.Equal(1, Of(summary, "contingency_card_access_rules").Rows);
        Assert.Equal(1, Of(summary, "users").Rows);
    }

    [Fact]
    public async Task Bare_spreadsheet_stays_tabular_even_with_columns_that_look_like_a_user()
    {
        var result = await AnalyzeCsv("id;name;registration\n1;Ana;A1\n");

        Assert.Equal(SourceFormats.Tabular, result.SourceFormat);
        Assert.Equal(ParserStatuses.NeedsManualMapping, result.ParserStatus);
        Assert.Null(result.Problem);
    }

    [Fact]
    public async Task Logs_are_detected_by_content_with_any_file_name()
    {
        var result = await AnalyzeCsv("id;time;event;device_id;user_id\n1;1700000000;7;9;3\n");

        Assert.Equal(SourceFormats.LogsCsv, result.SourceFormat);
        Assert.Equal(ParserStatuses.ParsedByDocumentation, result.ParserStatus);
        Assert.Equal(1, Of(result.Native!, "access_logs").Rows);
    }

    [Fact]
    public async Task Logs_zip_named_in_the_documentation_is_accepted()
    {
        var result = await AnalyzeZip("logs.csv", "id;time;event\n1;1700000000;7\n");

        Assert.Equal(SourceFormats.LogsZip, result.SourceFormat);
        Assert.Equal(ParserStatuses.ParsedByDocumentation, result.ParserStatus);
    }

    [Fact]
    public async Task Bare_csv_with_object_sections_is_an_object_export()
    {
        var result = await AnalyzeCsv("groups\nid;name\n1;Portaria\n");

        Assert.Equal(SourceFormats.ObjectsCsv, result.SourceFormat);
        Assert.Equal(ParserStatuses.ParsedByDocumentation, result.ParserStatus);
    }

    // Contradicoes com o tipo declarado pela documentacao

    [Theory]
    [InlineData("users.csv", "users\nid;name\n1;Ana\naccess_logs\nid;time;event\n1;1;7\n")]
    [InlineData("users.csv", "cards\nid;value;user_id\n1;2;3\n")]
    [InlineData("sync.csv", "users\nid;name\n1;Ana\naccess_logs\nid;time;event\n1;1;7\n")]
    [InlineData("logs.csv", "users\nid;name\n1;Ana\n")]
    [InlineData("logs.csv", "access_logs\nid;time;event\n1;1;7\nusers\nid;name\n1;Ana\n")]
    public async Task Objects_that_contradict_the_export_type_stop_and_say_why(string name, string csv)
    {
        var result = await AnalyzeZip(name, csv);

        Assert.Equal(ParserStatuses.NeedsManualMapping, result.ParserStatus);
        Assert.Equal(AnalysisProblems.ObjectsContradictFormat, result.Problem);
        Assert.NotNull(result.Native);
    }

    [Fact]
    public async Task Backup_may_carry_logs()
    {
        var result = await AnalyzeZip("backup.csv", "users\nid;name\n1;Ana\naccess_logs\nid;time;event\n1;1;7\n");

        Assert.Equal(ParserStatuses.ParsedByDocumentation, result.ParserStatus);
    }

    [Fact]
    public async Task Firmware_line_is_not_claimed()
    {
        var json = JsonSerializer.Serialize(await AnalyzeZip("users.csv", "users\nid;name\n1;Ana\n"));

        Assert.DoesNotContain("v5", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("v6", json, StringComparison.OrdinalIgnoreCase);
    }

    // Valores

    [Fact]
    public async Task Values_reach_the_consumer_exactly_as_written()
    {
        var (_, records) = await Parse("cards\nid;value;user_id\n9007199254740993;18446744073709551615;007\n");

        var fields = records.Single().Fields;
        Assert.Equal("9007199254740993", fields["id"]);
        Assert.Equal("18446744073709551615", fields["value"]);
        Assert.Equal("007", fields["user_id"]);
    }

    [Fact]
    public async Task Card_value_in_logs_above_the_signed_limit_is_accepted()
    {
        // A pagina declara int 64 em access_logs.card_value e unsigned em cards.value: o mesmo cartao precisa caber.
        var (summary, _) = await Parse("id;time;event;card_value\n1;1;7;18446744073709551615\n", "access_logs");

        Assert.Equal(0, Of(summary, "access_logs").RowsWithErrors);
    }

    [Fact]
    public async Task Groups_name_is_text_despite_the_page_declaring_int()
    {
        var (summary, _) = await Parse("groups\nid;name\n1;Portaria\n");

        Assert.Equal(0, Of(summary, "groups").RowsWithErrors);
    }

    [Fact]
    public async Task Unknown_columns_are_preserved_for_the_consumer_but_not_named_in_the_summary()
    {
        var (summary, records) = await Parse("users\nid;name;MARCADORCOLUNA\n1;Ana;MARCADORVALOR\n");

        Assert.Equal("MARCADORVALOR", records.Single().Extension["MARCADORCOLUNA"]);
        Assert.Equal(1, Of(summary, "users").UnknownColumns);
        Assert.DoesNotContain("MARCADOR", JsonSerializer.Serialize(summary), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pin_in_logs_never_reaches_the_consumer_and_is_only_named_as_discarded()
    {
        var (summary, records) = await Parse("id;time;event;pin_value\n1;1;7;MARCADORPIN\n", "access_logs");

        Assert.False(records.Single().Fields.ContainsKey("pin_value"));
        Assert.False(records.Single().Extension.ContainsKey("pin_value"));
        Assert.Equal(["pin_value"], Of(summary, "access_logs").DiscardedColumns);
        Assert.DoesNotContain("MARCADORPIN", JsonSerializer.Serialize(summary), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sensitive_columns_are_flagged_without_their_values()
    {
        var result = await AnalyzeZip(
            "users.csv",
            "users\nid;name;password;salt\n1;MARCADORNOME;MARCADORHASH;MARCADORSALT\ntemplates\nid;template;user_id;finger_type\n2;TUFSQ0FET1I=;1;0\n");

        var json = JsonSerializer.Serialize(result);
        Assert.Equal(["password", "salt"], Of(result.Native!, "users").SensitiveColumns);
        Assert.Equal(["template"], Of(result.Native!, "templates").SensitiveColumns);
        Assert.DoesNotContain("MARCADOR", json, StringComparison.Ordinal);
        Assert.DoesNotContain("TUFSQ0FET1I", json, StringComparison.Ordinal);
    }

    // Erros de linha

    [Theory]
    [InlineData("users\nid;name\nabc;Ana\n", "users", NativeRowErrors.InvalidInteger)]
    [InlineData("users\nid;name\n1.5;Ana\n", "users", NativeRowErrors.InvalidInteger)]
    [InlineData("users\nid;name\n 1;Ana\n", "users", NativeRowErrors.InvalidInteger)]
    [InlineData("users\nid;name\n99999999999999999999;Ana\n", "users", NativeRowErrors.InvalidInteger)]
    [InlineData("users\nid;name\n;Ana\n", "users", NativeRowErrors.MissingRequired)]
    [InlineData("users\nid;name\n1;\n", "users", NativeRowErrors.MissingRequired)]
    [InlineData("cards\nid;value;user_id\n1;42;\n", "cards", NativeRowErrors.MissingRequired)]
    [InlineData("users\nid;name\n1;Ana;mais\n", "users", NativeRowErrors.WidthMismatch)]
    [InlineData("users\nid;name;begin_time\n1;Ana;-5\n", "users", NativeRowErrors.InvalidTimestamp)]
    [InlineData("users\nid;name;begin_time;end_time\n1;Ana;200;100\n", "users", NativeRowErrors.InvalidValidityRange)]
    [InlineData("cards\nid;value\n1;-1\n", "cards", NativeRowErrors.InvalidUnsigned)]
    [InlineData("cards\nid;value\n1;18446744073709551616\n", "cards", NativeRowErrors.InvalidUnsigned)]
    [InlineData("templates\nid;template;user_id\n1;nao e base64!;2\n", "templates", NativeRowErrors.InvalidBase64)]
    [InlineData("sec_boxs\nid;enabled\n1;talvez\n", "sec_boxs", NativeRowErrors.InvalidBoolean)]
    [InlineData("users\nid;name\n1;Ana\n1;Bia\n", "users", NativeRowErrors.DuplicateKey)]
    [InlineData("user_groups\nuser_id;group_id\n1;2\n1;2\n", "user_groups", NativeRowErrors.DuplicateKey)]
    [InlineData("cards\nid;value\n1;42\n2;42\n", "cards", NativeRowErrors.DuplicateCredential)]
    public async Task Bad_row_is_counted_by_code_and_does_not_stop_the_file(string csv, string objectName, string code)
    {
        var (summary, _) = await Parse(csv);

        var section = Of(summary, objectName);
        Assert.Equal(1, section.RowsWithErrors);
        Assert.Equal(1, section.Errors[code]);
    }

    [Theory]
    [InlineData("users\nid;name;begin_time;end_time\n1;Ana;0;100\n")]
    [InlineData("users\nid;name;begin_time;end_time\n1;Ana;200;0\n")]
    [InlineData("users\nid;name;begin_time;end_time\n1;Ana;100;100\n")]
    [InlineData("users\nid;name;begin_time;end_time\n1;Ana;;\n")]
    [InlineData("users\nid;name;user_type_id\n1;Ana;\n")]
    [InlineData("user_groups\nuser_id;group_id\n1;2\n1;3\n")]
    public async Task Valid_rows_have_no_errors(string csv)
    {
        var (summary, _) = await Parse(csv);

        Assert.All(summary.Objects, section => Assert.Equal(0, section.RowsWithErrors));
    }

    [Fact]
    public async Task Same_card_value_in_different_objects_is_not_a_duplicate()
    {
        var (summary, _) = await Parse("cards\nid;value\n1;42\nqrcodes\nid;value\n1;42\n");

        Assert.All(summary.Objects, section => Assert.Equal(0, section.RowsWithErrors));
    }

    [Fact]
    public async Task Error_codes_never_carry_the_value()
    {
        var (summary, records) = await Parse("cards\nid;value\nMARCADORID;MARCADORCARTAO\n");

        Assert.DoesNotContain("MARCADOR", JsonSerializer.Serialize(summary), StringComparison.Ordinal);
        Assert.DoesNotContain(records.Single().Errors, e => e.Contains("MARCADOR", StringComparison.Ordinal));
    }

    // Referencias

    [Fact]
    public async Task Reference_to_a_user_absent_from_the_file_is_pending()
    {
        var (summary, _) = await Parse("users\nid;name\n1;Ana\ncards\nid;value;user_id\n1;10;1\n2;11;99\n3;12;99\n");

        Assert.Equal(1, summary.PendingReferences["cards.user_id"]);
    }

    [Fact]
    public async Task Reference_is_checked_regardless_of_section_order()
    {
        var (summary, _) = await Parse("cards\nid;value;user_id\n1;10;1\nusers\nid;name\n1;Ana\n");

        Assert.Empty(summary.PendingReferences);
    }

    [Fact]
    public async Task Target_object_outside_the_file_is_not_a_pending_reference()
    {
        var (summary, _) = await Parse("users\nid;name\n1;Ana\nuser_groups\nuser_id;group_id\n1;7\n");

        Assert.Empty(summary.PendingReferences);
    }

    [Fact]
    public async Task Rule_pointing_to_a_missing_group_is_reported()
    {
        var (summary, _) = await Parse("groups\nid;name\n1;A\ngroup_access_rules\ngroup_id;access_rule_id\n2;5\n");

        Assert.Equal(1, summary.PendingReferences["group_access_rules.group_id"]);
        Assert.False(summary.PendingReferences.ContainsKey("group_access_rules.access_rule_id"));
    }

    [Fact]
    public async Task Logs_citing_a_removed_user_are_not_pending()
    {
        var (summary, _) = await Parse("users\nid;name\n1;Ana\naccess_logs\nid;time;event;user_id\n1;1;7;99\n");

        Assert.Empty(summary.PendingReferences);
    }

    [Fact]
    public async Task Tracking_limit_is_reported_and_does_not_invent_pending_references()
    {
        var (summary, _) = await Parse("users\nid;name\n1;Ana\n2;Bia\n3;Cid\ncards\nid;value;user_id\n1;10;3\n", limits: new CsvLimits { MaxTrackedValues = 2 });

        Assert.True(summary.DuplicateTrackingTruncated);
        Assert.Empty(summary.PendingReferences);
    }

    [Fact]
    public async Task Beyond_the_tracking_limit_values_seen_before_are_still_recognized()
    {
        var (summary, _) = await Parse("users\nid;name\n1;Ana\n2;Bia\n3;Cid\n1;Dup\n", limits: new CsvLimits { MaxTrackedValues = 2 });

        Assert.Equal(1, Of(summary, "users").Errors[NativeRowErrors.DuplicateKey]);
    }

    // Secoes

    [Fact]
    public async Task Unknown_section_is_counted_without_its_name_or_values()
    {
        var (summary, records) = await Parse("users\nid;name\n1;Ana\nmarcadorsecao\ncoluna_a;coluna_b\nMARCADOR1;MARCADOR2\ncards\nid;value\n1;2\n");

        Assert.Equal(1, summary.UnknownSections);
        Assert.Equal(1, summary.UnknownSectionRows);
        Assert.Equal(0, summary.RowsOutsideSections);
        Assert.Equal(1, Of(summary, "cards").Rows);
        Assert.Equal(2, records.Count);
        var json = JsonSerializer.Serialize(summary);
        Assert.DoesNotContain("marcadorsecao", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MARCADOR", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Custom_object_from_the_documentation_example_is_an_unknown_section()
    {
        var (summary, _) = await Parse("users\nid;name\n1;Ana\nc_users\nid;user_id;cpf\n1;1;12345678900\n");

        Assert.Equal(1, summary.UnknownSections);
        Assert.DoesNotContain("12345678900", JsonSerializer.Serialize(summary), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Single_cell_row_without_a_header_after_it_is_an_ordinary_bad_row()
    {
        var (summary, records) = await Parse("users\nid;name\n1;Ana\nana\n2;Bia\n");

        var section = Of(summary, "users");
        Assert.Equal(3, section.Rows);
        Assert.Equal(1, section.Errors[NativeRowErrors.WidthMismatch]);
        Assert.Equal([3L, 4L, 5L], records.Select(r => r.RowNumber));
    }

    [Fact]
    public async Task Single_cell_row_at_the_end_of_the_file_is_not_lost()
    {
        var (summary, _) = await Parse("users\nid;name\n1;Ana\nfim\n");

        Assert.Equal(2, Of(summary, "users").Rows);
    }

    [Fact]
    public async Task Single_table_does_not_treat_a_lone_word_as_a_section()
    {
        var (summary, _) = await Parse("id;name\ncards\nid;name\n", "users");

        Assert.Equal(0, summary.UnknownSections);
        Assert.Equal(2, Of(summary, "users").Rows);
        Assert.Single(summary.Objects);
    }

    [Fact]
    public async Task Section_repeated_later_keeps_adding_to_the_same_object()
    {
        var (summary, _) = await Parse("users\nid;name\n1;Ana\ncards\nid;value\n1;2\nusers\nid;name\n2;Bia\n");

        Assert.Equal(2, Of(summary, "users").Rows);
    }

    [Fact]
    public async Task Missing_required_column_is_reported()
    {
        var (summary, _) = await Parse("users\nid;name\n1;Ana\n");

        Assert.Equal(["registration"], Of(summary, "users").MissingRequiredColumns);
    }

    [Fact]
    public async Task Columns_selected_in_the_documentation_example_are_accepted()
    {
        // O exemplo de export_objects pede cards so com value e user_id.
        var (summary, _) = await Parse("cards\nvalue;user_id\n42;1\n");

        Assert.Equal(1, Of(summary, "cards").Rows);
        Assert.Equal(["id"], Of(summary, "cards").MissingRequiredColumns);
        Assert.Equal(0, Of(summary, "cards").RowsWithErrors);
    }

    // Arquivo hostil

    [Fact]
    public async Task Formula_like_cells_are_counted_and_kept()
    {
        var (summary, records) = await Parse("users\nid;name\n1;=HYPERLINK(1)\n");

        Assert.Equal(1, summary.FormulaLikeCells);
        Assert.Equal("=HYPERLINK(1)", records.Single().Fields["name"]);
    }

    [Fact]
    public async Task Unterminated_quote_after_a_valid_start_is_unreadable()
    {
        var result = await AnalyzeZip("users.csv", "users\nid;name\n1;\"sem fechar\n");

        Assert.Equal(ParserStatuses.Unreadable, result.ParserStatus);
        Assert.Equal(CsvErrors.UnterminatedQuote, result.Problem);
    }

    [Fact]
    public async Task Too_many_rows_is_unreadable_even_after_a_valid_start()
    {
        var result = await SourceAnalyzer.AnalyzeAsync(
            Zip(("users.csv", Text("users\nid;name\n1;Ana\n2;Bia\n3;Cid\n"))),
            DetectedTypes.Zip,
            new CsvLimits { MaxRows = 3 },
            CancellationToken.None);

        Assert.Equal(ParserStatuses.Unreadable, result.ParserStatus);
        Assert.Equal(CsvErrors.TooManyRows, result.Problem);
    }

    [Fact]
    public async Task Windows_1252_is_read_correctly_in_a_native_file()
    {
        var bytes = Encoding.Latin1.GetBytes("users\nid;name\n1;João\n");
        var result = await SourceAnalyzer.AnalyzeAsync(Zip(("users.csv", bytes)), DetectedTypes.Zip, new CsvLimits(), CancellationToken.None);

        Assert.Equal("windows-1252", result.Encoding);
        Assert.Equal(ParserStatuses.ParsedByDocumentation, result.ParserStatus);
    }

    [Fact]
    public async Task Byte_order_mark_does_not_hide_the_first_section()
    {
        Assert.NotNull(await Layout("﻿users\nid;name\n1;Ana\n"));
    }

    // Imagens

    [Fact]
    public async Task Images_are_checked_by_signature()
    {
        var result = await AnalyzeZip(
            "users.csv",
            "users\nid;name\n1;Ana\n",
            ("user_images/1.jpg", Jpeg),
            ("user_images/2.png", Png),
            ("user_images/3.jpg", Text("<script>")),
            ("user_images/4.jpg", []));

        Assert.Equal(4, result.ImageEntries);
        Assert.Equal(2, result.InvalidImageEntries);
    }

    [Fact]
    public async Task Empty_images_folder_is_valid_as_the_documentation_describes_for_v5()
    {
        var result = await AnalyzeZip("users.csv", "users\nid;name\n1;Ana\n", ("user_images/", []));

        Assert.Equal(0, result.ImageEntries);
        Assert.Equal(ParserStatuses.ParsedByDocumentation, result.ParserStatus);
    }

    // Esquema

    [Fact]
    public void Schema_agrees_with_the_catalog_used_by_the_connector()
    {
        var catalog = Platform.Modules.DeviceIntegration.ControlId.ObjectCatalog.Objects;

        Assert.Equal(catalog.Keys.Order(StringComparer.Ordinal), NativeObjectSchema.Objects.Keys.Order(StringComparer.Ordinal));
        foreach (var (name, definition) in catalog)
        {
            var schema = NativeObjectSchema.Objects[name];
            Assert.Equal(definition.Key, schema.Key);
            Assert.Equal(definition.Safe.Order(StringComparer.Ordinal), Names(schema, NativeFieldSensitivity.Safe));
            Assert.Equal(definition.Sensitive.Order(StringComparer.Ordinal), Names(schema, NativeFieldSensitivity.Sensitive));
            Assert.Equal(definition.Never.Order(StringComparer.Ordinal), Names(schema, NativeFieldSensitivity.Never));
        }

        static IEnumerable<string> Names(NativeObject schema, NativeFieldSensitivity sensitivity) =>
            schema.Fields.Values.Where(f => f.Sensitivity == sensitivity).Select(f => f.Name).Order(StringComparer.Ordinal);
    }

    [Fact]
    public void Schema_covers_every_object_with_its_documented_divergences()
    {
        Assert.Equal(44, NativeObjectSchema.Objects.Count);
        Assert.Equal(NativeFieldKind.UnsignedWhole, NativeObjectSchema.Objects["cards"].Fields["value"].Kind);
        Assert.Equal(NativeFieldKind.UnsignedWhole, NativeObjectSchema.Objects["access_logs"].Fields["card_value"].Kind);
        Assert.Equal(NativeFieldKind.Text, NativeObjectSchema.Objects["groups"].Fields["name"].Kind);
        Assert.Equal(NativeFieldKind.Base64, NativeObjectSchema.Objects["templates"].Fields["template"].Kind);
        Assert.Equal(NativeFieldSensitivity.Never, NativeObjectSchema.Objects["access_logs"].Fields["pin_value"].Sensitivity);
        Assert.Equal(NativeFieldSensitivity.Sensitive, NativeObjectSchema.Objects["pins"].Fields["value"].Sensitivity);
    }
}
