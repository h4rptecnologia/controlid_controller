using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Platform.Modules.DeviceIntegration.ControlId;

namespace Platform.ContractTests;

public sealed class LoadObjectsTests : IAsyncLifetime
{
    private static readonly CancellationToken None = CancellationToken.None;

    private readonly System.Security.Cryptography.X509Certificates.X509Certificate2 certificate = TestCertificates.SelfSigned();
    private readonly CapturingLogger logger = new();
    private ControlIdSimulator simulator = null!;

    public async Task InitializeAsync() => simulator = await ControlIdSimulator.StartAsync(certificate);

    public async Task DisposeAsync()
    {
        await simulator.DisposeAsync();
        certificate.Dispose();
    }

    private ControlIdHttpClient Client(WhereDialect dialect = WhereDialect.ObjectMap, int attempts = 3) =>
        new(
            DeviceEndpoint.Https(simulator.Address, DeviceTlsPolicy.PinnedCertificate(TestCertificates.Pin(certificate))),
            _ => Task.FromResult(new DeviceCredentials(ControlIdSimulator.ValidLogin, ControlIdSimulator.ValidPassword)),
            new ControlIdClientOptions { Enabled = true, WhereDialect = dialect, MaxReadAttempts = attempts, RetryBaseDelay = TimeSpan.FromMilliseconds(2) },
            logger);

    private void SeedUsers(int count) =>
        simulator.Tables["users"] = Enumerable.Range(1, count)
            .Select(i => $$"""{"id":{{i}},"registration":"M{{i}}","name":"Pessoa {{i}}","password":"HASHSECRETO","salt":"SALSECRETO"}""")
            .ToList();

    private static async Task<List<DeviceObjectPage>> AllPages(ControlIdHttpClient client, LoadObjectsRequest request)
    {
        // Com prazo: uma regressao que faca a leitura repetir sem fim precisa falhar, nao travar o CI.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pages = new List<DeviceObjectPage>();
        await foreach (var page in client.ReadAllObjectsAsync(request, deadline.Token))
        {
            pages.Add(page);
        }

        return pages;
    }

    private static async Task<DeviceFailure> FailureOf(Func<Task> call) =>
        (await Assert.ThrowsAsync<DeviceCallException>(call)).Failure;

    // Contrato

    [Fact]
    public async Task Request_follows_the_documented_shape()
    {
        SeedUsers(1);
        using var client = Client();

        await client.LoadObjectsPageAsync(new LoadObjectsRequest("users", PageSize: 10), None);

        var request = simulator.Requests.Single(r => r.Path == "/load_objects.fcgi");
        Assert.StartsWith("?session=", request.Query, StringComparison.Ordinal);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("users", body.RootElement.GetProperty("object").GetString());
        Assert.Equal(10, body.RootElement.GetProperty("limit").GetInt32());
        Assert.Equal(["ascending", "id"], body.RootElement.GetProperty("order").EnumerateArray().Select(o => o.GetString()));
    }

    [Fact]
    public async Task Object_map_where_follows_the_documented_text()
    {
        SeedUsers(5);
        using var client = Client();

        await client.LoadObjectsPageAsync(new LoadObjectsRequest("users", new DeviceObjectCursor("2", 0)), None);

        using var body = JsonDocument.Parse(simulator.Requests.Single(r => r.Path == "/load_objects.fcgi").Body);
        Assert.Equal(2, body.RootElement.GetProperty("where").GetProperty("users").GetProperty("id").GetProperty(">").GetInt64());
    }

    [Fact]
    public async Task Condition_list_where_follows_the_documented_example()
    {
        SeedUsers(5);
        simulator.UnderstoodWhere = "list";
        using var client = Client(WhereDialect.ConditionList);

        var rows = (await AllPages(client, new LoadObjectsRequest("users", PageSize: 2))).SelectMany(p => p.Rows).ToList();

        Assert.Equal(5, rows.Count);
        using var body = JsonDocument.Parse(simulator.Requests.Last(r => r.Path == "/load_objects.fcgi").Body);
        var condition = body.RootElement.GetProperty("where")[0];
        Assert.Equal("users", condition.GetProperty("object").GetString());
        Assert.Equal("id", condition.GetProperty("field").GetString());
        Assert.Equal(">", condition.GetProperty("operator").GetString());
    }

    // Paginacao

