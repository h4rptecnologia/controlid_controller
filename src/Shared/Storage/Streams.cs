using System.Security.Cryptography;

namespace Platform.Shared.Storage;

/// <summary>Calcula o hash enquanto o conteudo passa, sem guardar nada.</summary>
internal sealed class HashingReadStream(Stream inner, IncrementalHash hash) : Stream
{
    public long BytesRead { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken);
        hash.AppendData(buffer.Span[..read]);
        BytesRead += read;
        return read;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        hash.AppendData(buffer, offset, read);
        BytesRead += read;
        return read;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Buffer em memoria que passa para arquivo temporario, apagado ao fechar, quando cresce demais.</summary>
internal sealed class SpillBuffer(int threshold) : Stream
{
    private Stream inner = new MemoryStream();
    private bool spilled;

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => true;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await SpillIfNeededAsync(buffer.Length, cancellationToken);
        await inner.WriteAsync(buffer, cancellationToken);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await SpillIfNeededAsync(count, cancellationToken);
        await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Use a escrita assincrona.");

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        inner.ReadAsync(buffer, cancellationToken);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void SetLength(long value) => inner.SetLength(value);

    public override void Flush() => inner.Flush();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        await base.DisposeAsync();
    }

    private async Task SpillIfNeededAsync(int incoming, CancellationToken cancellationToken)
    {
        if (spilled || inner.Length + incoming <= threshold)
        {
            return;
        }

        var file = new FileStream(
            Path.Combine(Path.GetTempPath(), "hub-" + Guid.NewGuid().ToString("N")),
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);

        inner.Position = 0;
        await inner.CopyToAsync(file, cancellationToken);
        await inner.DisposeAsync();
        inner = file;
        spilled = true;
    }
}
