using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Platform.Shared.Storage;

/// <summary>Falha de autenticacao. A mensagem e generica de proposito: nao ajuda a montar um ataque.</summary>
public sealed class BlobIntegrityException : Exception
{
    public BlobIntegrityException()
        : base("Conteudo invalido ou adulterado.")
    {
    }

    public BlobIntegrityException(string message)
        : base(message)
    {
    }

    public BlobIntegrityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// AES-256-GCM em segmentos, no formato STREAM (Hoang, Reyhanitabar, Rogaway e Vizar, 2015): o nonce e
/// prefixo aleatorio + contador + marca de ultimo segmento. Por isso trocar a ordem, cortar ou emendar
/// segmentos falha na autenticacao. A primitiva e a do .NET; este arquivo nao implementa cifra nenhuma.
///
/// Formato v1: "HUBB" | versao (1) | tamanho do segmento (4, BE) | prefixo (7) | { cifrado | tag (16) }...
/// O limite de 2^32 segmentos nunca e atingido: com 1 MiB por segmento seriam 4 PiB.
/// </summary>
public static class BlobCipher
{
    public const byte FormatVersion = 1;
    public const int DekSize = 32;
    public const int DefaultSegmentSize = 1024 * 1024;

    private const int TagSize = 16;
    private const int NoncePrefixSize = 7;
    private const int HeaderSize = 4 + 1 + 4 + NoncePrefixSize;
    private const int MaxSegmentSize = 64 * 1024 * 1024;
    private static readonly byte[] Magic = "HUBB"u8.ToArray();

    /// <summary>AAD canonico com tamanho prefixado: nenhum par de campos pode colidir com outro.</summary>
    public static byte[] BuildContext(Guid tenantId, Guid blobId, string kind, string classification)
    {
        using var buffer = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        foreach (var field in new[] { tenantId.ToString("N"), blobId.ToString("N"), kind, classification })
        {
            var bytes = Encoding.UTF8.GetBytes(field);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            buffer.Write(length);
            buffer.Write(bytes);
        }

        return buffer.ToArray();
    }

    public static async Task EncryptAsync(
        Stream plaintext,
        Stream ciphertext,
        byte[] dek,
        byte[] context,
        CancellationToken cancellationToken,
        int segmentSize = DefaultSegmentSize)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(ciphertext);
        ArgumentNullException.ThrowIfNull(dek);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(segmentSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(segmentSize, MaxSegmentSize);

        var header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        header[4] = FormatVersion;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(5), segmentSize);
        RandomNumberGenerator.Fill(header.AsSpan(9, NoncePrefixSize));
        await ciphertext.WriteAsync(header, cancellationToken);

        var aad = BuildAad(header, context);
        using var aes = new AesGcm(dek, TagSize);

        // Um byte a mais para saber se ha mais dados sem precisar do tamanho total do stream.
        var current = new byte[segmentSize];
        var next = new byte[segmentSize];
        var output = new byte[segmentSize + TagSize];
        var nonce = new byte[12];
        uint counter = 0;

        try
        {
            var currentLength = await ReadFullAsync(plaintext, current, cancellationToken);
            while (true)
            {
                var nextLength = currentLength == segmentSize ? await ReadFullAsync(plaintext, next, cancellationToken) : 0;
                var isLast = nextLength == 0;

                FillNonce(nonce, header, counter, isLast);
                aes.Encrypt(nonce, current.AsSpan(0, currentLength), output.AsSpan(0, currentLength), output.AsSpan(currentLength, TagSize), aad);
                await ciphertext.WriteAsync(output.AsMemory(0, currentLength + TagSize), cancellationToken);

                if (isLast)
                {
                    return;
                }

                (current, next) = (next, current);
                currentLength = nextLength;
                counter = checked(counter + 1);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(current);
            CryptographicOperations.ZeroMemory(next);
        }
    }

    /// <summary>
    /// Cada segmento so e escrito depois de autenticado, mas um corte no fim so aparece no ultimo. Se lancar
    /// excecao, o destino esta incompleto e tem de ser descartado.
    /// </summary>
    public static async Task DecryptAsync(Stream ciphertext, Stream plaintext, byte[] dek, byte[] context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(dek);

        var header = new byte[HeaderSize];
        if (await ReadFullAsync(ciphertext, header, cancellationToken) != HeaderSize
            || !header.AsSpan(0, 4).SequenceEqual(Magic)
            || header[4] != FormatVersion)
        {
            throw new BlobIntegrityException();
        }

        var segmentSize = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(5));
        if (segmentSize is <= 0 or > MaxSegmentSize)
        {
            throw new BlobIntegrityException();
        }

        var aad = BuildAad(header, context);
        using var aes = new AesGcm(dek, TagSize);

        var current = new byte[segmentSize + TagSize];
        var next = new byte[segmentSize + TagSize];
        var output = new byte[segmentSize];
        var nonce = new byte[12];
        uint counter = 0;

        try
        {
            var currentLength = await ReadFullAsync(ciphertext, current, cancellationToken);
            while (true)
            {
                if (currentLength < TagSize)
                {
                    throw new BlobIntegrityException();
                }

                var nextLength = currentLength == current.Length ? await ReadFullAsync(ciphertext, next, cancellationToken) : 0;
                var isLast = nextLength == 0;
                var bodyLength = currentLength - TagSize;

                FillNonce(nonce, header, counter, isLast);
                try
                {
                    aes.Decrypt(nonce, current.AsSpan(0, bodyLength), current.AsSpan(bodyLength, TagSize), output.AsSpan(0, bodyLength), aad);
                }
                catch (AuthenticationTagMismatchException ex)
                {
                    throw new BlobIntegrityException("Conteudo invalido ou adulterado.", ex);
                }

                await plaintext.WriteAsync(output.AsMemory(0, bodyLength), cancellationToken);

                if (isLast)
                {
                    return;
                }

                (current, next) = (next, current);
                currentLength = nextLength;
                counter = checked(counter + 1);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(output);
        }
    }

    private static byte[] BuildAad(byte[] header, byte[] context)
    {
        var aad = new byte[header.Length + context.Length];
        header.CopyTo(aad, 0);
        context.CopyTo(aad, header.Length);
        return aad;
    }

    private static void FillNonce(byte[] nonce, byte[] header, uint counter, bool isLast)
    {
        header.AsSpan(9, NoncePrefixSize).CopyTo(nonce);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(NoncePrefixSize), counter);
        nonce[11] = isLast ? (byte)1 : (byte)0;
    }

    private static async Task<int> ReadFullAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
