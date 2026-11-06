using System.Text.Json;
using Platform.Modules.Importation;

namespace Platform.UnitTests;

public sealed class PreviewClassifierTests
{
    private static readonly Dictionary<string, string> NoPolicy = [];
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static PersonCandidate Candidate(long row = 1, string? id = "10", string? registration = "MAT001", string? name = "Ana Souza", string type = "employee", DateTimeOffset? from = null, DateTimeOffset? to = null, params string[] errors) =>
        new(row, id, registration, name, type, from, to, errors);

    private static ExistingPerson Person(Guid? id = null, bool sameSite = true, string name = "Ana Souza", string? registration = "MAT001", string type = "employee", DateTimeOffset? from = null, DateTimeOffset? to = null, string state = "active", string? link = null) =>
        new(id ?? Guid.NewGuid(), sameSite, name, registration, type, from, to, state, link);

    private static PreviewRow One(PersonCandidate candidate, IReadOnlyCollection<ExistingPerson>? existing = null, Dictionary<string, string>? policy = null) =>
        PreviewClassifier.Classify([candidate], existing ?? [], policy ?? NoPolicy).Single();

    // Conciliacao

    [Fact]
    public void Person_not_found_is_new()
    {
        Assert.Equal(PreviewKinds.New, One(Candidate()).Kind);
    }

    [Fact]
    public void Same_registration_and_fields_is_no_change()
    {
        var existing = Person();

        var row = One(Candidate(), [existing]);

        Assert.Equal(PreviewKinds.NoChange, row.Kind);
        Assert.Equal(existing.Id, row.MatchPersonId);
    }

    [Fact]
    public void Importing_the_same_data_twice_changes_nothing()
    {
        var existing = Person(from: Start, to: Start.AddDays(30));

        var row = One(Candidate(from: Start, to: Start.AddDays(30)), [existing]);

        Assert.Equal(PreviewKinds.NoChange, row.Kind);
        Assert.Empty(row.Sample.Changes);
    }

    [Fact]
    public void Device_link_wins_over_a_missing_registration_match()
    {
        var existing = Person(registration: "OUTRA", link: "10");

        var row = One(Candidate(), [existing], new Dictionary<string, string> { [PersonFields.Registration] = FieldPolicies.SourceWins });

        Assert.Equal(PreviewKinds.Update, row.Kind);
        Assert.Equal(existing.Id, row.MatchPersonId);
    }

    [Fact]
    public void Link_and_registration_pointing_to_different_people_needs_a_person()
    {
        var byLink = Person(registration: "OUTRA", link: "10");
        var byRegistration = Person();

        var row = One(Candidate(), [byLink, byRegistration]);

        Assert.Equal(PreviewKinds.PossibleDuplicate, row.Kind);
        Assert.Equal([PreviewCodes.AmbiguousMatch], row.Codes);
        Assert.Null(row.MatchPersonId);
        Assert.Equal(2, row.Sample.Candidates.Count);
    }

    [Fact]
    public void Two_people_with_the_same_registration_is_a_tie_for_a_person_to_break()
    {
        var row = One(Candidate(), [Person(), Person()]);

        Assert.Equal(PreviewKinds.PossibleDuplicate, row.Kind);
        Assert.Equal([PreviewCodes.AmbiguousMatch], row.Codes);
    }

    [Fact]
    public void Name_alone_never_matches_automatically()
    {
        var existing = Person(registration: "DIFERENTE");

        var row = One(Candidate(), [existing]);

        Assert.Equal(PreviewKinds.PossibleDuplicate, row.Kind);
        Assert.Equal([PreviewCodes.SameNameDifferentRegistration], row.Codes);
        Assert.Null(row.MatchPersonId);
    }

    [Theory]
    [InlineData("ANA  SOUZA")]
    [InlineData(" ana souza ")]
    public void Name_suspicion_ignores_case_and_spacing(string name)
    {
        Assert.Equal(PreviewKinds.PossibleDuplicate, One(Candidate(name: name), [Person(registration: "X")]).Kind);
    }

