using Platform.Modules.AccessPolicy;
using Platform.Modules.People;
using Platform.Shared.Storage;

namespace Platform.UnitTests;

public sealed class CredentialValueTests
{
    [Theory]
    [InlineData("18446744073709551615", "18446744073709551615")]
    [InlineData("007", "7")]
    [InlineData("528281023086", "528281023086")]
    public void Card_is_decimal_up_to_unsigned_64_bits(string value, string expected)
    {
        Assert.True(CredentialValue.TryNormalize(CredentialKinds.Card, value, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void Card_in_area_and_number_format_follows_the_formula_in_the_documentation()
    {
        // Exemplo da Lista de Objetos: 123,45678 e enviado como 528281023086.
        Assert.True(CredentialValue.TryNormalize(CredentialKinds.Card, "123,45678", out var normalized));
        Assert.Equal("528281023086", normalized);
    }

    [Theory]
    [InlineData("18446744073709551616")]
    [InlineData("-1")]
    [InlineData("12a")]
    [InlineData(" 12")]
    [InlineData("1.5")]
    [InlineData("")]
    [InlineData(",5")]
    [InlineData("5,")]
    [InlineData("1,2,3")]
    [InlineData("4294967296,1")]
    [InlineData("1,4294967296")]
    public void Invalid_card_is_refused(string value)
    {
        Assert.False(CredentialValue.TryNormalize(CredentialKinds.Card, value, out _));
    }

    [Theory]
    [InlineData("1234", true)]
    [InlineData("0000000000000000", true)]
    [InlineData("123", false)]
    [InlineData("12345678901234567", false)]
    [InlineData("12a4", false)]
    public void Pin_is_only_digits_and_keeps_leading_zeros(string value, bool valid)
    {
        Assert.Equal(valid, CredentialValue.TryNormalize(CredentialKinds.Pin, value, out var normalized));
        if (valid)
        {
            Assert.Equal(value, normalized);
        }
    }

    [Theory]
    [InlineData(CredentialKinds.Qr)]
    [InlineData(CredentialKinds.Uhf)]
    public void Text_credentials_stay_exact(string kind)
    {
        Assert.True(CredentialValue.TryNormalize(kind, " AbC-01 ", out var normalized));
        Assert.Equal(" AbC-01 ", normalized);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("a\nb")]
    public void Blank_or_control_characters_are_refused(string value)
    {
        Assert.False(CredentialValue.TryNormalize(CredentialKinds.Qr, value, out _));
    }

    [Fact]
    public void Text_credential_has_a_size_limit()
    {
        Assert.True(CredentialValue.TryNormalize(CredentialKinds.Qr, new string('a', 512), out _));
        Assert.False(CredentialValue.TryNormalize(CredentialKinds.Qr, new string('a', 513), out _));
    }

    [Theory]
    [InlineData("fingerprint_template")]
    [InlineData("password")]
    [InlineData(null)]
    public void Biometrics_and_passwords_are_not_supported_here(string? kind)
    {
        Assert.False(CredentialKinds.IsSupported(kind));
        Assert.False(CredentialValue.TryNormalize(kind, "1234", out _));
    }

    [Fact]
    public void Pin_is_never_searchable()
    {
        Assert.False(CredentialKinds.IsSearchable(CredentialKinds.Pin));
        Assert.True(CredentialKinds.IsSearchable(CredentialKinds.Card));
    }

    [Theory]
    [InlineData(CredentialKinds.Pin, "12345678", "****")]
    [InlineData(CredentialKinds.Card, "1234567", "****")]
    [InlineData(CredentialKinds.Card, "528281023086", "****3086")]
    public void Hint_never_reveals_a_pin_nor_most_of_a_value(string kind, string value, string expected)
    {
        Assert.Equal(expected, CredentialValue.Hint(kind, value));
    }

    [Fact]
    public async Task Pin_cannot_be_indexed_even_by_mistake()
    {
        var protector = new CredentialProtector(new NoKeys());

        await Assert.ThrowsAsync<InvalidOperationException>(() => protector.IndexAsync(Guid.NewGuid(), CredentialKinds.Pin, "1234", CancellationToken.None));
    }

    private sealed class NoKeys : IKeyService
    {
        public Task<string> WrapAsync(string keyName, byte[] dek, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<byte[]> UnwrapAsync(string keyName, string wrappedDek, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> RewrapAsync(string keyName, string wrappedDek, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RotateAsync(string keyName, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task EnsureKeyAsync(string keyName, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<byte[]>> HmacAsync(string keyName, IReadOnlyList<byte[]> inputs, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

public sealed class PersonRulesTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Valid_person_has_no_problem()
    {
        Assert.Null(PeopleEndpoints.Validate("Ana", "007", "employee", Start, Start.AddDays(1)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("Ana\nSouza")]
    public void Name_is_required_and_clean(string? name)
    {
        Assert.NotNull(PeopleEndpoints.Validate(name, null, "employee", null, null));
    }

    [Fact]
    public void Name_has_a_limit()
    {
        Assert.Null(PeopleEndpoints.Validate(new string('a', 200), null, "employee", null, null));
        Assert.NotNull(PeopleEndpoints.Validate(new string('a', 201), null, "employee", null, null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a\tb")]
    public void Registration_is_clean_when_present(string registration)
    {
        Assert.NotNull(PeopleEndpoints.Validate("Ana", registration, "employee", null, null));
    }

    [Fact]
    public void Inverted_validity_is_refused_and_equal_is_accepted()
    {
        Assert.NotNull(PeopleEndpoints.Validate("Ana", null, "employee", Start.AddDays(1), Start));
        Assert.Null(PeopleEndpoints.Validate("Ana", null, "employee", Start, Start));
    }

    [Fact]
    public void Visitor_needs_an_end_date()
    {
        Assert.NotNull(PeopleEndpoints.Validate("Ana", null, "visitor", Start, null));
        Assert.Null(PeopleEndpoints.Validate("Ana", null, "visitor", null, Start));
    }

    [Fact]
    public void Unknown_type_is_refused()
    {
        Assert.NotNull(PeopleEndpoints.Validate("Ana", null, "chefe", null, null));
    }

    [Theory]
    [InlineData("123456", "****56")]
    [InlineData("12345", "****")]
    [InlineData("", "")]
    public void Registration_mask(string value, string expected)
    {
        Assert.Equal(expected, PeopleEndpoints.MaskRegistration(value));
    }

    [Theory]
    [InlineData("100%", @"100\%")]
    [InlineData("a_b", @"a\_b")]
    [InlineData(@"a\b", @"a\\b")]
    public void Search_term_cannot_become_a_wildcard(string term, string expected)
    {
        Assert.Equal(expected, PeopleEndpoints.EscapeLike(term));
    }
}

public sealed class ScheduleRulesTests
{
    private static readonly string[] Monday = ["mon"];

    private static SpanRequest Span(int start, int end, IReadOnlyList<string>? days = null, IReadOnlyList<int>? holidays = null) => new(start, end, days ?? Monday, holidays);

    [Fact]
    public void Valid_schedule_has_no_problem()
    {
        Assert.Null(AccessEndpoints.ValidateSchedule("America/Sao_Paulo", [Span(0, 86400)]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Lua/Crateras")]
    [InlineData("UTC")]
    public void Timezone_must_be_explicit_and_real(string? timezone)
    {
        Assert.NotNull(AccessEndpoints.ValidateSchedule(timezone, [Span(0, 3600)]));
    }

    [Fact]
    public void Crossing_midnight_is_refused_with_the_way_to_do_it()
    {
        var problem = AccessEndpoints.ValidateSchedule("America/Sao_Paulo", [Span(79200, 21600)]);

        Assert.Contains("dois intervalos", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Night_shift_as_two_spans_is_accepted()
    {
        Assert.Null(AccessEndpoints.ValidateSchedule("America/Sao_Paulo", [Span(79200, 86400), Span(0, 21600)]));
    }

    [Theory]
    [InlineData(-1, 100)]
    [InlineData(86400, 86400)]
    [InlineData(0, 86401)]
    [InlineData(100, 100)]
    public void Span_must_fit_the_day(int start, int end)
    {
        Assert.NotNull(AccessEndpoints.ValidateSchedule("America/Sao_Paulo", [Span(start, end)]));
    }

    [Fact]
    public void Span_needs_a_day_or_holiday_and_valid_values()
    {
        Assert.NotNull(AccessEndpoints.ValidateSchedule("America/Sao_Paulo", [Span(0, 100, [], [])]));
        Assert.Null(AccessEndpoints.ValidateSchedule("America/Sao_Paulo", [Span(0, 100, [], [1])]));
        Assert.NotNull(AccessEndpoints.ValidateSchedule("America/Sao_Paulo", [Span(0, 100, ["segunda"])]));
        Assert.NotNull(AccessEndpoints.ValidateSchedule("America/Sao_Paulo", [Span(0, 100, [], [4])]));
    }

    [Fact]
    public void Schedule_needs_at_least_one_span_and_has_a_limit()
    {
        Assert.NotNull(AccessEndpoints.ValidateSchedule("America/Sao_Paulo", []));
        Assert.NotNull(AccessEndpoints.ValidateSchedule("America/Sao_Paulo", null));
        Assert.NotNull(AccessEndpoints.ValidateSchedule("America/Sao_Paulo", Enumerable.Repeat(Span(0, 100), 51).ToList()));
    }

    [Fact]
    public void Policy_needs_someone_and_somewhere()
    {
        var id = new[] { Guid.NewGuid() };

        Assert.NotNull(AccessEndpoints.ValidateSpecShape(null));
        Assert.NotNull(AccessEndpoints.ValidateSpecShape(new PolicySpec(null, null, id, null)));
        Assert.NotNull(AccessEndpoints.ValidateSpecShape(new PolicySpec(id, null, null, null)));
        Assert.Null(AccessEndpoints.ValidateSpecShape(new PolicySpec(id, null, id, null)));
        Assert.Null(AccessEndpoints.ValidateSpecShape(new PolicySpec(null, id, id, null)));
    }
}