    [Fact]
    public async Task Reads_every_row_once_across_pages()
    {
        SeedUsers(7);
        using var client = Client();

        var pages = await AllPages(client, new LoadObjectsRequest("users", PageSize: 3));

        Assert.Equal(["1", "2", "3", "4", "5", "6", "7"], pages.SelectMany(p => p.Rows).Select(r => r.SourceKey));
        Assert.All(pages, p => Assert.Equal(PagingMode.Keyset, p.Paging));
    }

    [Fact]
    public async Task Exact_multiple_of_page_size_ends_cleanly()
    {
        SeedUsers(6);
        using var client = Client();

        var pages = await AllPages(client, new LoadObjectsRequest("users", PageSize: 3));

        Assert.Equal(6, pages.Sum(p => p.Rows.Count));
        Assert.False(pages[^1].HasMore);
    }

    [Fact]
    public async Task Empty_object_returns_one_empty_page()
    {
        using var client = Client();
        simulator.Tables["users"] = [];

        var page = Assert.Single(await AllPages(client, new LoadObjectsRequest("users")));

        Assert.Empty(page.Rows);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task Rows_inserted_or_removed_during_reading_cause_no_repeat_or_loss_with_keyset()
    {
        SeedUsers(6);
        var pages = 0;
        simulator.BeforeEachPage = () =>
        {
            if (++pages == 2)
            {
                var table = simulator.Tables["users"];
                table.RemoveAt(0); // Remove uma linha ja lida: com offset, a linha 4 seria pulada.
                table.Add("""{"id":100,"registration":"N","name":"Nova"}""");
            }
        };
        using var client = Client();

        var keys = (await AllPages(client, new LoadObjectsRequest("users", PageSize: 3))).SelectMany(p => p.Rows).Select(r => r.SourceKey);

        Assert.Equal(["1", "2", "3", "4", "5", "6", "100"], keys);
    }

    [Fact]
    public async Task Reading_resumes_from_a_saved_cursor()
    {
        SeedUsers(5);
        using var client = Client();

        var first = await client.LoadObjectsPageAsync(new LoadObjectsRequest("users", PageSize: 2), None);
        var cursor = JsonSerializer.Deserialize<DeviceObjectCursor>(JsonSerializer.Serialize(first.NextCursor))!;

        var keys = (await AllPages(client, new LoadObjectsRequest("users", cursor, PageSize: 2))).SelectMany(p => p.Rows).Select(r => r.SourceKey);

        Assert.Equal(["3", "4", "5"], keys);
    }

    [Fact]
    public async Task Object_without_id_uses_offset_and_declares_best_effort()
    {
        simulator.Tables["user_groups"] = Enumerable.Range(1, 5).Select(i => $$"""{"user_id":{{i}},"group_id":9}""").ToList();
        using var client = Client();

        var pages = await AllPages(client, new LoadObjectsRequest("user_groups", PageSize: 2));

        Assert.Equal(["1:9", "2:9", "3:9", "4:9", "5:9"], pages.SelectMany(p => p.Rows).Select(r => r.SourceKey));
        Assert.All(pages, p => Assert.Equal(PagingMode.OffsetBestEffort, p.Paging));
    }

    // Firmware fora do contrato: parar, nunca repetir dados

    [Fact]
    public async Task Ignored_where_stops_instead_of_looping()
    {
        SeedUsers(5);
        simulator.UnderstoodWhere = "list"; // Este "firmware" nao entende o formato enviado.
        using var client = Client(WhereDialect.ObjectMap);

        Assert.Equal(DeviceFailure.UnexpectedBehavior, await FailureOf(() => AllPages(client, new LoadObjectsRequest("users", PageSize: 2))));
        Assert.Equal(2, simulator.Calls.Count(c => c == "/load_objects.fcgi"));
    }

    [Fact]
    public async Task Ignored_limit_is_refused()
    {
        SeedUsers(5);
        simulator.IgnoreLimit = true;
        using var client = Client();

        Assert.Equal(DeviceFailure.UnexpectedBehavior, await FailureOf(() => client.LoadObjectsPageAsync(new LoadObjectsRequest("users", PageSize: 2), None)));
    }

    [Fact]
    public async Task Ignored_offset_stops_instead_of_looping()
    {
        simulator.Tables["user_groups"] = Enumerable.Range(1, 5).Select(i => $$"""{"user_id":{{i}},"group_id":9}""").ToList();
        simulator.IgnoreOffset = true;
        using var client = Client();

        Assert.Equal(DeviceFailure.UnexpectedBehavior, await FailureOf(() => AllPages(client, new LoadObjectsRequest("user_groups", PageSize: 2))));
    }

    [Fact]
    public async Task Out_of_order_rows_are_refused()
    {
        simulator.Overrides["/load_objects.fcgi"] = c =>
        {
            c.Response.ContentType = "application/json";
            return c.Response.WriteAsync("""{"users":[{"id":5},{"id":3}]}""");
        };
        using var client = Client();

        Assert.Equal(DeviceFailure.UnexpectedBehavior, await FailureOf(() => client.LoadObjectsPageAsync(new LoadObjectsRequest("users"), None)));
    }

    [Theory]
    [InlineData("""{"outro":[]}""")]
    [InlineData("""{"users":{"id":1}}""")]
    [InlineData("""{"users":[1]}""")]
    [InlineData("""{"users":[{"name":"sem id"}]}""")]
    [InlineData("""{"users":[{"id":"nao-numero"}]}""")]
    [InlineData("""{"users":[{"id":1.5}]}""")]
    [InlineData("""{"users":[{"id":1e3}]}""")]
    public async Task Malformed_page_is_invalid_response(string body)
    {
        simulator.Overrides["/load_objects.fcgi"] = c =>
        {
            c.Response.ContentType = "application/json";
            return c.Response.WriteAsync(body);
        };
        using var client = Client();

        Assert.Equal(DeviceFailure.InvalidResponse, await FailureOf(() => client.LoadObjectsPageAsync(new LoadObjectsRequest("users"), None)));
    }

    // Precisao numerica

    [Fact]
    public async Task Ids_and_card_values_above_2_to_53_keep_every_digit()
    {
        simulator.Tables["users"] = ["""{"id":9007199254740993,"name":"A"}"""];
        simulator.Tables["cards"] = ["""{"id":1,"value":18446744073709551615,"user_id":9007199254740993}"""];
        using var client = Client();

        var user = (await client.LoadObjectsPageAsync(new LoadObjectsRequest("users"), None)).Rows.Single();
        Assert.Equal("9007199254740993", user.SourceKey);

        var card = (await client.LoadObjectsPageAsync(new LoadObjectsRequest("cards", IncludeSensitive: ["value"]), None)).Rows.Single();
        Assert.Equal("18446744073709551615", card.Sensitive.Reveal("value"));
        Assert.Equal("9007199254740993", card.Fields["user_id"].GetRawText());
    }

    [Fact]
    public async Task Cursor_above_2_to_53_is_sent_without_rounding()
    {
        simulator.Tables["users"] = [];
        using var client = Client();

        await client.LoadObjectsPageAsync(new LoadObjectsRequest("users", new DeviceObjectCursor("9007199254740993", 0)), None);

        Assert.Contains(":9007199254740993", simulator.Requests.Single(r => r.Path == "/load_objects.fcgi").Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Wiegand_conversion_matches_the_official_example()
    {
        Assert.Equal("528281023086", new WiegandCard(123, 45678).ToApiValue());
        Assert.Equal(new WiegandCard(123, 45678), WiegandCard.FromApiValue("528281023086"));
        Assert.Equal(new WiegandCard(uint.MaxValue, uint.MaxValue), WiegandCard.FromApiValue("18446744073709551615"));
    }

    [Theory]
    [InlineData("18446744073709551616")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("")]
    public void Invalid_card_value_is_refused(string value)
    {
        Assert.Throws<FormatException>(() => WiegandCard.FromApiValue(value));
    }

    // Dados sensiveis

    [Fact]
    public async Task Sensitive_fields_are_not_requested_by_default()
    {
        SeedUsers(1);
        using var client = Client();

        var page = await client.LoadObjectsPageAsync(new LoadObjectsRequest("users"), None);

        var body = simulator.Requests.Single(r => r.Path == "/load_objects.fcgi").Body;
        Assert.DoesNotContain("password", body, StringComparison.Ordinal);
        Assert.DoesNotContain("salt", body, StringComparison.Ordinal);
        Assert.False(page.ProjectionIgnored);
        Assert.Equal("Pessoa 1", page.Rows[0].Fields["name"].GetString());
        Assert.Equal(0, page.Rows[0].Sensitive.Count);
    }

    [Fact]
    public async Task Pin_in_access_logs_is_dropped_even_if_the_device_sends_it()
    {
        simulator.IgnoreFields = true;
        simulator.Tables["access_logs"] = ["""{"id":1,"time":1700000000,"event":7,"user_id":3,"pin_value":"4321PINSECRETO","card_value":99,"qrcode_value":"QRSECRETO"}"""];
        using var client = Client();

        var page = await client.LoadObjectsPageAsync(new LoadObjectsRequest("access_logs"), None);
        var row = page.Rows.Single();

        Assert.True(page.ProjectionIgnored);
        Assert.Equal(7, row.Fields["event"].GetInt32());
        Assert.False(row.Fields.ContainsKey("pin_value"));
        Assert.False(row.Fields.ContainsKey("card_value"));
        Assert.False(row.Fields.ContainsKey("qrcode_value"));
        Assert.Equal(0, row.Sensitive.Count);

        var everything = JsonSerializer.Serialize(page) + page + row;
        Assert.DoesNotContain("PINSECRETO", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("QRSECRETO", everything, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("access_logs", "pin_value")]
    [InlineData("network_interlocking_rules", "password")]
    [InlineData("network_interlocking_rules", "login")]
    [InlineData("users", "campo_inventado")]
    [InlineData("users", "name")]
    public async Task Forbidden_field_cannot_be_requested(string table, string field)
    {
        using var client = Client();

        await Assert.ThrowsAsync<ArgumentException>(() => client.LoadObjectsPageAsync(new LoadObjectsRequest(table, IncludeSensitive: [field]), None));
        Assert.Empty(simulator.Calls);
    }

    [Fact]
    public async Task Requested_sensitive_field_stays_out_of_logs_and_serialization()
    {
        simulator.Tables["templates"] = ["""{"id":1,"finger_type":0,"user_id":3,"template":"BIOMETRIASECRETA"}"""];
        using var client = Client();

        var page = await client.LoadObjectsPageAsync(new LoadObjectsRequest("templates", IncludeSensitive: ["template"]), None);
        var row = page.Rows.Single();

        Assert.Equal("BIOMETRIASECRETA", row.Sensitive.Reveal("template"));
        Assert.False(row.Fields.ContainsKey("template"));
        Assert.DoesNotContain("BIOMETRIASECRETA", JsonSerializer.Serialize(row) + row + page, StringComparison.Ordinal);
        Assert.DoesNotContain(logger.Lines, l => l.Contains("BIOMETRIASECRETA", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Biometric_template_is_not_requested_by_default()
    {
        simulator.Tables["templates"] = ["""{"id":1,"finger_type":0,"user_id":3,"template":"BIOMETRIASECRETA"}"""];
        using var client = Client();

        await client.LoadObjectsPageAsync(new LoadObjectsRequest("templates"), None);

        Assert.DoesNotContain("template\"", simulator.Requests.Single(r => r.Path == "/load_objects.fcgi").Body.Replace("\"templates\"", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Field_unknown_to_the_catalog_has_its_value_dropped_and_name_reported()
    {
        simulator.Overrides["/load_objects.fcgi"] = c =>
        {
            c.Response.ContentType = "application/json";
            return c.Response.WriteAsync("""{"users":[{"id":1,"name":"A","campo_de_firmware_novo":"VALORDESCONHECIDO"}]}""");
        };
        using var client = Client();

        var page = await client.LoadObjectsPageAsync(new LoadObjectsRequest("users"), None);

        Assert.Equal(["campo_de_firmware_novo"], page.UnclassifiedFields);
        Assert.DoesNotContain("VALORDESCONHECIDO", JsonSerializer.Serialize(page), StringComparison.Ordinal);
    }

    // Catalogo fechado

    [Theory]
    [InlineData("objeto_inventado")]
    [InlineData("users; DROP")]
    [InlineData("sec_boxes")] // A grafia documentada e sec_boxs.
    public async Task Object_outside_the_catalog_is_refused_without_calling_the_device(string name)
    {
        using var client = Client();

        await Assert.ThrowsAsync<ArgumentException>(() => client.LoadObjectsPageAsync(new LoadObjectsRequest(name), None));
        Assert.Empty(simulator.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public async Task Invalid_page_size_is_refused(int size)
    {
        using var client = Client();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.LoadObjectsPageAsync(new LoadObjectsRequest("users", PageSize: size), None));
    }

    [Theory]
    [InlineData("1}],\"object\":\"pins")]
    [InlineData("abc")]
    public async Task Forged_cursor_cannot_inject_into_the_request(string cursor)
    {
        using var client = Client();

        await Assert.ThrowsAsync<DeviceCallException>(() => client.LoadObjectsPageAsync(new LoadObjectsRequest("users", new DeviceObjectCursor(cursor, 0)), None));
        Assert.Empty(simulator.Calls);
    }

    [Fact]
    public void Catalog_covers_documented_objects_and_keeps_vendor_spelling()
    {
        Assert.True(ObjectCatalog.Objects.Count >= 40);
        Assert.Contains("sec_boxs", ObjectCatalog.Objects.Keys);
        Assert.DoesNotContain("sec_boxes", ObjectCatalog.Objects.Keys);
        Assert.Contains("pin_value", ObjectCatalog.Objects["access_logs"].Never);
        Assert.Contains("password", ObjectCatalog.Objects["network_interlocking_rules"].Never);
        Assert.Contains("template", ObjectCatalog.Objects["templates"].Sensitive);
        Assert.Contains("value", ObjectCatalog.Objects["pins"].Sensitive);
        Assert.Contains("value", ObjectCatalog.Objects["cards"].Sensitive);
    }

    [Fact]
    public void No_sensitive_field_is_also_classified_safe()
    {
        foreach (var definition in ObjectCatalog.Objects.Values)
        {
            Assert.Empty(definition.Safe.Intersect(definition.Sensitive));
            Assert.Empty(definition.Safe.Intersect(definition.Never));
            Assert.All(definition.Key, key => Assert.Contains(key, definition.Safe));
        }
    }

    // Novas tentativas

    [Fact]
    public async Task Transient_failure_is_retried_on_read()
    {
        SeedUsers(1);
        var failures = 2;
        simulator.Overrides["/load_objects.fcgi"] = async c =>
        {
            if (Interlocked.Decrement(ref failures) >= 0)
            {
                c.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            c.Response.ContentType = "application/json";
            await c.Response.WriteAsync("""{"users":[{"id":1}]}""");
        };
        using var client = Client();

        var page = await client.LoadObjectsPageAsync(new LoadObjectsRequest("users"), None);

        Assert.Single(page.Rows);
        Assert.Equal(3, simulator.Calls.Count(c => c == "/load_objects.fcgi"));
    }

    [Fact]
    public async Task Retries_stop_at_the_configured_limit()
    {
        simulator.Overrides["/load_objects.fcgi"] = c =>
        {
            c.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return Task.CompletedTask;
        };
        using var client = Client(attempts: 3);

        Assert.Equal(DeviceFailure.DeviceError, await FailureOf(() => client.LoadObjectsPageAsync(new LoadObjectsRequest("users"), None)));
        Assert.Equal(3, simulator.Calls.Count(c => c == "/load_objects.fcgi"));
    }

    [Theory]
    [InlineData(StatusCodes.Status400BadRequest)]
    [InlineData(StatusCodes.Status404NotFound)]
    public async Task Client_errors_are_not_retried(int status)
    {
        simulator.Overrides["/load_objects.fcgi"] = c =>
        {
            c.Response.StatusCode = status;
            return Task.CompletedTask;
        };
        using var client = Client();

        Assert.Equal(DeviceFailure.DeviceError, await FailureOf(() => client.LoadObjectsPageAsync(new LoadObjectsRequest("users"), None)));
        Assert.Single(simulator.Calls, c => c == "/load_objects.fcgi");
    }

    [Fact]
    public async Task Contract_violation_is_not_retried()
    {
        SeedUsers(5);
        simulator.IgnoreLimit = true;
        using var client = Client();

        await FailureOf(() => client.LoadObjectsPageAsync(new LoadObjectsRequest("users", PageSize: 2), None));

        Assert.Single(simulator.Calls, c => c == "/load_objects.fcgi");
    }

    [Fact]
    public async Task Session_expired_during_paging_recovers_without_losing_rows()
    {
        SeedUsers(5);
        var pages = 0;
        simulator.BeforeEachPage = () =>
        {
            if (++pages == 2)
            {
                simulator.InvalidateSession();
            }
        };
        using var client = Client();

        // O simulador autoriza antes do gancho: invalida para a pagina seguinte.
        var keys = (await AllPages(client, new LoadObjectsRequest("users", PageSize: 2))).SelectMany(p => p.Rows).Select(r => r.SourceKey);

        Assert.Equal(["1", "2", "3", "4", "5"], keys);
        Assert.Equal(2, simulator.LoginCount);
    }
}
