using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Platform.Shared.Storage;

public static class CredentialKinds
{
    public const string Card = "card";
    public const string Qr = "qr";
    public const string Uhf = "uhf";
    public const string Pin = "pin";

    public static bool IsSupported(string? kind) => kind is Card or Qr or Uhf or Pin;

    /// <summary>PIN nao pode virar chave de busca: tem pouca entropia e o indice permitiria descobrir quem usa qual.</summary>
    public static bool IsSearchable(string kind) => kind is Card or Qr or Uhf;
}

/// <summary>Regras de valor. Nunca devolvem o valor em mensagem: quem chama recebe so verdadeiro ou falso.</summary>
public static class CredentialValue
{
    private const int MaxTextLength = 512;

    /// <summary>
    /// Forma canonica, para o mesmo cartao gerar o mesmo indice. Cartao e decimal de 64 bits sem sinal, ou "area,numero",
    /// como descrito na Lista de Objetos: area * 2^32 + numero.
    /// </summary>
    public static bool TryNormalize(string? kind, string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        switch (kind)
        {
            case CredentialKinds.Card:
                return TryCard(value, out normalized);
            case CredentialKinds.Pin:
                if (value.Length is < 4 or > 16 || !value.All(char.IsAsciiDigit))
                {
                    return false;
                }

                normalized = value;
                return true;
            case CredentialKinds.Qr:
            case CredentialKinds.Uhf:
                if (value.Length > MaxTextLength || value.Any(char.IsControl) || string.IsNullOrWhiteSpace(value))
                {
                    return false;
                }

                normalized = value;
                return true;
            default:
                return false;
        }
    }

    private static bool TryCard(string value, out string normalized)
    {
        normalized = string.Empty;
        var comma = value.IndexOf(',', StringComparison.Ordinal);
        if (comma >= 0)
        {
            var area = value[..comma];
            var number = value[(comma + 1)..];
            if (!IsDigits(area) || !IsDigits(number)
                || !uint.TryParse(area, NumberStyles.None, CultureInfo.InvariantCulture, out var high)
                || !uint.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var low))
            {
                return false;
            }

            normalized = (((ulong)high << 32) | low).ToString(CultureInfo.InvariantCulture);
            return true;
        }

        if (!IsDigits(value) || !ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        normalized = parsed.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    private static bool IsDigits(string value) => value.Length > 0 && value.All(char.IsAsciiDigit);

    /// <summary>Dica para a tela. PIN nao tem dica; os demais mostram no maximo o fim de um valor longo.</summary>
    public static string Hint(string kind, string normalized)
    {
        if (kind == CredentialKinds.Pin || normalized.Length < 8)
        {
            return "****";
        }

        return string.Concat("****", normalized.AsSpan(normalized.Length - 4));
    }
}

public sealed record ProtectedCredential(byte[] Ciphertext, string WrappedKey);

/// <summary>
/// Sigilo e busca com chaves independentes (docs/06-seguranca.md). Cada valor tem DEK propria, embrulhada no KMS, e o AAD
/// prende o texto cifrado ao tenant, a credencial e ao tipo: copiar a linha para outra pessoa ou tenant nao decifra.
/// O indice cego e HMAC calculado dentro do KMS, entao a chave nunca chega a aplicacao.
/// </summary>
public sealed class CredentialProtector(IKeyService keys)
{
    private const byte FormatVersion = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int BatchSize = 500;

    public async Task<ProtectedCredential> ProtectAsync(Guid tenantId, Guid credentialId, string kind, string normalized, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(normalized);
        var dek = RandomNumberGenerator.GetBytes(32);
        try
        {
            var plain = Encoding.UTF8.GetBytes(normalized);
            var output = new byte[1 + NonceSize + TagSize + plain.Length];
            output[0] = FormatVersion;
            var nonce = output.AsSpan(1, NonceSize);
            RandomNumberGenerator.Fill(nonce);
            using var aes = new AesGcm(dek, TagSize);
            aes.Encrypt(nonce, plain, output.AsSpan(1 + NonceSize + TagSize), output.AsSpan(1 + NonceSize, TagSize), Aad(tenantId, credentialId, kind));
            CryptographicOperations.ZeroMemory(plain);
            var wrapped = await keys.WrapAsync(EncryptedBlobStore.KeyName(tenantId, KeyPurpose.Credential), dek, cancellationToken);
            return new ProtectedCredential(output, wrapped);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    /// <summary>So para quem envia ao equipamento. Falha de autenticacao vira excecao, sem valor parcial.</summary>
    public async Task<string> RevealAsync(Guid tenantId, Guid credentialId, string kind, ProtectedCredential stored, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stored);
        if (stored.Ciphertext.Length < 1 + NonceSize + TagSize || stored.Ciphertext[0] != FormatVersion)
        {
            throw new CryptographicException("Formato de credencial desconhecido.");
        }

        var dek = await keys.UnwrapAsync(EncryptedBlobStore.KeyName(tenantId, KeyPurpose.Credential), stored.WrappedKey, cancellationToken);
        try
        {
            var body = stored.Ciphertext.AsSpan(1 + NonceSize + TagSize);
            var plain = new byte[body.Length];
            using var aes = new AesGcm(dek, TagSize);
            aes.Decrypt(stored.Ciphertext.AsSpan(1, NonceSize), body, stored.Ciphertext.AsSpan(1 + NonceSize, TagSize), plain, Aad(tenantId, credentialId, kind));
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    /// <summary>Indice por tenant: o mesmo cartao em dois clientes da indices diferentes, sem correlacao entre eles.</summary>
    public async Task<IReadOnlyList<byte[]>> IndexManyAsync(Guid tenantId, string kind, IReadOnlyList<string> normalized, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(normalized);
        if (!CredentialKinds.IsSearchable(kind))
        {
            throw new InvalidOperationException("Este tipo de credencial nao tem indice de busca.");
        }

        var keyName = EncryptedBlobStore.KeyName(tenantId, KeyPurpose.Index);
        await keys.EnsureKeyAsync(keyName, cancellationToken);

        var result = new List<byte[]>(normalized.Count);
        foreach (var batch in normalized.Chunk(BatchSize))
        {
            var inputs = batch.Select(value => Encoding.UTF8.GetBytes($"{kind}\0{value}")).ToList();
            result.AddRange(await keys.HmacAsync(keyName, inputs, cancellationToken));
        }

        return result;
    }

    public async Task<byte[]> IndexAsync(Guid tenantId, string kind, string normalized, CancellationToken cancellationToken) =>
        (await IndexManyAsync(tenantId, kind, [normalized], cancellationToken))[0];

    private static byte[] Aad(Guid tenantId, Guid credentialId, string kind) =>
        Encoding.UTF8.GetBytes($"cred-v{FormatVersion}|{tenantId:N}|{credentialId:N}|{kind}");
}
