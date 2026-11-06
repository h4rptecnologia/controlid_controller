using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Platform.Modules.Importation;

namespace Platform.UnitTests;

public sealed class FileInspectorTests
{
    private static readonly InspectionLimits Small = new()
    {
        MaxEntries = 50,
        MaxUncompressedBytes = 4 * 1024 * 1024,
        MaxEntryBytes = 2 * 1024 * 1024,
        MaxExpansionRatio = 50,
    };

    private static byte[] Zip(params (string Name, byte[] Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var stream = entry.Open();
                stream.Write(content);
            }
        }

        return buffer.ToArray();
    }

    private static byte[] Random(int size)
    {
        var bytes = new byte[size];
        new Random(size).NextBytes(bytes);
        return bytes;
    }

    private static Task<InspectionResult> Inspect(byte[] file, InspectionLimits? limits = null) =>
        FileInspector.InspectAsync(new MemoryStream(file), limits ?? Small, CancellationToken.None);

    private static byte[] Csv(string text = "id;name\n1;Ana\n") => Encoding.UTF8.GetBytes(text);

    // Formato oficial do fabricante

    [Fact]
    public async Task Documented_structure_with_csv_and_user_images_is_accepted()
    {
        var file = Zip(("users.csv", Csv()), ("user_images/1.jpg", Random(2048)));

        var result = await Inspect(file);

        Assert.Null(result.RejectionCode);
        Assert.Equal(DetectedTypes.Zip, result.DetectedType);
        Assert.Equal(2, result.EntryCount);
    }

    [Theory]
    [InlineData("id;name\n1;Ana\n")]
    [InlineData("id,name\r\n1,Ana\r\n")]
    [InlineData("\uFEFFid;name\n")]
    [InlineData("id\tname\n1\tAna\n")]
    public async Task Delimited_text_is_accepted(string text)
    {
        var result = await Inspect(Encoding.UTF8.GetBytes(text));

        Assert.Equal(DetectedTypes.DelimitedText, result.DetectedType);
        Assert.Null(result.RejectionCode);
    }

    [Fact]
    public async Task Windows_1252_text_is_accepted()
    {
        var result = await Inspect(Encoding.Latin1.GetBytes("nome;cidade\nJoão;São Paulo\n"));

        Assert.Equal(DetectedTypes.DelimitedText, result.DetectedType);
    }

    // Tipo detectado pelo conteudo

    [Theory]
    [InlineData(new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03 })] // Executavel do Windows
    [InlineData(new byte[] { 0x7F, 0x45, 0x4C, 0x46, 0x02 })] // ELF
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D })] // PDF sem separador de coluna
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00 })] // JPEG
    public async Task Other_formats_are_refused_by_content(byte[] start)
    {
        var result = await Inspect(start);

        Assert.Equal(RejectionCodes.UnsupportedType, result.RejectionCode);
    }

    [Fact]
    public async Task Empty_file_is_refused()
    {
        Assert.Equal(RejectionCodes.Empty, (await Inspect([])).RejectionCode);
    }

    [Fact]
    public async Task Text_without_any_separator_is_refused()
    {
        Assert.Equal(RejectionCodes.UnsupportedType, (await Inspect(Encoding.UTF8.GetBytes("apenas uma palavra sem separador"))).RejectionCode);
    }

    // Caminhos

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("user_images/../../fora.jpg")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/evil.dll")]
    [InlineData("./users.csv")]
    [InlineData("pasta/./arquivo")]
    public async Task Unsafe_paths_are_refused(string name)
    {
        var result = await Inspect(Zip((name, Csv())));

        Assert.Equal(RejectionCodes.UnsafePath, result.RejectionCode);
    }

    [Fact]
    public void Backslash_and_control_characters_are_unsafe()
    {
        Assert.False(FileInspector.IsSafePath("..\\..\\windows", 260));
        Assert.False(FileInspector.IsSafePath("nome\u0000.jpg.exe", 260));
        Assert.False(FileInspector.IsSafePath(new string('a', 261), 260));
        Assert.True(FileInspector.IsSafePath("user_images/foto 1.jpg", 260));
    }

    [Fact]
    public async Task Names_differing_only_by_case_are_refused()
    {
        var result = await Inspect(Zip(("Users.csv", Csv()), ("users.CSV", Csv())));

        Assert.Equal(RejectionCodes.DuplicateName, result.RejectionCode);
    }

    [Fact]
    public async Task Symbolic_link_entry_is_refused()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("link");
            entry.ExternalAttributes = 0xA1FF << 16; // S_IFLNK
            using var stream = entry.Open();
            stream.Write("/etc/passwd"u8);
        }

        Assert.Equal(RejectionCodes.SpecialEntry, (await Inspect(buffer.ToArray())).RejectionCode);
    }

    // Bombas e limites

    [Fact]
    public async Task Highly_compressed_entry_is_refused_by_ratio()
    {
        var result = await Inspect(Zip(("users.csv", new byte[1024 * 1024])));

        Assert.Equal(RejectionCodes.ExpansionRatio, result.RejectionCode);
    }

    [Fact]
    public async Task Entry_larger_than_the_limit_is_refused_while_reading()
    {
        var limits = new InspectionLimits { MaxEntryBytes = 1024, MaxUncompressedBytes = 1024 * 1024, MaxEntries = 10, MaxExpansionRatio = 1000 };

        var result = await Inspect(Zip(("a.bin", Random(4096))), limits);

        Assert.Equal(RejectionCodes.EntryTooLarge, result.RejectionCode);
    }

    [Fact]
    public async Task Total_uncompressed_size_is_limited()
    {
        var limits = new InspectionLimits { MaxEntryBytes = 4096, MaxUncompressedBytes = 6000, MaxEntries = 10, MaxExpansionRatio = 1000 };

        var result = await Inspect(Zip(("a.bin", Random(4000)), ("b.bin", Random(4000))), limits);

        Assert.Equal(RejectionCodes.TooLarge, result.RejectionCode);
    }

    [Fact]
    public async Task Too_many_entries_are_refused_before_opening_the_archive()
    {
        var entries = Enumerable.Range(0, 51).Select(i => ($"f{i}.txt", Csv())).ToArray();

        Assert.Equal(RejectionCodes.TooManyEntries, (await Inspect(Zip(entries))).RejectionCode);
    }

    [Theory]
    [InlineData("inner.zip")]
    [InlineData("inner.ZIP")]
    [InlineData("backup.tar")]
    [InlineData("dados.gz")]
    [InlineData("pacote.7z")]
    public async Task Nested_archive_by_name_is_refused(string name)
    {
        Assert.Equal(RejectionCodes.NestedArchive, (await Inspect(Zip((name, Random(512))))).RejectionCode);
    }

    [Fact]
    public async Task Nested_archive_disguised_with_another_name_is_refused_by_content()
    {
        var inner = Zip(("a.txt", Random(1024)));

        Assert.Equal(RejectionCodes.NestedArchive, (await Inspect(Zip(("foto.jpg", inner)))).RejectionCode);
    }

    [Fact]
    public async Task Lying_size_in_the_central_directory_does_not_bypass_limits()
    {
        var file = Zip(("users.csv", Random(4096)));
        PatchCentralDirectory(file, directoryEntry => BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(directoryEntry + 24), 10));

        var result = await Inspect(file);

        Assert.NotNull(result.RejectionCode);
    }

    [Fact]
    public async Task Overlapping_entries_are_refused()
    {
        // Tecnica de bomba sem aninhamento: varias entradas do diretorio apontam para os mesmos dados.
        var original = Zip(("a.bin", Random(4096)));
        var end = FindSignature(original, 0x06054B50);
        var directoryStart = (int)BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(end + 16));
        var directorySize = (int)BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(end + 12));
        var record = original.AsSpan(directoryStart, directorySize).ToArray();
        var copy = (byte[])record.Clone();
        copy[46] = (byte)'b'; // Mesmo dado, outro nome.

        var forged = new byte[directoryStart + record.Length + copy.Length + 22];
        original.AsSpan(0, directoryStart).CopyTo(forged);
        record.CopyTo(forged, directoryStart);
        copy.CopyTo(forged, directoryStart + record.Length);
        var newEnd = directoryStart + record.Length + copy.Length;
        original.AsSpan(end, 22).CopyTo(forged.AsSpan(newEnd));
        BinaryPrimitives.WriteUInt16LittleEndian(forged.AsSpan(newEnd + 8), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(forged.AsSpan(newEnd + 10), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(forged.AsSpan(newEnd + 12), (uint)(record.Length + copy.Length));

        Assert.Equal(RejectionCodes.OverlappingEntries, (await Inspect(forged)).RejectionCode);
    }

    private static int FindSignature(byte[] file, uint signature)
    {
        for (var i = file.Length - 4; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(i)) == signature)
            {
                return i;
            }
        }

        throw new InvalidOperationException("Assinatura nao encontrada.");
    }

    // Integridade

    [Fact]
    public async Task Wrong_checksum_is_refused()
    {
        var file = Zip(("users.csv", Random(4096)));
        PatchCentralDirectory(file, directoryEntry => file[directoryEntry + 16] ^= 0xFF);

        Assert.Equal(RejectionCodes.ChecksumMismatch, (await Inspect(file)).RejectionCode);
    }

    [Fact]
    public async Task Encrypted_entry_is_refused()
    {
        var file = Zip(("users.csv", Random(4096)));
        PatchCentralDirectory(file, directoryEntry => file[directoryEntry + 8] |= 1);

        Assert.Equal(RejectionCodes.EncryptedEntry, (await Inspect(file)).RejectionCode);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(100)]
    public async Task Truncated_archive_is_refused(int cut)
    {
        var file = Zip(("users.csv", Random(4096)));

        Assert.Equal(RejectionCodes.CorruptArchive, (await Inspect(file[..^cut])).RejectionCode);
    }

    [Fact]
    public async Task Garbage_starting_like_a_zip_is_refused()
    {
        var file = new byte[] { 0x50, 0x4B, 0x03, 0x04 }.Concat(Random(1024)).ToArray();

        Assert.Equal(RejectionCodes.CorruptArchive, (await Inspect(file)).RejectionCode);
    }

    [Fact]
    public async Task Expired_inspection_is_refused_instead_of_accepted()
    {
        var limits = new InspectionLimits { MaxDuration = TimeSpan.Zero, MaxEntries = 50, MaxUncompressedBytes = 1024 * 1024, MaxEntryBytes = 1024 * 1024, MaxExpansionRatio = 1000 };
        var entries = Enumerable.Range(0, 40).Select(i => ($"f{i}.bin", Random(8192))).ToArray();

        var result = await Inspect(Zip(entries), limits);

        Assert.Equal(RejectionCodes.Timeout, result.RejectionCode);
    }

    private static void PatchCentralDirectory(byte[] file, Action<int> patch)
    {
        for (var i = file.Length - 46; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(i)) == 0x02014B50)
            {
                patch(i);
                return;
            }
        }

        throw new InvalidOperationException("Diretorio central nao encontrado.");
    }
}
