using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Platform.Shared.Storage;

/// <summary>Chaves separadas por finalidade: quem decifra arquivos comuns nao precisa alcancar biometria.</summary>
public enum KeyPurpose
{
    General,
    Biometric,
    Secret,
    Credential,
    Index,
}

public sealed class KeyServiceException : Exception
{
    public KeyServiceException()
        : base("Servico de chaves indisponivel ou operacao recusada.")
    {
    }

    public KeyServiceException(string message)
        : base(message)
    {
    }

    public KeyServiceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public interface IKeyService
{
    Task<string> WrapAsync(string keyName, byte[] dek, CancellationToken cancellationToken);

    Task<byte[]> UnwrapAsync(string keyName, string wrappedDek, CancellationToken cancellationToken);

    /// <summary>Re-embrulha com a versao atual da KEK. A DEK nunca passa em claro pela aplicacao.</summary>
    Task<string> RewrapAsync(string keyName, string wrappedDek, CancellationToken cancellationToken);

    Task RotateAsync(string keyName, CancellationToken cancellationToken);

    /// <summary>Cria a chave se faltar. Sem efeito se ja existir.</summary>
    Task EnsureKeyAsync(string keyName, CancellationToken cancellationToken);

    /// <summary>HMAC-SHA256 calculado no KMS, sempre com a versao 1 da chave: o indice precisa dar o mesmo resultado depois de rotacoes.</summary>
    Task<IReadOnlyList<byte[]>> HmacAsync(string keyName, IReadOnlyList<byte[]> inputs, CancellationToken cancellationToken);
}

/// <summary>
/// Transit do HashiCorp Vault ou OpenBao. O vinculo com tenant, objeto e classificacao fica no AAD do proprio
/// blob: o endpoint rewrap nao aceita associated_data, e a separacao aqui vem de uma chave por tenant e finalidade.
/// </summary>
public sealed class TransitKeyService(HttpClient client, string mount = "transit") : IKeyService
{
    public async Task<string> WrapAsync(string keyName, byte[] dek, CancellationToken cancellationToken)
    {
        var data = await PostAsync(keyName, "encrypt", new { plaintext = Convert.ToBase64String(dek) }, cancellationToken);
        return data.Ciphertext ?? throw new KeyServiceException();
    }

    public async Task<byte[]> UnwrapAsync(string keyName, string wrappedDek, CancellationToken cancellationToken)
    {
        var data = await PostAsync(keyName, "decrypt", new { ciphertext = wrappedDek }, cancellationToken);
        return Convert.FromBase64String(data.Plaintext ?? throw new KeyServiceException());
    }

    public async Task<string> RewrapAsync(string keyName, string wrappedDek, CancellationToken cancellationToken)
    {
        var data = await PostAsync(keyName, "rewrap", new { ciphertext = wrappedDek }, cancellationToken);
        return data.Ciphertext ?? throw new KeyServiceException();
    }

    public async Task RotateAsync(string keyName, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsync(new Uri($"v1/{mount}/keys/{Name(keyName)}/rotate", UriKind.Relative), null, cancellationToken);
        EnsureSuccess(response);
    }

    public async Task EnsureKeyAsync(string keyName, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(new Uri($"v1/{mount}/keys/{Name(keyName)}", UriKind.Relative), new { type = "aes256-gcm96" }, cancellationToken);
        EnsureSuccess(response);
    }

    public async Task<IReadOnlyList<byte[]>> HmacAsync(string keyName, IReadOnlyList<byte[]> inputs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var body = new { key_version = 1, batch_input = inputs.Select(input => new { input = Convert.ToBase64String(input) }).ToList() };
        using var response = await client.PostAsJsonAsync(new Uri($"v1/{mount}/hmac/{Name(keyName)}/sha2-256", UriKind.Relative), body, cancellationToken);
        EnsureSuccess(response);
        var envelope = await response.Content.ReadFromJsonAsync<HmacEnvelope>(cancellationToken);
        var results = envelope?.Data?.Results;
        if (results is null || results.Count != inputs.Count)
        {
            throw new KeyServiceException();
        }

        return results.Select(result => FromHmac(result.Hmac)).ToList();
    }

    // Formato "vault:v1:base64". O prefixo nao entra no indice: so a versao 1 e usada.
    private static byte[] FromHmac(string? value)
    {
        const string Prefix = "vault:v1:";
        return value is not null && value.StartsWith(Prefix, StringComparison.Ordinal) ? Convert.FromBase64String(value[Prefix.Length..]) : throw new KeyServiceException();
    }

    private async Task<TransitData> PostAsync(string keyName, string operation, object body, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(new Uri($"v1/{mount}/{operation}/{Name(keyName)}", UriKind.Relative), body, cancellationToken);
        EnsureSuccess(response);
        var envelope = await response.Content.ReadFromJsonAsync<TransitEnvelope>(cancellationToken);
        return envelope?.Data ?? throw new KeyServiceException();
    }

    // O corpo do erro nunca entra na excecao: pode ecoar o que foi enviado.
    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new KeyServiceException();
        }
    }

    private static string Name(string keyName) => Uri.EscapeDataString(keyName);

    private sealed record HmacEnvelope([property: JsonPropertyName("data")] HmacData? Data);

    private sealed record HmacData([property: JsonPropertyName("batch_results")] IReadOnlyList<HmacResult>? Results);

    private sealed record HmacResult([property: JsonPropertyName("hmac")] string? Hmac);

    private sealed record TransitEnvelope([property: JsonPropertyName("data")] TransitData? Data);

    private sealed record TransitData(
        [property: JsonPropertyName("ciphertext")] string? Ciphertext,
        [property: JsonPropertyName("plaintext")] string? Plaintext);
}
