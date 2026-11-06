using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using Platform.Shared.Kernel.Authorization;
using Platform.Shared.Storage;

namespace Platform.IntegrationTests;

/// <summary>Cadastro canonico com banco e KMS reais, nos papeis de producao.</summary>
[Collection(PostgresTests.Name)]
public sealed class PeopleAccessTests(PostgresFixture postgres, StorageFixture storage) : IDisposable
{
    private readonly TestData data = new(postgres.AdminOnHubConnection);
    private readonly EdgeHost host = new(postgres, storage);
    private readonly HttpClient kms = storage.CreateKmsClient();

    public void Dispose()
    {
        kms.Dispose();
        host.Dispose();
    }

    private static Uri Url(string path) => new(path, UriKind.Relative);

    private static readonly string[] AllPeople = [Permissions.PeopleRead, Permissions.PeopleWrite, Permissions.PeopleRevoke, Permissions.CredentialsManage, Permissions.PoliciesRead, Permissions.PoliciesWrite];

    private static readonly string[] Monday = ["mon"];
    private static readonly string[] Tuesday = ["tue"];
    private static readonly int[] HolidayOne = [1];

    private sealed record Org(HttpClient Client, Guid Site, Guid Tenant, string Slug);

    private async Task<Org> OrgAsync()
    {
        var tenant = await data.TenantAsync();
        var site = await data.SiteAsync(tenant.Id);
        return new Org(host.AdminClient(await data.ActorAsync(tenant, AllPeople)), site, tenant.Id, tenant.Slug);
    }

    private async Task<HttpClient> ClientAsync(Org org, params string[] permissions) =>
        host.AdminClient(await data.ActorAsync((org.Tenant, org.Slug), permissions));

    private async Task<HttpClient> ScopedAsync(Org org, Guid site, params string[] permissions)
    {
        var actor = await data.ActorAsync((org.Tenant, org.Slug));
        foreach (var permission in permissions)
        {
            await data.GrantAsync(actor, permission, site);
        }

        return host.AdminClient(actor);
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await BodyAsync(response)).GetProperty("code").GetString();

    private static Task<HttpResponseMessage> CreatePersonAsync(HttpClient client, Guid site, string name = "Ana Souza", string? registration = "MAT001", string? type = null, DateTimeOffset? to = null) =>
        client.PostAsJsonAsync(Url("/api/v1/people"), new { siteId = site, displayName = name, registration, personType = type, validTo = to });

