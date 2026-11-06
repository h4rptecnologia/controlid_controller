using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NSec.Cryptography;
using Platform.Shared.Storage;

namespace Platform.Modules.Importation;

/// <summary>Falha unica para senha errada e adulteracao: nao informa ao atacante qual das duas ocorreu.</summary>
public sealed class PackageOpenException : Exception
{
    public PackageOpenException()
        : base("Arquivo inválido ou senha incorreta.")
    {
    }

    public PackageOpenException(string message)
        : base(message)
    {
    }

    public PackageOpenException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Pacote proprio .cidhub, protegido por senha. Nao e formato Control iD e nao vai para o equipamento.
/// Argon2id (libsodium, via NSec) deriva a chave que embrulha uma DEK aleatoria; o conteudo usa o BlobCipher.
/// O cabecalho inteiro e AAD do conteudo: alterar qualquer parametro faz a autenticacao falhar.
///
/// Layout: "CIDHUB1\n" | tamanho do cabecalho (4, BE) | cabecalho JSON | conteudo cifrado.
/// Nao prova autoria: quem tem a senha pode gerar um pacote valido.
/// </summary>
public static class HubPackage
{
    public const int MinPasswordLength = 12;

    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxPasswordLength = 256;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    // Piso contra cabecalho forjado que enfraquece a derivacao; teto contra cabecalho que esgota a memoria do servidor.
    private const int MinMemoryKiB = 64 * 1024;
    private const int MaxMemoryKiB = 256 * 1024;
    private const int MinPasses = 3;
    private const int MaxPasses = 10;
    private const int DefaultMemoryKiB = 64 * 1024;
    private const int DefaultPasses = 3;

    private static readonly byte[] Magic = "CIDHUB1\n"u8.ToArray();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Os parametros ja entram na derivacao, e o cabecalho inteiro autentica o conteudo: aqui basta separar o dominio.
    private static readonly byte[] WrapLabel = "cidhub-v1-wrap"u8.ToArray();

    public static bool HasMagic(ReadOnlySpan<byte> start) => start.StartsWith(Magic);

    public static Task SealAsync(Stream plaintext, Stream package, string password, CancellationToken cancellationToken) =>
        SealAsync(plaintext, package, password, DefaultMemoryKiB, DefaultPasses, cancellationToken);

    internal static async Task SealAsync(Stream plaintext, Stream package, string password, int memoryKiB, int passes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length is < MinPasswordLength or > MaxPasswordLength)
        {
            throw new ArgumentException($"A senha deve ter de {MinPasswordLength} a {MaxPasswordLength} caracteres.", nameof(password));
        }

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var dek = RandomNumberGenerator.GetBytes(BlobCipher.DekSize);
        var kek = Derive(password, salt, memoryKiB, passes);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var wrapped = new byte[BlobCipher.DekSize + TagSize];
            var parameters = new PackageKdf("argon2id", memoryKiB, passes, 1, Convert.ToBase64String(salt));
            using (var aes = new AesGcm(kek, TagSize))
            {
                aes.Encrypt(nonce, dek, wrapped.AsSpan(0, BlobCipher.DekSize), wrapped.AsSpan(BlobCipher.DekSize, TagSize), WrapLabel);
            }

            var header = new PackageHeader("com.h4rp.controlid-hub.package", 1, DateTimeOffset.UtcNow, parameters, Convert.ToBase64String(nonce), Convert.ToBase64String(wrapped));
            var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header, Json);

            await package.WriteAsync(Magic, cancellationToken);
            var length = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, headerBytes.Length);
            await package.WriteAsync(length, cancellationToken);
            await package.WriteAsync(headerBytes, cancellationToken);
            await BlobCipher.EncryptAsync(plaintext, package, dek, headerBytes, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    /// <summary>Se lancar, o destino esta incompleto e tem de ser descartado. Todas as falhas sao indistinguiveis.</summary>
    public static async Task OpenAsync(Stream package, Stream plaintext, string password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(plaintext);
        if (string.IsNullOrEmpty(password) || password.Length > MaxPasswordLength)
        {
            throw new PackageOpenException();
        }

        var headerBytes = await ReadHeaderAsync(package, cancellationToken);
        PackageHeader? header;
        try
        {
            header = JsonSerializer.Deserialize<PackageHeader>(headerBytes, Json);
        }
        catch (JsonException)
        {
            throw new PackageOpenException();
        }

        if (header is not { Version: 1, Kdf: { Algorithm: "argon2id", Parallelism: 1, MemoryKiB: >= MinMemoryKiB and <= MaxMemoryKiB, Passes: >= MinPasses and <= MaxPasses } })
        {
            throw new PackageOpenException();
        }

        byte[]? kek = null;
        var dek = new byte[BlobCipher.DekSize];
        try
        {
            var salt = Convert.FromBase64String(header.Kdf.Salt);
            var nonce = Convert.FromBase64String(header.WrapNonce);
            var wrapped = Convert.FromBase64String(header.WrappedDek);
            if (salt.Length != SaltSize || nonce.Length != NonceSize || wrapped.Length != BlobCipher.DekSize + TagSize)
            {
                throw new PackageOpenException();
            }

            kek = Derive(password, salt, header.Kdf.MemoryKiB, header.Kdf.Passes);
            using (var aes = new AesGcm(kek, TagSize))
            {
                aes.Decrypt(nonce, wrapped.AsSpan(0, BlobCipher.DekSize), wrapped.AsSpan(BlobCipher.DekSize, TagSize), dek, WrapLabel);
            }

            await BlobCipher.DecryptAsync(package, plaintext, dek, headerBytes, cancellationToken);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or EndOfStreamException or CryptographicException or BlobIntegrityException)
        {
            throw new PackageOpenException();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
            if (kek is not null)
            {
                CryptographicOperations.ZeroMemory(kek);
            }
        }
    }

    private static async Task<byte[]> ReadHeaderAsync(Stream package, CancellationToken cancellationToken)
    {
        var prefix = new byte[Magic.Length + 4];
        if (await package.ReadAtLeastAsync(prefix, prefix.Length, throwOnEndOfStream: false, cancellationToken) != prefix.Length || !HasMagic(prefix))
        {
            throw new PackageOpenException();
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(prefix.AsSpan(Magic.Length));
        if (length is <= 0 or > MaxHeaderBytes)
        {
            throw new PackageOpenException();
        }

        var header = new byte[length];
        return await package.ReadAtLeastAsync(header, length, throwOnEndOfStream: false, cancellationToken) == length
            ? header
            : throw new PackageOpenException();
    }

    internal static byte[] Derive(string password, byte[] salt, int memoryKiB, int passes)
    {
        // Por bytes, de proposito: a sobrecarga com string do NSec gera outra saida, sem documentar o motivo, e o formato
        // do arquivo nao pode depender disso. NFC para o mesmo "c cedilha" abrir o pacote em qualquer sistema.
        var parameters = new Argon2Parameters { DegreeOfParallelism = 1, MemorySize = memoryKiB, NumberOfPasses = passes };
        var secret = Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormC));
        try
        {
            return PasswordBasedKeyDerivationAlgorithm.Argon2id(parameters).DeriveBytes(secret, salt, KeySize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    private sealed record PackageKdf(string Algorithm, int MemoryKiB, int Passes, int Parallelism, string Salt);

    private sealed record PackageHeader(string Format, int Version, DateTimeOffset CreatedUtc, PackageKdf Kdf, string WrapNonce, string WrappedDek);
}
