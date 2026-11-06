using System.Security.Cryptography;
using Platform.Shared.Storage;

namespace Platform.UnitTests;

public sealed class BlobCipherTests
{
    private const int Segment = 64;
    private const int Header = 16;
    private const int Tag = 16;
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(BlobCipher.DekSize);
    private static readonly byte[] Context = BlobCipher.BuildContext(Guid.NewGuid(), Guid.NewGuid(), "import_original", "personal");

    private static async Task<byte[]> EncryptAsync(byte[] plain, byte[]? key = null, byte[]? context = null)
    {
        using var output = new MemoryStream();
        using var input = new MemoryStream(plain);
        await BlobCipher.EncryptAsync(input, output, key ?? Key, context ?? Context, CancellationToken.None, Segment);
        return output.ToArray();
    }

    private static async Task<byte[]> DecryptAsync(byte[] cipher, byte[]? key = null, byte[]? context = null)
    {
        using var output = new MemoryStream();
        using var input = new MemoryStream(cipher);
        await BlobCipher.DecryptAsync(input, output, key ?? Key, context ?? Context, CancellationToken.None);
        return output.ToArray();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(Segment - 1)]
    [InlineData(Segment)]
    [InlineData(Segment + 1)]
    [InlineData(Segment * 2)]
    [InlineData(Segment * 5 + 7)]
    public async Task Round_trip_at_segment_boundaries(int size)
    {
        var plain = RandomNumberGenerator.GetBytes(size);
        Assert.Equal(plain, await DecryptAsync(await EncryptAsync(plain)));
    }

    [Fact]
    public async Task Same_content_encrypts_differently()
    {
        var plain = new byte[Segment];
        Assert.NotEqual(await EncryptAsync(plain), await EncryptAsync(plain));
    }

    [Fact]
    public async Task Ciphertext_does_not_contain_the_plaintext()
    {
        var plain = "CPF 123.456.789-00 de uma pessoa"u8.ToArray();
        var cipher = await EncryptAsync(plain);
        Assert.Equal(-1, cipher.AsSpan().IndexOf(plain.AsSpan(0, 10)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(9)]
    [InlineData(Header)]
    [InlineData(Header + Segment + Tag)]
    public async Task Any_flipped_byte_fails(int position)
    {
        var cipher = await EncryptAsync(RandomNumberGenerator.GetBytes(Segment * 3));
        cipher[position] ^= 0x01;
        await Assert.ThrowsAsync<BlobIntegrityException>(() => DecryptAsync(cipher));
    }

    [Fact]
    public async Task Truncating_whole_final_segment_fails()
    {
        var cipher = await EncryptAsync(RandomNumberGenerator.GetBytes(Segment * 3));
        var truncated = cipher[..(Header + (Segment + Tag) * 2)];
        await Assert.ThrowsAsync<BlobIntegrityException>(() => DecryptAsync(truncated));
    }

    [Fact]
    public async Task Reordering_segments_fails()
    {
        var cipher = await EncryptAsync(RandomNumberGenerator.GetBytes(Segment * 3));
        var size = Segment + Tag;
        var swapped = (byte[])cipher.Clone();
        cipher.AsSpan(Header, size).CopyTo(swapped.AsSpan(Header + size));
        cipher.AsSpan(Header + size, size).CopyTo(swapped.AsSpan(Header));
        await Assert.ThrowsAsync<BlobIntegrityException>(() => DecryptAsync(swapped));
    }

    [Fact]
    public async Task Appending_data_fails()
    {
        var cipher = await EncryptAsync(RandomNumberGenerator.GetBytes(Segment * 2));
        await Assert.ThrowsAsync<BlobIntegrityException>(() => DecryptAsync([.. cipher, .. new byte[Segment + Tag]]));
    }

    [Fact]
    public async Task Wrong_key_fails()
    {
        var cipher = await EncryptAsync([1, 2, 3]);
        await Assert.ThrowsAsync<BlobIntegrityException>(() => DecryptAsync(cipher, RandomNumberGenerator.GetBytes(BlobCipher.DekSize)));
    }

    [Fact]
    public async Task Moving_blob_to_other_tenant_or_object_fails()
    {
        var tenant = Guid.NewGuid();
        var blob = Guid.NewGuid();
        var context = BlobCipher.BuildContext(tenant, blob, "export", "personal");
        var cipher = await EncryptAsync([1, 2, 3], context: context);

        await Assert.ThrowsAsync<BlobIntegrityException>(() => DecryptAsync(cipher, context: BlobCipher.BuildContext(Guid.NewGuid(), blob, "export", "personal")));
        await Assert.ThrowsAsync<BlobIntegrityException>(() => DecryptAsync(cipher, context: BlobCipher.BuildContext(tenant, Guid.NewGuid(), "export", "personal")));
        await Assert.ThrowsAsync<BlobIntegrityException>(() => DecryptAsync(cipher, context: BlobCipher.BuildContext(tenant, blob, "biometric_template", "personal")));
        await Assert.ThrowsAsync<BlobIntegrityException>(() => DecryptAsync(cipher, context: BlobCipher.BuildContext(tenant, blob, "export", "internal")));
    }

    [Fact]
    public void Context_fields_cannot_collide()
    {
        var tenant = Guid.NewGuid();
        var blob = Guid.NewGuid();
        Assert.NotEqual(BlobCipher.BuildContext(tenant, blob, "ab", "c"), BlobCipher.BuildContext(tenant, blob, "a", "bc"));
    }

    [Fact]
    public async Task Garbage_and_empty_input_fail_closed()
    {
        await Assert.ThrowsAsync<BlobIntegrityException>(() => DecryptAsync([]));
        await Assert.ThrowsAsync<BlobIntegrityException>(() => DecryptAsync(RandomNumberGenerator.GetBytes(200)));
    }

    [Fact]
    public async Task Oversized_declared_segment_is_rejected_before_allocating()
    {
        var cipher = await EncryptAsync([1, 2, 3]);
        cipher[5] = 0x7F;
        await Assert.ThrowsAsync<BlobIntegrityException>(() => DecryptAsync(cipher));
    }

    [Fact]
    public void Biometric_and_secret_blobs_never_use_the_general_key()
    {
        Assert.Equal(KeyPurpose.Biometric, EncryptedBlobStore.PurposeOf(BlobKind.BiometricTemplate));
        Assert.Equal(KeyPurpose.Secret, EncryptedBlobStore.PurposeOf(BlobKind.DeviceSecret));
        var tenant = Guid.NewGuid();
        Assert.NotEqual(EncryptedBlobStore.KeyName(tenant, KeyPurpose.General), EncryptedBlobStore.KeyName(tenant, KeyPurpose.Biometric));
        Assert.NotEqual(EncryptedBlobStore.KeyName(tenant, KeyPurpose.General), EncryptedBlobStore.KeyName(Guid.NewGuid(), KeyPurpose.General));
    }
}