    [Fact]
    public void Registration_in_another_site_is_flagged_without_revealing_the_person()
    {
        var other = Person(sameSite: false, name: "MARCADOR Outra Unidade");

        var row = One(Candidate(), [other]);

        Assert.Equal(PreviewKinds.PossibleDuplicate, row.Kind);
        Assert.Equal([PreviewCodes.RegistrationInOtherSite], row.Codes);
        Assert.Empty(row.Sample.Candidates);
        Assert.Null(row.MatchPersonId);
        Assert.DoesNotContain(other.Id.ToString(), JsonSerializer.Serialize(row), StringComparison.Ordinal);
    }

    [Fact]
    public void Person_of_another_site_is_not_a_name_suspect()
    {
        Assert.Equal(PreviewKinds.New, One(Candidate(), [Person(sameSite: false, registration: "X")]).Kind);
    }

    [Fact]
    public void Device_link_of_another_site_is_not_followed()
    {
        Assert.Equal(PreviewKinds.New, One(Candidate(), [Person(sameSite: false, registration: "X", name: "Outra", link: "10")]).Kind);
    }

    [Fact]
    public void Deleted_person_is_ignored()
    {
        Assert.Equal(PreviewKinds.New, One(Candidate(), [Person(state: "deleted")]).Kind);
    }

    [Fact]
    public void Revoked_person_never_comes_back_silently()
    {
        var row = One(Candidate(), [Person(state: "revoked")], new Dictionary<string, string> { [PersonFields.DisplayName] = FieldPolicies.SourceWins });

        Assert.Equal(PreviewKinds.FieldConflict, row.Kind);
        Assert.Equal([PreviewCodes.PersonRevoked], row.Codes);
    }

    // Politica por campo

    [Fact]
    public void Default_policy_asks_a_person()
    {
        var row = One(Candidate(name: "Ana Souza Lima"), [Person()]);

        Assert.Equal(PreviewKinds.FieldConflict, row.Kind);
        Assert.Equal(FieldPolicies.Manual, row.Sample.Changes.Single().Policy);
    }

    [Theory]
    [InlineData(FieldPolicies.SourceWins, PreviewKinds.Update)]
    [InlineData(FieldPolicies.PlatformWins, PreviewKinds.NoChange)]
    [InlineData(FieldPolicies.Manual, PreviewKinds.FieldConflict)]
    public void Policy_decides_what_a_difference_means(string policy, string kind)
    {
        var row = One(Candidate(name: "Ana Souza Lima"), [Person()], new Dictionary<string, string> { [PersonFields.DisplayName] = policy });

        Assert.Equal(kind, row.Kind);
    }

    [Fact]
    public void One_manual_field_is_enough_for_a_conflict()
    {
        var row = One(
            Candidate(name: "Ana Souza Lima", type: "visitor"),
            [Person()],
            new Dictionary<string, string> { [PersonFields.DisplayName] = FieldPolicies.SourceWins, [PersonFields.PersonType] = FieldPolicies.Manual });

        Assert.Equal(PreviewKinds.FieldConflict, row.Kind);
        Assert.Equal(2, row.Sample.Changes.Count);
    }

    [Theory]
    [InlineData(PersonFields.DisplayName)]
    [InlineData(PersonFields.PersonType)]
    [InlineData(PersonFields.ValidFrom)]
    [InlineData(PersonFields.ValidTo)]
    public void Each_field_is_compared(string field)
    {
        var candidate = field switch
        {
            PersonFields.DisplayName => Candidate(name: "Outro Nome"),
            PersonFields.PersonType => Candidate(type: "visitor"),
            PersonFields.ValidFrom => Candidate(from: Start),
            _ => Candidate(to: Start),
        };

        Assert.Equal(field, One(candidate, [Person()]).Sample.Changes.Single().Field);
    }

    [Fact]
    public void Same_instant_in_another_offset_is_not_a_difference()
    {
        Assert.Equal(PreviewKinds.NoChange, One(Candidate(from: Start.ToOffset(TimeSpan.FromHours(-3))), [Person(from: Start)]).Kind);
    }

