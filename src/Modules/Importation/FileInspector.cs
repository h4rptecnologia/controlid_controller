using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;

namespace Platform.Modules.Importation;

public sealed class InspectionLimits
{
    public long MaxUploadBytes { get; init; } = 256L * 1024 * 1024;

    public int MaxEntries { get; init; } = 20_000;

    public long MaxUncompressedBytes { get; init; } = 1024L * 1024 * 1024;

    public long MaxEntryBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>Foto e CSV reais comprimem pouco; muito acima disso e sinal de bomba.</summary>
    public int MaxExpansionRatio { get; init; } = 200;

    public int MaxPathLength { get; init; } = 260;

    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(5);
}

public static class DetectedTypes
{
    public const string Zip = "zip";
    public const string DelimitedText = "delimited_text";
    public const string HubPackage = "hub_package";
}

/// <summary>Codigos estaveis de recusa. A interface traduz; o detalhe tecnico nao vai para fora.</summary>
public static class RejectionCodes
{
    public const string Empty = "empty_file";
    public const string UnsupportedType = "unsupported_type";
    public const string CorruptArchive = "corrupt_archive";
    public const string TooManyEntries = "too_many_entries";
    public const string TooLarge = "too_large_uncompressed";
    public const string EntryTooLarge = "entry_too_large";
    public const string ExpansionRatio = "expansion_ratio";
    public const string OverlappingEntries = "overlapping_entries";
    public const string UnsafePath = "unsafe_path";
    public const string DuplicateName = "duplicate_name";
    public const string SpecialEntry = "special_entry";
    public const string EncryptedEntry = "encrypted_entry";
    public const string NestedArchive = "nested_archive";
    public const string ChecksumMismatch = "checksum_mismatch";
    public const string Timeout = "inspection_timeout";
    public const string Malware = "malware_detected";
    public const string HashMismatch = "hash_mismatch";
    public const string SizeMismatch = "size_mismatch";
    public const string InspectionFailed = "inspection_failed";
    public const string NestedPackage = "nested_package";
    public const string TooManyUnlockAttempts = "too_many_unlock_attempts";
}

public sealed record InspectionResult(string? DetectedType, string? RejectionCode, int EntryCount, long UncompressedBytes)
{
    public bool IsAccepted => RejectionCode is null;
}

/// <summary>
/// Inspeciona sem extrair: tudo e lido em streaming para dentro de um contador, nada e gravado. Nao confia em nome,
/// extensao, Content-Type nem nos tamanhos declarados dentro do ZIP.
/// </summary>
public static class FileInspector
{
    private const int LocalHeaderSignature = 0x04034B50;
    private const uint EndOfCentralDirectory = 0x06054B50;
    private const int EndRecordSize = 22;
    private const int SampleSize = 64 * 1024;
    private const int UnixFileTypeMask = 0xF000;
    private const int UnixRegularFile = 0x8000;
    private const int UnixDirectory = 0x4000;
    private const int GeneralPurposeOffset = 8;
    private const ushort EncryptedFlag = 1;

    private static readonly string[] ArchiveExtensions = [".zip", ".rar", ".7z", ".gz", ".tgz", ".tar", ".bz2", ".xz", ".jar", ".cab", ".iso", ".cidhub"];