    private static async Task<Guid> PersonAsync(HttpClient client, Guid site, string name = "Ana Souza", string? registration = "MAT001")
    {
        var response = await CreatePersonAsync(client, site, name, registration);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await BodyAsync(response)).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> PatchAsync(HttpClient client, Guid id, object body, string? ifMatch)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, Url($"/api/v1/people/{id}")) { Content = JsonContent.Create(body) };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> AddCredentialAsync(HttpClient client, Guid person, string kind, string value) =>
        client.PostAsJsonAsync(Url($"/api/v1/people/{person}/credentials"), new { kind, value });

    private async Task<long> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(postgres.AdminOnHubConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    // Pessoas

    [Fact]
    public async Task Person_is_created_with_an_internal_id_audited_and_published()
    {
        var org = await OrgAsync();
        var created = await BodyAsync(await CreatePersonAsync(org.Client, org.Site));
        var id = created.GetProperty("id").GetGuid();

        Assert.Equal("MAT001", created.GetProperty("registration").GetString());
        Assert.Equal("employee", created.GetProperty("personType").GetString());
        Assert.Equal(1, created.GetProperty("version").GetInt32());
        Assert.Equal(1, await ScalarAsync("SELECT count(*) FROM hub.audit_log WHERE resource_id = @r AND action = 'person.create'", ("r", id.ToString())));
        Assert.Equal(1, await ScalarAsync("SELECT count(*) FROM hub.outbox WHERE aggregate_id = @a AND topic = 'person.created.v1'", ("a", id)));
    }

    [Fact]
    public async Task Audit_and_outbox_do_not_carry_personal_data()
    {
        var org = await OrgAsync();
        var id = await PersonAsync(org.Client, org.Site, "MARCADORNOME Souza", "MARCADORMAT");
        await PatchAsync(org.Client, id, new { displayName = "MARCADORNOVO Lima" }, "\"1\"");

        foreach (var table in new[] { "audit_log", "outbox", "idempotency_keys" })
        {
            Assert.Equal(0, await ScalarAsync($"SELECT count(*) FROM hub.{table} t WHERE t.tenant_id = @t AND t::text LIKE '%MARCADOR%'", ("t", org.Tenant)));
        }
    }

    [Fact]
    public async Task Same_registration_in_the_same_site_is_refused_and_in_another_site_is_allowed()
    {
        var org = await OrgAsync();
        await PersonAsync(org.Client, org.Site);

        var repeated = await CreatePersonAsync(org.Client, org.Site, "Outra Pessoa");
        Assert.Equal(HttpStatusCode.Conflict, repeated.StatusCode);
        Assert.Equal("registration_in_use", await CodeAsync(repeated));

        var otherSite = await data.SiteAsync(org.Tenant, "Outra");
        Assert.Equal(HttpStatusCode.Created, (await CreatePersonAsync(org.Client, otherSite, "Outra Pessoa")).StatusCode);
    }

    [Fact]
    public async Task Visitor_without_an_end_date_is_refused()
    {
        var org = await OrgAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await CreatePersonAsync(org.Client, org.Site, type: "visitor")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await CreatePersonAsync(org.Client, org.Site, type: "visitor", to: DateTimeOffset.UtcNow.AddDays(1))).StatusCode);
    }

    [Fact]
    public async Task External_key_is_unique_and_cannot_be_changed_by_the_application()
    {
        var org = await OrgAsync();
        var body = new { siteId = org.Site, displayName = "Ana", sourceSystem = "rh", externalKey = "E-1" };
        var created = await org.Client.PostAsJsonAsync(Url("/api/v1/people"), body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await BodyAsync(created)).GetProperty("id").GetGuid();

        var keys = await BodyAsync(await org.Client.GetAsync(Url($"/api/v1/people/{id}/source-keys")));
        Assert.Equal("E-1", keys[0].GetProperty("externalKey").GetString());

        Assert.Equal(HttpStatusCode.Conflict, (await org.Client.PostAsJsonAsync(Url("/api/v1/people"), body with { displayName = "Outra" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await org.Client.PostAsJsonAsync(Url("/api/v1/people"), new { siteId = org.Site, displayName = "Ana", sourceSystem = "rh" })).StatusCode);

        await using var connection = new NpgsqlConnection(postgres.AppConnection);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var set = new NpgsqlCommand($"SELECT set_config('app.tenant_id', '{org.Tenant}', true)", connection, transaction);
        await set.ExecuteNonQueryAsync();
        await using var update = new NpgsqlCommand("UPDATE hub.entity_source_keys SET external_key = 'X'", connection, transaction);
        var denied = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
    }

    [Fact]
    public async Task Edit_requires_the_current_version()
    {
        var org = await OrgAsync();
        var id = await PersonAsync(org.Client, org.Site);

        Assert.Equal((HttpStatusCode)428, (await PatchAsync(org.Client, id, new { displayName = "Ana Lima" }, null)).StatusCode);

        var edited = await PatchAsync(org.Client, id, new { displayName = "Ana Lima" }, "\"1\"");
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal(2, (await BodyAsync(edited)).GetProperty("version").GetInt32());
        Assert.Equal("\"2\"", edited.Headers.ETag?.Tag);

        var stale = await PatchAsync(org.Client, id, new { displayName = "Ana Velha" }, "\"1\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal("version_conflict", await CodeAsync(stale));
        Assert.Equal("Ana Lima", (await BodyAsync(await org.Client.GetAsync(Url($"/api/v1/people/{id}")))).GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Edit_records_only_the_names_of_the_changed_fields()
    {
        var org = await OrgAsync();
        var id = await PersonAsync(org.Client, org.Site);

        await PatchAsync(org.Client, id, new { displayName = "Ana Lima" }, "\"1\"");

        Assert.Equal(1, await ScalarAsync(
            "SELECT count(*) FROM hub.audit_log WHERE resource_id = @r AND action = 'person.update' AND metadata -> 'changed' = '[\"display_name\"]'::jsonb",
            ("r", id.ToString())));
    }

    [Fact]
    public async Task Edit_can_clear_the_validity_only_when_asked()
    {
        var org = await OrgAsync();
        var created = await org.Client.PostAsJsonAsync(Url("/api/v1/people"), new { siteId = org.Site, displayName = "Ana", validTo = DateTimeOffset.UtcNow.AddDays(5) });
        var id = (await BodyAsync(created)).GetProperty("id").GetGuid();

        var kept = await BodyAsync(await PatchAsync(org.Client, id, new { displayName = "Ana L" }, "\"1\""));
        Assert.NotEqual(JsonValueKind.Null, kept.GetProperty("validTo").ValueKind);

        var cleared = await BodyAsync(await PatchAsync(org.Client, id, new { clearValidTo = true }, "\"2\""));
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("validTo").ValueKind);
    }

    [Fact]
    public async Task Search_finds_by_name_prefix_or_exact_registration_and_treats_wildcards_as_text()
    {
        var org = await OrgAsync();
        await PersonAsync(org.Client, org.Site, "Ana Souza", "MAT001");
        await PersonAsync(org.Client, org.Site, "Bruno Lima", "MAT002");

        async Task<int> Count(string term)
        {
            var page = await BodyAsync(await org.Client.GetAsync(Url($"/api/v1/people?search={Uri.EscapeDataString(term)}")));
            return page.GetProperty("items").GetArrayLength();
        }

        Assert.Equal(1, await Count("ana"));
        Assert.Equal(1, await Count("MAT002"));
        Assert.Equal(0, await Count("MAT"));
        Assert.Equal(0, await Count("%"));
        Assert.Equal(0, await Count("_na"));
    }

    [Fact]
    public async Task List_is_paginated()
    {
        var org = await OrgAsync();
        await PersonAsync(org.Client, org.Site, "A", "1");
        await PersonAsync(org.Client, org.Site, "B", "2");
        await PersonAsync(org.Client, org.Site, "C", "3");

        var first = await BodyAsync(await org.Client.GetAsync(Url("/api/v1/people?limit=2")));
        Assert.Equal(2, first.GetProperty("items").GetArrayLength());
        Assert.Equal(2, first.GetProperty("nextOffset").GetInt32());

        var second = await BodyAsync(await org.Client.GetAsync(Url("/api/v1/people?limit=2&offset=2")));
        Assert.Equal("C", second.GetProperty("items")[0].GetProperty("displayName").GetString());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextOffset").ValueKind);
    }

    [Fact]
    public async Task Reader_sees_the_registration_masked_and_editor_sees_it_whole()
    {
        var org = await OrgAsync();
        var id = await PersonAsync(org.Client, org.Site, "Ana", "MAT0012345");
        using var reader = await ClientAsync(org, Permissions.PeopleRead);

        var masked = await BodyAsync(await reader.GetAsync(Url($"/api/v1/people/{id}")));
        Assert.Equal("****45", masked.GetProperty("registration").GetString());
        Assert.True(masked.GetProperty("registrationMasked").GetBoolean());

        var list = await BodyAsync(await reader.GetAsync(Url("/api/v1/people")));
        Assert.Equal("****45", list.GetProperty("items")[0].GetProperty("registration").GetString());

        var whole = await BodyAsync(await org.Client.GetAsync(Url($"/api/v1/people/{id}")));
        Assert.Equal("MAT0012345", whole.GetProperty("registration").GetString());
        Assert.False(whole.GetProperty("registrationMasked").GetBoolean());
    }

    // Revogacao

    [Fact]
    public async Task Revoking_ends_the_credentials_blocks_edits_and_keeps_the_trail()
    {
        var org = await OrgAsync();
        var id = await PersonAsync(org.Client, org.Site);
        await AddCredentialAsync(org.Client, id, CredentialKinds.Card, "42");

        Assert.Equal(HttpStatusCode.BadRequest, (await org.Client.PostAsJsonAsync(Url($"/api/v1/people/{id}/revoke"), new { reason = " " })).StatusCode);

        var revoked = await org.Client.PostAsJsonAsync(Url($"/api/v1/people/{id}/revoke"), new { reason = "Desligada" });
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        Assert.Equal("revoked", (await BodyAsync(revoked)).GetProperty("state").GetString());

        var credentials = await BodyAsync(await org.Client.GetAsync(Url($"/api/v1/people/{id}/credentials")));
        Assert.NotEqual(JsonValueKind.Null, credentials[0].GetProperty("revokedAt").ValueKind);
        Assert.Equal(HttpStatusCode.Conflict, (await PatchAsync(org.Client, id, new { displayName = "X" }, "\"2\"")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await AddCredentialAsync(org.Client, id, CredentialKinds.Card, "43")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await org.Client.PostAsJsonAsync(Url($"/api/v1/people/{id}/revoke"), new { reason = "De novo" })).StatusCode);
        Assert.Equal(1, await ScalarAsync("SELECT count(*) FROM hub.outbox WHERE aggregate_id = @a AND topic = 'person.revoked.v1'", ("a", id)));
    }

    [Fact]
    public async Task Revoked_card_can_be_given_to_someone_else()
    {
        var org = await OrgAsync();
        var first = await PersonAsync(org.Client, org.Site, "Ana", "1");
        var second = await PersonAsync(org.Client, org.Site, "Bia", "2");
        await AddCredentialAsync(org.Client, first, CredentialKinds.Card, "42");
        await org.Client.PostAsJsonAsync(Url($"/api/v1/people/{first}/revoke"), new { reason = "Saiu" });

        Assert.Equal(HttpStatusCode.Created, (await AddCredentialAsync(org.Client, second, CredentialKinds.Card, "42")).StatusCode);
    }

    [Fact]
    public async Task Physical_deletion_is_not_possible_for_the_application()
    {
        var org = await OrgAsync();
        var id = await PersonAsync(org.Client, org.Site);

        await using var connection = new NpgsqlConnection(postgres.AppConnection);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var set = new NpgsqlCommand($"SELECT set_config('app.tenant_id', '{org.Tenant}', true)", connection, transaction);
        await set.ExecuteNonQueryAsync();
        await using var delete = new NpgsqlCommand($"DELETE FROM hub.people WHERE id = '{id}'", connection, transaction);

        var denied = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
    }

    // Credenciais

    [Fact]
    public async Task Credential_value_is_never_stored_in_clear_nor_returned()
    {
        var org = await OrgAsync();
        var id = await PersonAsync(org.Client, org.Site);

        var created = await AddCredentialAsync(org.Client, id, CredentialKinds.Card, "18446744073709551615");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var text = await created.Content.ReadAsStringAsync();
        Assert.DoesNotContain("18446744073709551615", text, StringComparison.Ordinal);
        Assert.Contains("****1615", text, StringComparison.Ordinal);

        await AddCredentialAsync(org.Client, id, CredentialKinds.Pin, "908172");
        var pin = await BodyAsync(await org.Client.GetAsync(Url($"/api/v1/people/{id}/credentials")));
        Assert.DoesNotContain("908172", pin.GetRawText(), StringComparison.Ordinal);

        foreach (var table in new[] { "credentials", "audit_log", "outbox", "idempotency_keys" })
        {
            Assert.Equal(0, await ScalarAsync(
                $"SELECT count(*) FROM hub.{table} t WHERE t.tenant_id = @t AND (t::text LIKE '%18446744073709551615%' OR t::text LIKE '%908172%' OR t::text LIKE '%' || encode(convert_to('908172', 'UTF8'), 'hex') || '%')",
                ("t", org.Tenant)));
        }
    }

    [Fact]
    public async Task Credential_can_be_read_back_only_with_its_own_tenant_id_and_kind()
    {
        var org = await OrgAsync();
        var id = await PersonAsync(org.Client, org.Site);
        var created = await BodyAsync(await AddCredentialAsync(org.Client, id, CredentialKinds.Card, "123,45678"));
        var credential = created.GetProperty("id").GetGuid();

        await using var connection = new NpgsqlConnection(postgres.AdminOnHubConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT ciphertext, key_ref FROM hub.credentials WHERE id = @i", connection);
        command.Parameters.AddWithValue("i", credential);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        var stored = new ProtectedCredential((byte[])reader[0], (string)reader[1]);
        var protector = new CredentialProtector(new TransitKeyService(kms));

        Assert.Equal("528281023086", await protector.RevealAsync(org.Tenant, credential, CredentialKinds.Card, stored, CancellationToken.None));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => protector.RevealAsync(org.Tenant, Guid.NewGuid(), CredentialKinds.Card, stored, CancellationToken.None));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => protector.RevealAsync(org.Tenant, credential, CredentialKinds.Qr, stored, CancellationToken.None));
        await Assert.ThrowsAnyAsync<Exception>(() => protector.RevealAsync(Guid.NewGuid(), credential, CredentialKinds.Card, stored, CancellationToken.None));

        var tampered = (byte[])stored.Ciphertext.Clone();
        tampered[^1] ^= 1;
        await Assert.ThrowsAnyAsync<CryptographicException>(() => protector.RevealAsync(org.Tenant, credential, CredentialKinds.Card, stored with { Ciphertext = tampered }, CancellationToken.None));
    }

    [Fact]
    public async Task Same_card_gives_the_same_index_in_a_tenant_and_a_different_one_in_another()
    {
        var protector = new CredentialProtector(new TransitKeyService(kms));
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());

        var first = await protector.IndexAsync(a, CredentialKinds.Card, "42", CancellationToken.None);
        Assert.Equal(first, await protector.IndexAsync(a, CredentialKinds.Card, "42", CancellationToken.None));
        Assert.NotEqual(first, await protector.IndexAsync(b, CredentialKinds.Card, "42", CancellationToken.None));
        Assert.NotEqual(first, await protector.IndexAsync(a, CredentialKinds.Qr, "42", CancellationToken.None));
        Assert.NotEqual(first, SHA256.HashData("card\u000042"u8));
    }

    [Fact]
    public async Task Index_keeps_working_after_the_key_is_rotated()
    {
        var protector = new CredentialProtector(new TransitKeyService(kms));
        var tenant = Guid.NewGuid();
        var before = await protector.IndexAsync(tenant, CredentialKinds.Card, "42", CancellationToken.None);

        await new TransitKeyService(kms).RotateAsync(EncryptedBlobStore.KeyName(tenant, KeyPurpose.Index), CancellationToken.None);

        Assert.Equal(before, await protector.IndexAsync(tenant, CredentialKinds.Card, "42", CancellationToken.None));
    }

    [Fact]
    public async Task Card_in_use_is_refused_without_saying_by_whom_and_equivalent_formats_collide()
    {
        var org = await OrgAsync();
        var first = await PersonAsync(org.Client, org.Site, "Ana", "1");
        var second = await PersonAsync(org.Client, org.Site, "Bia", "2");
        await AddCredentialAsync(org.Client, first, CredentialKinds.Card, "528281023086");

        var repeated = await AddCredentialAsync(org.Client, second, CredentialKinds.Card, "123,45678");

        Assert.Equal(HttpStatusCode.Conflict, repeated.StatusCode);
        var text = await repeated.Content.ReadAsStringAsync();
        Assert.Contains("credential_in_use", text, StringComparison.Ordinal);
        Assert.DoesNotContain(first.ToString(), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Ana", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_active_pin_per_person_and_a_new_one_after_revoking()
    {
        var org = await OrgAsync();
        var id = await PersonAsync(org.Client, org.Site);
        var created = await BodyAsync(await AddCredentialAsync(org.Client, id, CredentialKinds.Pin, "1234"));

        Assert.Equal(HttpStatusCode.Conflict, (await AddCredentialAsync(org.Client, id, CredentialKinds.Pin, "5678")).StatusCode);

        var revoke = await org.Client.PostAsJsonAsync(Url($"/api/v1/people/{id}/credentials/{created.GetProperty("id").GetGuid()}/revoke"), new { reason = "Trocado" });
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await AddCredentialAsync(org.Client, id, CredentialKinds.Pin, "5678")).StatusCode);
    }

    [Fact]
    public async Task Two_people_may_have_the_same_pin_without_anyone_finding_out()
    {
        var org = await OrgAsync();
        var first = await PersonAsync(org.Client, org.Site, "Ana", "1");
        var second = await PersonAsync(org.Client, org.Site, "Bia", "2");

        Assert.Equal(HttpStatusCode.Created, (await AddCredentialAsync(org.Client, first, CredentialKinds.Pin, "1234")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await AddCredentialAsync(org.Client, second, CredentialKinds.Pin, "1234")).StatusCode);
        Assert.Equal(0, await ScalarAsync("SELECT count(*) FROM hub.credentials WHERE tenant_id = @t AND kind = 'pin' AND blind_index IS NOT NULL", ("t", org.Tenant)));
        Assert.Equal(2, await ScalarAsync("SELECT count(DISTINCT ciphertext) FROM hub.credentials WHERE tenant_id = @t AND kind = 'pin'", ("t", org.Tenant)));
    }

    [Theory]
    [InlineData("fingerprint_template", "MARCADORVALOR")]
    [InlineData("card", "MARCADORVALOR")]
    [InlineData("pin", "MARCADORVALOR")]
    public async Task Invalid_credential_is_refused_without_echoing_the_value(string kind, string value)
    {
        var org = await OrgAsync();
        var id = await PersonAsync(org.Client, org.Site);

        var response = await AddCredentialAsync(org.Client, id, kind, value);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(value, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Credential_of_another_person_cannot_be_revoked_through_this_person()
    {
        var org = await OrgAsync();
        var first = await PersonAsync(org.Client, org.Site, "Ana", "1");
        var second = await PersonAsync(org.Client, org.Site, "Bia", "2");
        var credential = (await BodyAsync(await AddCredentialAsync(org.Client, first, CredentialKinds.Card, "42"))).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await org.Client.PostAsJsonAsync(Url($"/api/v1/people/{second}/credentials/{credential}/revoke"), new { reason = "x" })).StatusCode);
    }

    [Fact]
    public async Task Lookup_finds_active_cards_only_within_the_scope_and_never_a_pin()
    {
        var org = await OrgAsync();
        var id = await PersonAsync(org.Client, org.Site);
        await AddCredentialAsync(org.Client, id, CredentialKinds.Card, "42");
        await AddCredentialAsync(org.Client, id, CredentialKinds.Pin, "1234");

        var found = await BodyAsync(await org.Client.PostAsJsonAsync(Url("/api/v1/credentials/lookup"), new { kind = "card", value = "042" }));
        Assert.True(found.GetProperty("found").GetBoolean());
        Assert.Equal(id, found.GetProperty("personId").GetGuid());

        Assert.False((await BodyAsync(await org.Client.PostAsJsonAsync(Url("/api/v1/credentials/lookup"), new { kind = "card", value = "43" }))).GetProperty("found").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await org.Client.PostAsJsonAsync(Url("/api/v1/credentials/lookup"), new { kind = "pin", value = "1234" })).StatusCode);

        var otherSite = await data.SiteAsync(org.Tenant, "Outra");
        using var limited = await ScopedAsync(org, otherSite, Permissions.CredentialsManage);
        Assert.False((await BodyAsync(await limited.PostAsJsonAsync(Url("/api/v1/credentials/lookup"), new { kind = "card", value = "42" }))).GetProperty("found").GetBoolean());

        var other = await OrgAsync();
        Assert.False((await BodyAsync(await other.Client.PostAsJsonAsync(Url("/api/v1/credentials/lookup"), new { kind = "card", value = "42" }))).GetProperty("found").GetBoolean());
    }

    [Fact]
    public async Task Lookup_does_not_find_a_revoked_card()
    {
        var org = await OrgAsync();
        var id = await PersonAsync(org.Client, org.Site);
        var card = (await BodyAsync(await AddCredentialAsync(org.Client, id, CredentialKinds.Card, "42"))).GetProperty("id").GetGuid();
        await org.Client.PostAsJsonAsync(Url($"/api/v1/people/{id}/credentials/{card}/revoke"), new { reason = "Perdido" });

        var found = await BodyAsync(await org.Client.PostAsJsonAsync(Url("/api/v1/credentials/lookup"), new { kind = "card", value = "42" }));

        Assert.False(found.GetProperty("found").GetBoolean());
    }

    [Fact]
    public async Task Same_card_in_another_tenant_is_independent()
    {
        var org = await OrgAsync();
        var other = await OrgAsync();

        Assert.Equal(HttpStatusCode.Created, (await AddCredentialAsync(org.Client, await PersonAsync(org.Client, org.Site), CredentialKinds.Card, "42")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await AddCredentialAsync(other.Client, await PersonAsync(other.Client, other.Site), CredentialKinds.Card, "42")).StatusCode);
    }

    // Permissoes e isolamento

    [Fact]
    public async Task Each_action_requires_its_own_permission()
    {
        var org = await OrgAsync();
        var id = await PersonAsync(org.Client, org.Site);
        using var reader = await ClientAsync(org, Permissions.PeopleRead);
        using var writer = await ClientAsync(org, Permissions.PeopleWrite);

        Assert.Equal(HttpStatusCode.Forbidden, (await CreatePersonAsync(reader, org.Site, "X", "X")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PatchAsync(reader, id, new { displayName = "X" }, "\"1\"")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await writer.PostAsJsonAsync(Url($"/api/v1/people/{id}/revoke"), new { reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(Url($"/api/v1/people/{id}/credentials"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await AddCredentialAsync(writer, id, CredentialKinds.Card, "42")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await writer.PostAsJsonAsync(Url("/api/v1/credentials/lookup"), new { kind = "card", value = "42" })).StatusCode);
    }

    [Fact]
    public async Task Credentials_require_a_second_factor()
    {
        var org = await OrgAsync();
        var id = await PersonAsync(org.Client, org.Site);
        using var noMfa = await ClientAsync(org, Permissions.CredentialsManage);
        noMfa.DefaultRequestHeaders.Remove("X-Test-Mfa");

        Assert.Equal("mfa_required", await CodeAsync(await AddCredentialAsync(noMfa, id, CredentialKinds.Card, "42")));
    }

    [Fact]
    public async Task Other_tenant_reaches_nothing()
    {
        var org = await OrgAsync();
        var other = await OrgAsync();
        var id = await PersonAsync(org.Client, org.Site);
        var credential = (await BodyAsync(await AddCredentialAsync(org.Client, id, CredentialKinds.Card, "42"))).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.GetAsync(Url($"/api/v1/people/{id}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PatchAsync(other.Client, id, new { displayName = "X" }, "\"1\"")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.PostAsJsonAsync(Url($"/api/v1/people/{id}/revoke"), new { reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.GetAsync(Url($"/api/v1/people/{id}/credentials"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AddCredentialAsync(other.Client, id, CredentialKinds.Card, "43")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.PostAsJsonAsync(Url($"/api/v1/people/{id}/credentials/{credential}/revoke"), new { reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.GetAsync(Url($"/api/v1/people/{id}/device-links"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.GetAsync(Url($"/api/v1/people/{id}/source-keys"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.GetAsync(Url($"/api/v1/people/{id}/groups"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await CreatePersonAsync(other.Client, org.Site, "X", "X")).StatusCode);
        Assert.Empty((await BodyAsync(await other.Client.GetAsync(Url("/api/v1/people")))).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Operator_of_another_site_reaches_nothing_of_this_person()
    {
        var org = await OrgAsync();
        var id = await PersonAsync(org.Client, org.Site);
        var otherSite = await data.SiteAsync(org.Tenant, "Outra");
        using var limited = await ScopedAsync(org, otherSite, AllPeople);

        Assert.Equal(HttpStatusCode.NotFound, (await limited.GetAsync(Url($"/api/v1/people/{id}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PatchAsync(limited, id, new { displayName = "X" }, "\"1\"")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await limited.PostAsJsonAsync(Url($"/api/v1/people/{id}/revoke"), new { reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await limited.GetAsync(Url($"/api/v1/people/{id}/credentials"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AddCredentialAsync(limited, id, CredentialKinds.Card, "42")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await limited.GetAsync(Url($"/api/v1/people/{id}/groups"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await CreatePersonAsync(limited, org.Site, "X", "X")).StatusCode);
        Assert.Empty((await BodyAsync(await limited.GetAsync(Url("/api/v1/people")))).GetProperty("items").EnumerateArray());
    }

    // Vinculo com equipamento

    private async Task<Guid> DeviceAsync(Org org, Guid site)
    {
        await using var connection = new NpgsqlConnection(postgres.AdminOnHubConnection);
        await connection.OpenAsync();
        await using var gateway = new NpgsqlCommand("INSERT INTO hub.edge_gateways (tenant_id, site_id, name) VALUES (@t, @s, 'g') RETURNING id", connection);
        gateway.Parameters.AddWithValue("t", org.Tenant);
        gateway.Parameters.AddWithValue("s", site);
        var gatewayId = (Guid)(await gateway.ExecuteScalarAsync())!;
        await using var device = new NpgsqlCommand("INSERT INTO hub.devices (tenant_id, site_id, gateway_id, serial_number, name) VALUES (@t, @s, @g, @n, 'Portaria') RETURNING id", connection);
        device.Parameters.AddWithValue("t", org.Tenant);
        device.Parameters.AddWithValue("s", site);
        device.Parameters.AddWithValue("g", gatewayId);
        device.Parameters.AddWithValue("n", Guid.NewGuid().ToString());
        return (Guid)(await device.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Device_link_keeps_the_exact_id_and_cannot_repeat()
    {
        var org = await OrgAsync();
        var device = await DeviceAsync(org, org.Site);
        var first = await PersonAsync(org.Client, org.Site, "Ana", "1");
        var second = await PersonAsync(org.Client, org.Site, "Bia", "2");

        var linked = await org.Client.PostAsJsonAsync(Url($"/api/v1/people/{first}/device-links"), new { deviceId = device, sourceUserId = "18446744073709551615" });
        Assert.Equal(HttpStatusCode.Created, linked.StatusCode);

        var list = await BodyAsync(await org.Client.GetAsync(Url($"/api/v1/people/{first}/device-links")));
        Assert.Equal("18446744073709551615", list[0].GetProperty("sourceUserId").GetString());
        Assert.Equal("Portaria", list[0].GetProperty("deviceName").GetString());

        Assert.Equal(HttpStatusCode.Conflict, (await org.Client.PostAsJsonAsync(Url($"/api/v1/people/{second}/device-links"), new { deviceId = device, sourceUserId = "18446744073709551615" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await org.Client.PostAsJsonAsync(Url($"/api/v1/people/{second}/device-links"), new { deviceId = device, sourceUserId = "abc" })).StatusCode);
    }

    [Fact]
    public async Task Person_cannot_be_linked_to_a_device_of_another_site()
    {
        var org = await OrgAsync();
        var otherSite = await data.SiteAsync(org.Tenant, "Outra");
        var device = await DeviceAsync(org, otherSite);
        var id = await PersonAsync(org.Client, org.Site);

        Assert.Equal(HttpStatusCode.NotFound, (await org.Client.PostAsJsonAsync(Url($"/api/v1/people/{id}/device-links"), new { deviceId = device, sourceUserId = "1" })).StatusCode);
    }

    // Modelo de acesso

    private static async Task<Guid> NamedAsync(HttpClient client, string path, Guid site, string name)
    {
        var response = await client.PostAsJsonAsync(Url($"/api/v1/access/{path}"), new { siteId = site, name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await BodyAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> PortalAsync(HttpClient client, Guid site)
    {
        var from = await NamedAsync(client, "areas", site, "Rua " + Guid.NewGuid());
        var to = await NamedAsync(client, "areas", site, "Hall " + Guid.NewGuid());
        var response = await client.PostAsJsonAsync(Url("/api/v1/access/portals"), new { siteId = site, name = "Porta " + Guid.NewGuid(), areaFromId = from, areaToId = to });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await BodyAsync(response)).GetProperty("id").GetGuid();
    }

    [Theory]
    [InlineData("groups")]
    [InlineData("areas")]
    public async Task Named_items_are_unique_in_the_site_regardless_of_case(string path)
    {
        var org = await OrgAsync();
        await NamedAsync(org.Client, path, org.Site, "Portaria");

        var repeated = await org.Client.PostAsJsonAsync(Url($"/api/v1/access/{path}"), new { siteId = org.Site, name = "PORTARIA" });
        Assert.Equal(HttpStatusCode.Conflict, repeated.StatusCode);
        Assert.Equal("name_in_use", await CodeAsync(repeated));

        await NamedAsync(org.Client, path, await data.SiteAsync(org.Tenant, "Outra"), "Portaria");
    }

    [Fact]
    public async Task Portal_graph_has_no_broken_references()
    {
        var org = await OrgAsync();
        var otherSite = await data.SiteAsync(org.Tenant, "Outra");
        var area = await NamedAsync(org.Client, "areas", org.Site, "Hall");
        var foreign = await NamedAsync(org.Client, "areas", otherSite, "Fora");

        Assert.Equal(HttpStatusCode.BadRequest, (await org.Client.PostAsJsonAsync(Url("/api/v1/access/portals"), new { siteId = org.Site, name = "P", areaFromId = area, areaToId = area })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await org.Client.PostAsJsonAsync(Url("/api/v1/access/portals"), new { siteId = org.Site, name = "P", areaFromId = area, areaToId = foreign })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await org.Client.PostAsJsonAsync(Url("/api/v1/access/portals"), new { siteId = org.Site, name = "P", areaFromId = area, areaToId = Guid.NewGuid() })).StatusCode);
    }

    [Fact]
    public async Task Schedule_keeps_its_timezone_and_spans()
    {
        var org = await OrgAsync();
        var created = await org.Client.PostAsJsonAsync(Url("/api/v1/access/schedules"), new
        {
            siteId = org.Site,
            name = "Noturno",
            timezone = "America/Manaus",
            spans = new object[]
            {
                new { startSecond = 79200, endSecond = 86400, weekdays = Monday },
                new { startSecond = 0, endSecond = 21600, weekdays = Tuesday, holidayTypes = HolidayOne },
            },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var list = await BodyAsync(await org.Client.GetAsync(Url("/api/v1/access/schedules")));
        Assert.Equal("America/Manaus", list[0].GetProperty("timezone").GetString());
        var spans = list[0].GetProperty("spans");
        Assert.Equal(2, spans.GetArrayLength());
        Assert.Equal(0, spans[0].GetProperty("startSecond").GetInt32());
        Assert.Equal("tue", spans[0].GetProperty("weekdays")[0].GetString());
        Assert.Equal(1, spans[0].GetProperty("holidayTypes")[0].GetInt32());
    }

    [Fact]
    public async Task Holiday_validates_dates_and_type()
    {
        var org = await OrgAsync();
        var ok = await org.Client.PostAsJsonAsync(Url("/api/v1/access/holidays"), new { siteId = org.Site, name = "Natal", startsOn = "2026-12-25", endsOn = "2026-12-25", holidayType = 1, repeats = true });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.Equal("2026-12-25", (await BodyAsync(ok)).GetProperty("startsOn").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await org.Client.PostAsJsonAsync(Url("/api/v1/access/holidays"), new { siteId = org.Site, name = "X", startsOn = "2026-12-26", endsOn = "2026-12-25", holidayType = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await org.Client.PostAsJsonAsync(Url("/api/v1/access/holidays"), new { siteId = org.Site, name = "X", startsOn = "2026-12-25", endsOn = "2026-12-25", holidayType = 4 })).StatusCode);
    }

    [Fact]
    public async Task Policy_references_must_exist_in_the_site()
    {
        var org = await OrgAsync();
        var otherSite = await data.SiteAsync(org.Tenant, "Outra");
        var group = await NamedAsync(org.Client, "groups", org.Site, "Equipe");
        var portal = await PortalAsync(org.Client, org.Site);
        var foreignPortal = await PortalAsync(org.Client, otherSite);
        var foreignGroup = await NamedAsync(org.Client, "groups", otherSite, "Fora");

        Task<HttpResponseMessage> Create(string name, Guid[] groups, Guid[] portals) =>
            org.Client.PostAsJsonAsync(Url("/api/v1/policies"), new { siteId = org.Site, kind = "allow", name, spec = new { groups, portals } });

        var created = await Create("Liberar", [group], [portal]);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(1, (await BodyAsync(created)).GetProperty("specVersion").GetInt32());

        var foreign = await Create("A", [group], [foreignPortal]);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, foreign.StatusCode);
        Assert.Equal("reference_not_found", await CodeAsync(foreign));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Create("B", [foreignGroup], [portal])).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Create("C", [Guid.NewGuid()], [portal])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Create("D", [], [portal])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await org.Client.PostAsJsonAsync(Url("/api/v1/policies"), new { siteId = org.Site, kind = "talvez", name = "E", spec = new { groups = new[] { group }, portals = new[] { portal } } })).StatusCode);
    }

    [Fact]
    public async Task Policy_edit_uses_the_version_and_drops_a_previous_approval()
    {
        var org = await OrgAsync();
        var group = await NamedAsync(org.Client, "groups", org.Site, "Equipe");
        var portal = await PortalAsync(org.Client, org.Site);
        var created = await BodyAsync(await org.Client.PostAsJsonAsync(Url("/api/v1/policies"), new { siteId = org.Site, kind = "deny", name = "Bloqueio", spec = new { groups = new[] { group }, portals = new[] { portal } } }));
        var id = created.GetProperty("id").GetGuid();
        await using (var connection = new NpgsqlConnection(postgres.AdminOnHubConnection))
        {
            await connection.OpenAsync();
            await using var approve = new NpgsqlCommand("UPDATE hub.access_policies SET approved_at = now() WHERE id = @i", connection);
            approve.Parameters.AddWithValue("i", id);
            await approve.ExecuteNonQueryAsync();
        }

        async Task<HttpResponseMessage> Patch(object body, string? match)
        {
            using var request = new HttpRequestMessage(HttpMethod.Patch, Url($"/api/v1/policies/{id}")) { Content = JsonContent.Create(body) };
            if (match is not null)
            {
                request.Headers.TryAddWithoutValidation("If-Match", match);
            }

            return await org.Client.SendAsync(request);
        }

        Assert.Equal((HttpStatusCode)428, (await Patch(new { name = "Novo" }, null)).StatusCode);
        var edited = await Patch(new { name = "Novo" }, "\"1\"");
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var body = await BodyAsync(edited);
        Assert.Equal(2, body.GetProperty("version").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("approvedAt").ValueKind);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Patch(new { name = "Velho" }, "\"1\"")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Patch(new { spec = new { groups = new[] { Guid.NewGuid() }, portals = new[] { portal } } }, "\"2\"")).StatusCode);
    }

    [Fact]
    public async Task Membership_requires_a_group_of_the_same_site()
    {
        var org = await OrgAsync();
        var otherSite = await data.SiteAsync(org.Tenant, "Outra");
        var id = await PersonAsync(org.Client, org.Site);
        var group = await NamedAsync(org.Client, "groups", org.Site, "Equipe");
        var foreign = await NamedAsync(org.Client, "groups", otherSite, "Fora");

        Assert.Equal(HttpStatusCode.NoContent, (await org.Client.PostAsJsonAsync(Url($"/api/v1/people/{id}/groups"), new { groupId = group })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await org.Client.PostAsJsonAsync(Url($"/api/v1/people/{id}/groups"), new { groupId = group })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await org.Client.PostAsJsonAsync(Url($"/api/v1/people/{id}/groups"), new { groupId = foreign })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await org.Client.PostAsJsonAsync(Url($"/api/v1/people/{id}/groups"), new { groupId = Guid.NewGuid() })).StatusCode);

        var groups = await BodyAsync(await org.Client.GetAsync(Url($"/api/v1/people/{id}/groups")));
        Assert.Equal(1, groups.GetArrayLength());

        Assert.Equal(HttpStatusCode.NoContent, (await org.Client.DeleteAsync(Url($"/api/v1/people/{id}/groups/{group}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await org.Client.DeleteAsync(Url($"/api/v1/people/{id}/groups/{group}"))).StatusCode);
    }

    [Fact]
    public async Task Access_model_respects_tenant_site_and_write_permission()
    {
        var org = await OrgAsync();
        var other = await OrgAsync();
        var otherSite = await data.SiteAsync(org.Tenant, "Outra");
        await NamedAsync(org.Client, "groups", org.Site, "Equipe");
        using var reader = await ClientAsync(org, Permissions.PoliciesRead);
        using var limited = await ScopedAsync(org, otherSite, Permissions.PoliciesRead, Permissions.PoliciesWrite);

        Assert.Empty((await BodyAsync(await other.Client.GetAsync(Url("/api/v1/access/groups")))).EnumerateArray());
        Assert.Empty((await BodyAsync(await limited.GetAsync(Url("/api/v1/access/groups")))).EnumerateArray());
        Assert.Equal(1, (await BodyAsync(await reader.GetAsync(Url("/api/v1/access/groups")))).GetArrayLength());
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(Url("/api/v1/access/groups"), new { siteId = org.Site, name = "X" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await limited.PostAsJsonAsync(Url("/api/v1/access/groups"), new { siteId = org.Site, name = "X" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.PostAsJsonAsync(Url("/api/v1/access/groups"), new { siteId = org.Site, name = "X" })).StatusCode);
    }

    [Theory]
    [InlineData("entity_source_keys")]
    [InlineData("access_groups")]
    [InlineData("person_groups")]
    [InlineData("areas")]
    [InlineData("portals")]
    [InlineData("schedules")]
    [InlineData("schedule_spans")]
    [InlineData("holidays")]
    public async Task New_tables_have_forced_row_security(string table)
    {
        Assert.Equal(1, await ScalarAsync(
            "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'hub' AND c.relname = @t AND c.relrowsecurity AND c.relforcerowsecurity",
            ("t", table)));
    }
}