    // Dentro do arquivo

    [Fact]
    public void Identical_row_is_an_exact_duplicate_even_with_the_repeated_key_error()
    {
        var rows = PreviewClassifier.Classify([Candidate(1), Candidate(2, errors: NativeRowErrors.DuplicateKey)], [], NoPolicy);

        Assert.Equal(PreviewKinds.New, rows[0].Kind);
        Assert.Equal(PreviewKinds.ExactDuplicate, rows[1].Kind);
    }

    [Fact]
    public void Same_key_with_different_content_is_invalid()
    {
        var rows = PreviewClassifier.Classify([Candidate(1), Candidate(2, name: "Outra Pessoa", errors: NativeRowErrors.DuplicateKey)], [], NoPolicy);

        Assert.Equal(PreviewKinds.Invalid, rows[1].Kind);
    }

    [Fact]
    public void Same_registration_with_another_key_is_a_suspect()
    {
        var rows = PreviewClassifier.Classify([Candidate(1), Candidate(2, id: "11", name: "Outra Pessoa")], [], NoPolicy);

        Assert.Equal(PreviewKinds.PossibleDuplicate, rows[1].Kind);
        Assert.Equal([PreviewCodes.RegistrationRepeatedInFile], rows[1].Codes);
    }

    [Theory]
    [InlineData(null, "MAT1", PreviewCodes.MissingName)]
    [InlineData("  ", "MAT1", PreviewCodes.MissingName)]
    [InlineData("Ana", null, PreviewCodes.MissingRegistration)]
    [InlineData("Ana", "", PreviewCodes.MissingRegistration)]
    public void Without_name_or_registration_the_row_is_invalid(string? name, string? registration, string code)
    {
        var row = One(Candidate(name: name, registration: registration));

        Assert.Equal(PreviewKinds.Invalid, row.Kind);
        Assert.Contains(code, row.Codes);
    }

    [Fact]
    public void Parser_error_makes_the_row_invalid_and_keeps_the_code()
    {
        var row = One(Candidate(errors: NativeRowErrors.InvalidValidityRange));

        Assert.Equal(PreviewKinds.Invalid, row.Kind);
        Assert.Equal([NativeRowErrors.InvalidValidityRange], row.Codes);
    }

    // Dados na previa

