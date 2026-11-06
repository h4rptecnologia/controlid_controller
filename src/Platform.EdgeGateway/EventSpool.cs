using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Platform.Shared.Contracts;

namespace Platform.EdgeGateway;

/// <summary>
/// Fila local durante falta de rede: um arquivo por evento, cifrado com AES-256-GCM. O terminal so recebe resposta
/// depois da gravacao em disco, entao uma queda do gateway nao perde evento confirmado. O nome do arquivo entra no
/// AAD: trocar conteudo entre arquivos falha. A chave fica ao lado do spool, protegida so por permissao de arquivo,
/// o que protege contra copia dos eventos sem a chave, nao contra quem controla a maquina.
/// </summary>
public sealed class EventSpool
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const string Extension = ".evt";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string directory;
    private readonly int maxEvents;
    private readonly byte[] key;
    private long dropped;
    private long corrupted;

    public EventSpool(string directory, int maxEvents = 100_000)
    {
        this.directory = directory;
        this.maxEvents = maxEvents;
        Directory.CreateDirectory(directory);
        key = LoadOrCreateKey(Path.Combine(directory, "spool.key"));
    }

    public int Depth => Directory.EnumerateFiles(directory, "*" + Extension).Count();

    /// <summary>Recusados pela central e descartados. Recuperaveis pelo backfill quando houver id de origem.</summary>
    public long Dropped => Interlocked.Read(ref dropped);

    public long Corrupted => Interlocked.Read(ref corrupted);

    /// <summary>Falso quando o limite foi atingido: quem chamou nao pode confirmar o recebimento.</summary>
    public async Task<bool> TryAppendAsync(EdgeEvent item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (Depth >= maxEvents)
        {
            return false;
        }

        var name = $"{item.ObservedAt.ToUnixTimeMilliseconds():D16}-{item.Id:N}{Extension}";
        var plain = JsonSerializer.SerializeToUtf8Bytes(item, Json);
        var sealedBytes = new byte[NonceSize + plain.Length + TagSize];
        var nonce = sealedBytes.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Encrypt(nonce, plain, sealedBytes.AsSpan(NonceSize, plain.Length), sealedBytes.AsSpan(NonceSize + plain.Length, TagSize), Encoding.UTF8.GetBytes(name));
        }

        CryptographicOperations.ZeroMemory(plain);

        // Temporario + renomear: nunca aparece arquivo pela metade como se fosse evento.
        var temporary = Path.Combine(directory, name + ".tmp");
        await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
        {
            await file.WriteAsync(sealedBytes, cancellationToken);
            file.Flush(flushToDisk: true);
        }

        File.Move(temporary, Path.Combine(directory, name));
        return true;
    }

    /// <summary>Os mais antigos primeiro. Arquivo adulterado e separado, sem interromper os demais.</summary>
    public async Task<IReadOnlyList<SpooledEvent>> ReadBatchAsync(int limit, CancellationToken cancellationToken)
    {
        var files = Directory.EnumerateFiles(directory, "*" + Extension).OrderBy(f => f, StringComparer.Ordinal).Take(limit).ToList();
        var items = new List<SpooledEvent>(files.Count);
        foreach (var path in files)
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            if (TryOpen(bytes, Path.GetFileName(path)) is { } item)
            {
                items.Add(new SpooledEvent(path, item));
            }
            else
            {
                Interlocked.Increment(ref corrupted);
                File.Move(path, path + ".corrupt", overwrite: true);
            }
        }

        return items;
    }

    public static void Remove(SpooledEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        File.Delete(item.Path);
    }

    public void Drop(SpooledEvent item)
    {
        Remove(item);
        Interlocked.Increment(ref dropped);
    }

    private EdgeEvent? TryOpen(byte[] bytes, string name)
    {
        if (bytes.Length < NonceSize + TagSize)
        {
            return null;
        }

        var plain = new byte[bytes.Length - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(bytes.AsSpan(0, NonceSize), bytes.AsSpan(NonceSize, plain.Length), bytes.AsSpan(NonceSize + plain.Length, TagSize), plain, Encoding.UTF8.GetBytes(name));
            return JsonSerializer.Deserialize<EdgeEvent>(plain, Json);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static byte[] LoadOrCreateKey(string path)
    {
        if (File.Exists(path))
        {
            return File.ReadAllBytes(path);
        }

        var created = RandomNumberGenerator.GetBytes(32);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using var file = new FileStream(path, options);
        file.Write(created);
        return created;
    }
}

public sealed record SpooledEvent(string Path, EdgeEvent Event);