    public static async Task<InspectionResult> InspectAsync(Stream content, InspectionLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(limits);

        if (content.Length == 0)
        {
            return Rejected(null, RejectionCodes.Empty);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(limits.MaxDuration);

        var sample = new byte[Math.Min(SampleSize, content.Length)];
        content.Position = 0;
        await content.ReadExactlyAsync(sample, cancellationToken);
        content.Position = 0;

        if (HubPackage.HasMagic(sample))
        {
            return new InspectionResult(DetectedTypes.HubPackage, null, 0, content.Length);
        }

        try
        {
            if (BinaryPrimitives.ReadInt32LittleEndian(PadTo4(sample)) == LocalHeaderSignature)
            {
                return await InspectZipAsync(content, limits, deadline.Token);
            }

            return LooksLikeText(sample) ? new InspectionResult(DetectedTypes.DelimitedText, null, 1, content.Length) : Rejected(null, RejectionCodes.UnsupportedType);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Rejected(DetectedTypes.Zip, RejectionCodes.Timeout);
        }
    }

    private static async Task<InspectionResult> InspectZipAsync(Stream content, InspectionLimits limits, CancellationToken cancellationToken)
    {
        // Antes de abrir: o ZipArchive carregaria um diretorio central gigante inteiro na memoria.
        var declaredEntries = await ReadDeclaredEntryCountAsync(content, cancellationToken);
        if (declaredEntries is null)
        {
            return Rejected(DetectedTypes.Zip, RejectionCodes.CorruptArchive);
        }

        if (declaredEntries > limits.MaxEntries)
        {
            return Rejected(DetectedTypes.Zip, RejectionCodes.TooManyEntries);
        }

        content.Position = 0;
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException)
        {
            return Rejected(DetectedTypes.Zip, RejectionCodes.CorruptArchive);
        }

        using (archive)
        {
            System.Collections.ObjectModel.ReadOnlyCollection<ZipArchiveEntry> entries;
            try
            {
                entries = archive.Entries;
            }
            catch (InvalidDataException)
            {
                return Rejected(DetectedTypes.Zip, RejectionCodes.CorruptArchive);
            }

            if (entries.Count > limits.MaxEntries)
            {
                return Rejected(DetectedTypes.Zip, RejectionCodes.TooManyEntries);
            }

            // Entradas sobrepostas (bomba sem aninhamento) somam mais comprimido do que o arquivo tem.
            if (entries.Sum(e => e.CompressedLength) > content.Length)
            {
                return Rejected(DetectedTypes.Zip, RejectionCodes.OverlappingEntries);
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                if (ValidateMetadata(entry, names, limits) is { } code)
                {
                    return Rejected(DetectedTypes.Zip, code);
                }
            }

            if (await FindEncryptedEntryAsync(content, cancellationToken))
            {
                return Rejected(DetectedTypes.Zip, RejectionCodes.EncryptedEntry);
            }

            var total = 0L;
            var buffer = new byte[81920];
            foreach (var entry in entries)
            {
                // Explicito: leitura de arquivo em memoria termina sem nunca observar o cancelamento.
                cancellationToken.ThrowIfCancellationRequested();

                if (entry.FullName.EndsWith('/'))
                {
                    continue;
                }

                var (code, read) = await ReadEntryAsync(entry, buffer, total, limits, cancellationToken);
                if (code is not null)
                {
                    return Rejected(DetectedTypes.Zip, code);
                }

                total += read;
            }

            return new InspectionResult(DetectedTypes.Zip, null, entries.Count, total);
        }
    }

    private static string? ValidateMetadata(ZipArchiveEntry entry, HashSet<string> names, InspectionLimits limits)
    {
        var name = entry.FullName;
        if (!IsSafePath(name, limits.MaxPathLength))
        {
            return RejectionCodes.UnsafePath;
        }

        if (!names.Add(name.TrimEnd('/')))
        {
            return RejectionCodes.DuplicateName;
        }

        // Tipo Unix nos 16 bits altos: so arquivo comum ou pasta. Link simbolico e dispositivo sao recusados.
        var unixMode = (entry.ExternalAttributes >> 16) & UnixFileTypeMask;
        if (unixMode != 0 && unixMode != UnixRegularFile && unixMode != UnixDirectory)
        {
            return RejectionCodes.SpecialEntry;
        }

        return ArchiveExtensions.Any(extension => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            ? RejectionCodes.NestedArchive
            : null;
    }

    private static async Task<(string? Code, long Read)> ReadEntryAsync(ZipArchiveEntry entry, byte[] buffer, long totalSoFar, InspectionLimits limits, CancellationToken cancellationToken)
    {
        var crc = new Crc32();
        var read = 0L;
        var first = new byte[4];
        var firstLength = 0;

        try
        {
            await using var stream = await entry.OpenAsync(cancellationToken);
            int count;
            while ((count = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (firstLength < first.Length)
                {
                    var take = Math.Min(first.Length - firstLength, count);
                    Array.Copy(buffer, 0, first, firstLength, take);
                    firstLength += take;
                }

                crc.Append(buffer.AsSpan(0, count));
                read += count;

                // Conta o que saiu de verdade: tamanho declarado no ZIP pode ser mentira.
                if (read > limits.MaxEntryBytes)
                {
                    return (RejectionCodes.EntryTooLarge, read);
                }

                if (totalSoFar + read > limits.MaxUncompressedBytes)
                {
                    return (RejectionCodes.TooLarge, read);
                }

                if (read > limits.MaxExpansionRatio * Math.Max(entry.CompressedLength, 1024))
                {
                    return (RejectionCodes.ExpansionRatio, read);
                }
            }
        }
        catch (InvalidDataException)
        {
            return (RejectionCodes.CorruptArchive, read);
        }

        // O nome nao decide: conteudo cifrado escondido dentro do ZIP passaria sem o antivirus ver.
        if (firstLength >= 4 && (BinaryPrimitives.ReadInt32LittleEndian(first) == LocalHeaderSignature || first.AsSpan().SequenceEqual("CIDH"u8)))
        {
            return (RejectionCodes.NestedArchive, read);
        }

        return crc.GetCurrentHashAsUInt32() == entry.Crc32 ? (null, read) : (RejectionCodes.ChecksumMismatch, read);
    }

    /// <summary>ZIP64 e rejeitado por ora: 0xFFFF nao informa o total, e arquivos do fabricante nao devem precisar.</summary>
    private static async Task<int?> ReadDeclaredEntryCountAsync(Stream content, CancellationToken cancellationToken)
    {
        var length = (int)Math.Min(content.Length, ushort.MaxValue + EndRecordSize);
        var tail = new byte[length];
        content.Position = content.Length - length;
        await content.ReadExactlyAsync(tail, cancellationToken);

        for (var i = length - EndRecordSize; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == EndOfCentralDirectory)
            {
                var total = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 10));
                return total == ushort.MaxValue ? null : total;
            }
        }