    [Fact]
    public void Preview_has_only_masked_values()
    {
        var existing = Person(name: "MARCADORANTIGO Lima", registration: "MARCADORMAT99", link: null);
        var rows = PreviewClassifier.Classify(
            [Candidate(name: "MARCADORNOVO Souza", registration: "MARCADORMAT99"), Candidate(2, id: "11", name: "MARCADORANTIGO Lima", registration: "MARCADOROUTRA")],
            [existing],
            NoPolicy);

        var json = JsonSerializer.Serialize(rows);

        Assert.DoesNotContain("MARCADOR", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Souza", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Lima", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1234567890")]
    [InlineData("=HYPERLINK(1)")]
    [InlineData("Ana Souza")]
    public void Source_key_that_is_not_a_number_does_not_enter_the_preview(string key)
    {
        var expected = key.All(char.IsAsciiDigit) ? key : null;

        Assert.Equal(expected, One(Candidate(id: key)).SourceKey);
    }

    [Theory]
    [InlineData("Ana Souza", "A** S****")]
    [InlineData("Jo", "J**")]
    [InlineData("Áurea", "Á****")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Name_mask(string? value, string expected)
    {
        Assert.Equal(expected, Mask.Name(value));
    }

    [Theory]
    [InlineData("123456", "****56")]
    [InlineData("12345", "*****")]
    [InlineData("12", "****")]
    [InlineData("", "")]
    public void Code_mask_shows_only_the_tail_of_long_values(string value, string expected)
    {
        Assert.Equal(expected, Mask.Code(value));
    }

    // Decisoes

    [Theory]
    [InlineData(PreviewKinds.FieldConflict, ImportDecisions.AcceptSource, true)]
    [InlineData(PreviewKinds.FieldConflict, ImportDecisions.KeepPlatform, true)]
    [InlineData(PreviewKinds.FieldConflict, ImportDecisions.Skip, true)]
    [InlineData(PreviewKinds.FieldConflict, ImportDecisions.Link, false)]
    [InlineData(PreviewKinds.FieldConflict, ImportDecisions.CreateNew, false)]
    [InlineData(PreviewKinds.PossibleDuplicate, ImportDecisions.Link, true)]
    [InlineData(PreviewKinds.PossibleDuplicate, ImportDecisions.CreateNew, true)]
    [InlineData(PreviewKinds.PossibleDuplicate, ImportDecisions.Skip, true)]
    [InlineData(PreviewKinds.PossibleDuplicate, ImportDecisions.AcceptSource, false)]
    [InlineData(PreviewKinds.New, ImportDecisions.Skip, false)]
    [InlineData(PreviewKinds.Invalid, ImportDecisions.AcceptSource, false)]
    public void Decision_must_fit_the_category(string kind, string decision, bool allowed)
    {
        Assert.Equal(allowed, PreviewPlan.Allows(kind, decision));
    }

    // Leitura

    private static NativeRecord Native(params (string Key, string Value)[] fields) =>
        new("users", 7, fields.ToDictionary(f => f.Key, f => f.Value), new Dictionary<string, string>(), []);

    [Fact]
    public void Native_user_maps_documented_fields()
    {
        var person = PersonReader.FromNative(Native(("id", "5"), ("registration", "A1"), ("name", "Ana"), ("user_type_id", "1"), ("begin_time", "1700000000"), ("end_time", "1800000000")));

        Assert.Equal("5", person.SourceId);
        Assert.Equal("A1", person.Registration);
        Assert.Equal("Ana", person.DisplayName);
        Assert.Equal("visitor", person.PersonType);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), person.ValidFrom);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1800000000), person.ValidTo);
        Assert.Empty(person.Errors);
    }

    [Fact]
    public void Zero_time_means_no_check_and_empty_type_means_user_as_documented()
    {
        var person = PersonReader.FromNative(Native(("id", "5"), ("begin_time", "0"), ("end_time", "0"), ("user_type_id", "")));

        Assert.Null(person.ValidFrom);
        Assert.Null(person.ValidTo);
        Assert.Equal("employee", person.PersonType);
    }

    [Fact]
    public void Undocumented_user_type_is_not_guessed()
    {
        Assert.Contains(PreviewCodes.UnknownUserType, PersonReader.FromNative(Native(("user_type_id", "2"))).Errors);
    }

    [Fact]
    public void Password_columns_do_not_reach_the_candidate()
    {
        var person = PersonReader.FromNative(Native(("id", "5"), ("name", "Ana"), ("password", "MARCADOR"), ("salt", "MARCADOR")));

        Assert.DoesNotContain("MARCADOR", JsonSerializer.Serialize(person), StringComparison.Ordinal);
    }

    private static TabularMapping Mapped(params MappingColumn[] extra) =>
        new(";", true, [new MappingColumn(0, PersonFields.DisplayName, null), new MappingColumn(1, PersonFields.Registration, null), .. extra]);

    [Fact]
    public void Mapped_row_keeps_values_exact()
    {
        var person = PersonReader.FromMapped(["Ana", "007"], 2, Mapped());

        Assert.Equal("007", person.Registration);
        Assert.Empty(person.Errors);
    }

    [Theory]
    [InlineData(DateFormats.Brazilian, "31/12/2026", 2026, 12, 31)]
    [InlineData(DateFormats.Brazilian, "01/02/2026 08:30", 2026, 2, 1)]
    [InlineData(DateFormats.Iso, "2026-03-04", 2026, 3, 4)]
    [InlineData(DateFormats.Unix, "1772582400", 2026, 3, 4)]
    public void Dates_follow_the_chosen_format(string format, string value, int year, int month, int day)
    {
        var person = PersonReader.FromMapped(["Ana", "1", value], 2, Mapped(new MappingColumn(2, PersonFields.ValidTo, format)));

        Assert.Empty(person.Errors);
        Assert.Equal(new DateOnly(year, month, day), DateOnly.FromDateTime(person.ValidTo!.Value.UtcDateTime));
    }

    [Theory]
    [InlineData(DateFormats.Brazilian, "2026-12-31")]
    [InlineData(DateFormats.Brazilian, "31/13/2026")]
    [InlineData(DateFormats.Iso, "amanha")]
    [InlineData(DateFormats.Unix, "-5")]
    [InlineData(DateFormats.Unix, "99999999999999")]
    public void Date_outside_the_format_is_an_error_and_not_a_guess(string format, string value)
    {
        var person = PersonReader.FromMapped(["Ana", "1", value], 2, Mapped(new MappingColumn(2, PersonFields.ValidFrom, format)));

        Assert.Contains(PreviewCodes.InvalidDate, person.Errors);
        Assert.Null(person.ValidFrom);
    }

    [Fact]
    public void Inverted_validity_is_an_error()
    {
        var person = PersonReader.FromMapped(
            ["Ana", "1", "02/01/2026", "01/01/2026"],
            2,
            Mapped(new MappingColumn(2, PersonFields.ValidFrom, DateFormats.Brazilian), new MappingColumn(3, PersonFields.ValidTo, DateFormats.Brazilian)));

        Assert.Contains(NativeRowErrors.InvalidValidityRange, person.Errors);
    }

    [Fact]
    public void Row_shorter_than_the_mapping_is_an_error()
    {
        Assert.Contains(NativeRowErrors.WidthMismatch, PersonReader.FromMapped(["Ana"], 2, Mapped()).Errors);
    }

    [Theory]
    [InlineData("employee", true)]
    [InlineData("visitor", true)]
    [InlineData("chefe", false)]
    public void Person_type_must_be_known(string value, bool valid)
    {
        var person = PersonReader.FromMapped(["Ana", "1", value], 2, Mapped(new MappingColumn(2, PersonFields.PersonType, null)));

        Assert.Equal(!valid, person.Errors.Contains(PreviewCodes.InvalidPersonType));
    }

    // Regras do mapeamento

    [Fact]
    public void Valid_mapping_has_no_problem()
    {
        Assert.Null(TabularMappingRules.Validate(Mapped()));
    }

    public static TheoryData<TabularMapping?> InvalidMappings =>
    [
        null,
        new TabularMapping(";", true, []),
        new TabularMapping("|", true, [new MappingColumn(0, PersonFields.DisplayName, null), new MappingColumn(1, PersonFields.Registration, null)]),
        new TabularMapping(";", true, [new MappingColumn(0, PersonFields.DisplayName, null)]),
        new TabularMapping(";", true, [new MappingColumn(0, PersonFields.Registration, null)]),
        new TabularMapping(";", true, [new MappingColumn(0, PersonFields.DisplayName, null), new MappingColumn(0, PersonFields.Registration, null)]),
        new TabularMapping(";", true, [new MappingColumn(0, PersonFields.DisplayName, null), new MappingColumn(1, PersonFields.Registration, null), new MappingColumn(2, PersonFields.Registration, null)]),
        new TabularMapping(";", true, [new MappingColumn(-1, PersonFields.DisplayName, null), new MappingColumn(1, PersonFields.Registration, null)]),
        new TabularMapping(";", true, [new MappingColumn(0, PersonFields.DisplayName, null), new MappingColumn(1, PersonFields.Registration, null), new MappingColumn(2, "password", null)]),
        new TabularMapping(";", true, [new MappingColumn(0, PersonFields.DisplayName, null), new MappingColumn(1, PersonFields.Registration, null), new MappingColumn(2, PersonFields.ValidTo, null)]),
    ];

    [Theory]
    [MemberData(nameof(InvalidMappings))]
    public void Invalid_mapping_is_explained(TabularMapping? mapping)
    {
        Assert.NotNull(TabularMappingRules.Validate(mapping));
    }
}