        return null;
    }

    // O ZipArchive nao expoe o bit de criptografia: le o cabecalho local de cada entrada no proprio arquivo.
    private static async Task<bool> FindEncryptedEntryAsync(Stream content, CancellationToken cancellationToken)
    {
        var length = (int)Math.Min(content.Length, ushort.MaxValue + EndRecordSize);
        var tail = new byte[length];
        content.Position = content.Length - length;
        await content.ReadExactlyAsync(tail, cancellationToken);

        for (var i = length - EndRecordSize; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) != EndOfCentralDirectory)
            {
                continue;
            }

            var size = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i + 12));
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i + 16));
            if (offset + size > content.Length)
            {
                return false;
            }

            var directory = new byte[size];
            content.Position = offset;
            await content.ReadExactlyAsync(directory, cancellationToken);

            const int centralHeaderSize = 46;
            var position = 0;
            while (position + centralHeaderSize <= directory.Length)
            {
                if ((BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(position + GeneralPurposeOffset)) & EncryptedFlag) != 0)
                {
                    return true;
                }

                position += centralHeaderSize
                    + BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(position + 28))
                    + BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(position + 30))
                    + BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(position + 32));
            }

            return false;
        }

        return false;
    }

    internal static bool IsSafePath(string name, int maxLength)
    {
        if (name.Length == 0 || name.Length > maxLength || name[0] is '/' or '\\' || name.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        // Unidade do Windows, como C:, e caracteres de controle.
        if ((name.Length >= 2 && name[1] == ':') || name.Any(char.IsControl))
        {
            return false;
        }

        return !name.Split('/').Any(part => part is ".." or ".");
    }

    private static bool LooksLikeText(byte[] sample)
    {
        var start = sample.AsSpan();
        if (start.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            start = start[3..];
        }

        if (start.IsEmpty)
        {
            return false;
        }

        var controls = 0;
        foreach (var value in start)
        {
            if (value == 0)
            {
                return false;
            }

            if (value < 0x09 || (value > 0x0D && value < 0x20))
            {
                controls++;
            }
        }

        // A amostra precisa ter separador de linha ou coluna: nao basta nao ser binario.
        return controls == 0 && Encoding.Latin1.GetString(start).AsSpan().IndexOfAny(";,\t\n") >= 0;
    }

    private static byte[] PadTo4(byte[] sample)
    {
        var padded = new byte[4];
        Array.Copy(sample, padded, Math.Min(4, sample.Length));
        return padded;
    }

    private static InspectionResult Rejected(string? type, string code) => new(type, code, 0, 0);
}
