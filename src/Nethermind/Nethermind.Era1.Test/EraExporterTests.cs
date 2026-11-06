// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using NSubstitute;
using NSubstitute.ReturnsExtensions;
using System.IO.Abstractions;
using Autofac;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.IO;
using Nethermind.Era1.Exceptions;
using Testably.Abstractions;

namespace Nethermind.Era1.Test;

public class EraExporterTests
{
    [TestCase(1, 0UL, 1UL - 1, 1UL, 1)]
    [TestCase(3, 0UL, 3UL - 1, 1UL, 3)]
    [TestCase(16, 0UL, 16UL - 1, 16UL, 1)]
    [TestCase(16, 0UL, 0UL, 16UL, 1)]
    [TestCase(32, 0UL, 32UL - 1, 16UL, 2)]
    [TestCase(32, 8UL, 0UL, 16UL, 2)]
    [TestCase(48, 8UL, 40UL - 1, 16UL, 2)]
    [TestCase(64 * 2 + 1, 0UL, 64UL * 2 + 1 - 1, 64UL, 3)]
    public async Task Export_ChainHasDifferentLength_CorrectNumberOfFilesCreated_WithFileName(int chainLength, ulong start, ulong end, ulong size, int expectedNumberOfFiles)
    {
        await using IContainer container = EraTestModule.BuildContainerBuilderWithBlockTreeOfLength(chainLength)
            .AddSingleton<IEraConfig>(new EraConfig() { MaxEra1Size = size })
            .Build();

        string tmpDirectory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();
        await sut.Export(tmpDirectory, start, end);

        int fileCount = container.Resolve<IFileSystem>().Directory.GetFiles(tmpDirectory).Length;
        int metaFile = 2;
        Assert.That(fileCount, Is.EqualTo(expectedNumberOfFiles + metaFile));
        string[] files = Directory.GetFiles(tmpDirectory);
        foreach (string file in files)
        {
            if (Path.GetFileName(file).Equals("accumulators.txt") || Path.GetFileName(file).Equals("checksums.txt"))
            {
                foreach (string line in File.ReadLines(file))
                {
                    ReadOnlySpan<char> lineSpan = line.AsSpan();
                    int spaceIndex = lineSpan.IndexOf(' ');

                    Assert.That(spaceIndex, Is.GreaterThan(0)); // not at beginning
                    Assert.That(spaceIndex, Is.LessThan(lineSpan.Length - 1)); // not at end

                    int secondSpaceIndex = lineSpan[(spaceIndex + 1)..].IndexOf(' ');
                    Assert.That(secondSpaceIndex, Is.EqualTo(-1), "More than two words found");

                    ReadOnlySpan<char> secondWord = lineSpan[(spaceIndex + 1)..];
                    Assert.That(secondWord.EndsWith(".era1".AsSpan()));
                    string fileName = secondWord.ToString();
                    Assert.That(File.Exists(Path.Combine(tmpDirectory, fileName)), $"{fileName} does not exist");
                }
            }
        }
    }

    [TestCase(0UL, 31UL, 32UL, 63UL, false, false)]
    [TestCase(32UL, 63UL, 0UL, 31UL, false, false)]
    [TestCase(0UL, 47UL, 16UL, 63UL, false, false)]
    [TestCase(0UL, 31UL, 32UL, 63UL, true, false)]
    [TestCase(32UL, 63UL, 16UL, 31UL, false, true)]
    [TestCase(16UL, 47UL, 48UL, 63UL, false, true)]
    [TestCase(0UL, 40UL, 0UL, 63UL, false, false)]
    public async Task Export_IntoDirectoryWithEarlierExport_WritesManifestsOfWholeDirectory(
        ulong firstFrom, ulong firstTo, ulong secondFrom, ulong secondTo, bool removeFirstEraFile, bool hashOnly)
    {
        const ulong eraSize = 16;
        await using IContainer container = EraTestModule.BuildContainerBuilderWithBlockTreeOfLength(64).Build();
        string directory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();
        await sut.Export(directory, firstFrom, firstTo);

        // Rewrite the earlier manifests as older exports wrote them, naming each epoch's placeholder file, or as hash-only
        // (geth-style) lines matched by position, and give the epochs the second export rewrites stale hashes that must be
        // replaced, not kept.
        foreach (string manifest in new[] { EraExporter.AccumulatorFileName, EraExporter.ChecksumsFileName })
        {
            string path = Path.Combine(directory, manifest);
            string[] lines = await File.ReadAllLinesAsync(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string[] columns = lines[i].Split(' ');
                ulong epoch = ulong.Parse(columns[1].Split('-')[1]);
                string hash = epoch >= secondFrom / eraSize && epoch <= secondTo / eraSize ? $"0x{new string('0', 64)}" : columns[0];
                lines[i] = hashOnly ? hash : $"{hash} {EraPathUtils.Filename(EraTestModule.TestNetwork, epoch, Keccak.Zero)}";
            }
            await File.WriteAllLinesAsync(path, lines);
        }

        ulong from = Math.Min(firstFrom, secondFrom);
        if (removeFirstEraFile)
        {
            File.Delete(Directory.GetFiles(directory, "*.era1").Order().First());
            from += eraSize;
        }

        await sut.Export(directory, secondFrom, secondTo);

        using TempPath reference = TempPath.GetTempDirectory();
        await sut.Export(reference.Path, from, Math.Max(firstTo, secondTo));
        using IEraStore eraStore = container.Resolve<IEraStoreFactory>().Create(directory, null);

        using (Assert.EnterMultipleScope())
        {
            foreach (string manifest in new[] { EraExporter.AccumulatorFileName, EraExporter.ChecksumsFileName })
            {
                Assert.That(await File.ReadAllLinesAsync(Path.Combine(directory, manifest)),
                    Is.EqualTo(await File.ReadAllLinesAsync(Path.Combine(reference.Path, manifest))), manifest);
            }

            Assert.That(Directory.GetFiles(directory, "*.era1"), Has.Length.EqualTo((int)((64 - from) / eraSize)), "one file per epoch");
            for (ulong block = from; block < 64; block += eraSize)
            {
                Assert.That(async () => await eraStore.FindBlockAndReceipts(block), Throws.Nothing, $"block {block}");
            }

            Block source = container.Resolve<IBlockTree>().FindBlock(45, BlockTreeLookupOptions.None)!;
            (Block? read, TxReceipt[]? receipts) = await eraStore.FindBlockAndReceipts(45);
            Assert.That(read?.Hash, Is.EqualTo(source.Hash));
            Assert.That(receipts?.Select(static r => r.GasUsedTotal), Is.EqualTo(container.Resolve<IReceiptStorage>().Get(source).Select(static r => r.GasUsedTotal)));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Export_WhenEpochHasPlaceholder_ReplacesItAndKeepsUnrelatedFiles(bool truncated)
    {
        await using IContainer container = EraTestModule.BuildContainerBuilderWithBlockTreeOfLength(16).Build();
        string directory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();
        await sut.Export(directory, 0, 15);
        string finalName = Path.GetFileName(Directory.GetFiles(directory, "*.era1").Single());
        string hash = finalName.Split('-')[2][..8];
        string[] unrelated = [$"xyz-00000-{hash}.era1", $"abc-00001-{hash}.era1", $"abc-00000-{hash}-noproofs.ere", $"abc-00000-{hash}.txt", $"abc-00000-{hash}.era1.bak"];
        foreach (string name in unrelated) File.Copy(Path.Combine(directory, finalName), Path.Combine(directory, name));

        // Left by an interrupted export, or finished and listed by the manifests: only its name tells that the export never published it.
        string placeholder = Path.Combine(directory, EraPathUtils.Filename(EraTestModule.TestNetwork, 0, Keccak.Zero));
        if (truncated)
        {
            await File.WriteAllBytesAsync(placeholder, (await File.ReadAllBytesAsync(Path.Combine(directory, finalName)))[..100]);
        }
        else
        {
            File.Move(Path.Combine(directory, finalName), placeholder);
            foreach (string manifest in new[] { EraExporter.AccumulatorFileName, EraExporter.ChecksumsFileName })
            {
                string path = Path.Combine(directory, manifest);
                await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path)).Replace(finalName, Path.GetFileName(placeholder)));
            }
        }

        await sut.Export(directory, 0, 15);

        Assert.That(Directory.GetFiles(directory).Select(Path.GetFileName),
            Is.EquivalentTo(unrelated.Append(finalName).Concat([EraExporter.AccumulatorFileName, EraExporter.ChecksumsFileName])));
        using IEraStore eraStore = container.Resolve<IEraStoreFactory>().Create(directory, null);
        Assert.That(async () => await eraStore.FindBlockAndReceipts(15), Throws.Nothing);
    }

    [Test]
    public async Task Export_WhenSiblingDiffersOnlyInHashCase_KeepsOneFile()
    {
        await using IContainer container = EraTestModule.BuildContainerBuilderWithBlockTreeOfLength(16).Build();
        string directory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();
        await sut.Export(directory, 0, 15);
        RenameToUpperCaseHash(Directory.GetFiles(directory, "*.era1").Single(), keepOriginal: false);

        await sut.Export(directory, 0, 15);

        Assert.That(Directory.GetFiles(directory, "*.era1"), Has.Length.EqualTo(1));
        using IEraStore eraStore = container.Resolve<IEraStoreFactory>().Create(directory, null);
        Assert.That(async () => await eraStore.FindBlockAndReceipts(15), Throws.Nothing);
    }

    [Test]
    public async Task Export_WhenLastEpochWouldLoseBlocks_ChangesNothing()
    {
        await using IContainer container = EraTestModule.BuildContainerBuilderWithBlockTreeOfLength(48).Build();
        string directory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();
        await sut.Export(directory, 0, 20);
        await sut.Export(directory, 32, 47);
        Dictionary<string, byte[]> before = Directory.GetFiles(directory).ToDictionary(static f => f, File.ReadAllBytes);

        // Epoch 1 alone could be rewritten as 16-31, but epoch 2 would lose blocks 41-47.
        Assert.That(() => sut.Export(directory, 0, 40), Throws.TypeOf<EraException>().With.Message.Contains("(blocks 32-47)"));
        Assert.That(Directory.GetFiles(directory).ToDictionary(static f => f, File.ReadAllBytes), Is.EqualTo(before));
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task Export_AfterAFailedReexport_KeepsTheManifestEntryOfAnUntouchedEpoch(bool reversed, bool placeholderNames)
    {
        await using IContainer container = EraTestModule.BuildContainerBuilderWithBlockTreeOfLength(64).AddSingleton(ListedInOrder(reversed)).Build();
        string directory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();
        await sut.Export(directory, 0, 20);
        string finished = Path.GetFileName(Directory.GetFiles(directory, $"{EraTestModule.TestNetwork}-00001-*.era1").Single());
        string[] Epoch1Entries() => [.. new[] { EraExporter.AccumulatorFileName, EraExporter.ChecksumsFileName }
            .SelectMany(manifest => File.ReadAllLines(Path.Combine(directory, manifest))).Where(static line => line.Contains("-00001-"))];
        string[] before = Epoch1Entries();
        // Releases up to 2.2.2 listed each placeholder name instead of the file it was renamed to.
        foreach (string manifest in new[] { EraExporter.AccumulatorFileName, EraExporter.ChecksumsFileName }.Where(_ => placeholderNames))
        {
            string path = Path.Combine(directory, manifest);
            await File.WriteAllLinesAsync(path, (await File.ReadAllLinesAsync(path)).Select(static line => line[..^"00000000.era1".Length] + "00000000.era1"));
        }
        for (ulong number = 32; number <= 40; number++)
            container.Resolve<IReceiptStorage>().RemoveReceipts(container.Resolve<IBlockTree>().FindBlock(number, BlockTreeLookupOptions.None)!);

        // Epoch 1 is rewritten under a new name beside the old one, then epoch 2 fails; a later export leaves epoch 1 alone.
        Assert.That(() => sut.Export(directory, 0, 40), Throws.TypeOf<EraException>());
        Assert.That(Directory.GetFiles(directory, $"{EraTestModule.TestNetwork}-00001-*.era1"), Has.Length.EqualTo(2));
        using (IEraStore eraStore = container.Resolve<IEraStoreFactory>().Create(directory, null))
            Assert.That(async () => await eraStore.FindBlockAndReceipts(16), Throws.Nothing);
        await sut.Export(directory, 48, 63);

        Assert.That(Epoch1Entries(), Is.EqualTo(before), $"both manifests keep {finished} with its hashes");
        using (IEraStore eraStore = container.Resolve<IEraStoreFactory>().Create(directory, null))
            Assert.That(async () => await eraStore.FindBlockAndReceipts(16), Throws.Nothing);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Export_WhenAnEpochHasTwoIdenticalFiles_RepairsItOnlyWhenRequested(bool requested)
    {
        await using IContainer container = EraTestModule.BuildContainerBuilderWithBlockTreeOfLength(64).Build();
        string directory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();
        await sut.Export(directory, 0, 47);
        File.Copy(Directory.GetFiles(directory, $"{EraTestModule.TestNetwork}-00001-*.era1").Single(), Path.Combine(directory, $"{EraTestModule.TestNetwork}-00001-0000000a.era1"));
        string checksums = Path.Combine(directory, EraExporter.ChecksumsFileName);
        await File.WriteAllLinesAsync(checksums, (await File.ReadAllLinesAsync(checksums)).Select(static line => line.Split(' ')[0]));
        Dictionary<string, byte[]> before = Directory.GetFiles(directory).ToDictionary(static f => f, File.ReadAllBytes);

        if (!requested)
        {
            // Neither a name nor the checksum tells the copies apart, so an export that leaves epoch 1 alone writes nothing.
            Assert.That(() => sut.Export(directory, 48, 63), Throws.TypeOf<EraException>().With.Message.Contains("Epoch 1 has more than one era1 file"));
            Assert.That(Directory.GetFiles(directory).ToDictionary(static f => f, File.ReadAllBytes), Is.EqualTo(before));
            return;
        }

        await sut.Export(directory, 16, 31);
        Assert.That(Directory.GetFiles(directory, $"{EraTestModule.TestNetwork}-00001-*.era1"), Has.Length.EqualTo(1));
        using IEraStore eraStore = container.Resolve<IEraStoreFactory>().Create(directory, null);
        Assert.That(async () => await eraStore.FindBlockAndReceipts(16), Throws.Nothing);
    }

    [Test]
    public async Task Export_WhenAnEpochFails_DeletesNoEarlierFile()
    {
        await using IContainer container = EraTestModule.BuildContainerBuilderWithBlockTreeOfLength(48).Build();
        string directory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();
        await sut.Export(directory, 0, 20);
        Dictionary<string, byte[]> before = Directory.GetFiles(directory).ToDictionary(static f => f, File.ReadAllBytes);
        for (ulong number = 32; number <= 40; number++)
            container.Resolve<IReceiptStorage>().RemoveReceipts(container.Resolve<IBlockTree>().FindBlock(number, BlockTreeLookupOptions.None)!);

        // Epoch 1 is rewritten as 16-31 and supersedes its earlier file, then epoch 2 fails on its missing receipts.
        Assert.That(() => sut.Export(directory, 0, 40), Throws.TypeOf<EraException>().With.Message.Contains("receipts"));
        Assert.That(before.Keys.Where(File.Exists).ToDictionary(static f => f, File.ReadAllBytes), Is.EqualTo(before));
    }

    [Test]
    public async Task Export_WhenEarlierExportStartedAtAnotherOffset_AdvisesTheSameOffset()
    {
        await using IContainer container = EraTestModule.BuildContainerBuilderWithBlockTreeOfLength(64).Build();
        string directory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();
        await sut.Export(directory, 5, 40);

        // 0..63 holds every existing file, but its epoch 0 is 0-15 while the existing one is 5-20.
        Assert.That(() => sut.Export(directory, 0, 63), Throws.TypeOf<EraException>().With.Message.Contains("same offset within an era (5), for example 5"));
        await sut.Export(directory, 5, 63);
        Assert.That(Directory.GetFiles(directory, "*.era1"), Has.Length.EqualTo(4));
    }

    [TestCase(32UL, 47UL, 32UL, 40UL, "(blocks 32-47)")]
    [TestCase(0UL, 47UL, 8UL, 47UL, "(blocks 0-15)")]
    public async Task Export_WhenItWouldLoseBlocksOfEarlierFile_ThrowsBeforeWriting(ulong firstFrom, ulong firstTo, ulong from, ulong to, string lostFile)
    {
        await using IContainer container = EraTestModule.BuildContainerBuilderWithBlockTreeOfLength(48).Build();
        string directory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();
        await sut.Export(directory, firstFrom, firstTo);
        Dictionary<string, byte[]> before = Directory.GetFiles(directory).ToDictionary(static f => f, File.ReadAllBytes);

        Assert.That(() => sut.Export(directory, from, to), Throws.TypeOf<EraException>().With.Message.Contains(lostFile));
        Assert.That(Directory.GetFiles(directory).ToDictionary(static f => f, File.ReadAllBytes), Is.EqualTo(before));
    }

    [Test]
    public async Task Export_OnCaseInsensitiveFileSystem_KeepsTheSurvivorOfACaseOnlyRename()
    {
        await using IContainer container = EraTestModule.BuildContainerBuilderWithBlockTreeOfLength(16).AddSingleton(CaseInsensitiveFileSystem()).Build();
        string directory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();
        await sut.Export(directory, 0, 15);
        string file = Directory.GetFiles(directory, "*.era1").Single();
        string hash = Path.GetFileName(file).Split('-')[2][..8];
        File.Move(file, Path.Combine(directory, Path.GetFileName(file).Replace(hash, hash.ToUpperInvariant())));

        // The new file replaces the upper-case one, which the file system no longer lists as a second name.
        await sut.Export(directory, 0, 15);

        Assert.That(Directory.GetFiles(directory, "*.era1"), Has.Length.EqualTo(1));
        using IEraStore eraStore = container.Resolve<IEraStoreFactory>().Create(directory, null);
        Assert.That(async () => await eraStore.FindBlockAndReceipts(15), Throws.Nothing);
    }

    [TestCase(1, 1UL)]
    [TestCase(2, 2UL)]
    [TestCase(2, 3UL)]
    [TestCase(99, 999UL)]
    public void Export_ExportBeyondAvailableBlocks_ThrowEraException(int chainLength, ulong to)
    {
        using IContainer container = EraTestModule.BuildContainerBuilderWithBlockTreeOfLength(chainLength)
            .Build();

        IEraExporter sut = container.Resolve<IEraExporter>();
        string tmpDirectory = container.ResolveTempDirPath();

        Assert.That(() => { _ = sut.Export(tmpDirectory, 0UL, to); }, Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public void Export_ReceiptsAreNull_ThrowEraException()
    {
        using IContainer container = EraTestModule.BuildContainerBuilderWithBlockTreeOfLength(10)
            .AddSingleton<IReceiptStorage>(Substitute.For<IReceiptStorage>())
            .Build();

        string tmpDirectory = container.ResolveTempDirPath();
        container.Resolve<IReceiptStorage>().Get(Arg.Any<Block>(), Arg.Any<bool>(), Arg.Any<bool>()).ReturnsNull();

        IEraExporter sut = container.Resolve<IEraExporter>();

        Assert.That(() => sut.Export(tmpDirectory, 0UL, 1UL), Throws.TypeOf<EraException>());
    }

    /// <summary>The real file system, listing files in ordinal order or in reverse.</summary>
    private static IFileSystem ListedInOrder(bool reversed)
    {
        IFileSystem real = new RealFileSystem();
        string[] Order(string[] files) => reversed ? [.. files.Order(StringComparer.Ordinal).Reverse()] : [.. files.Order(StringComparer.Ordinal)];
        IDirectory listing = Substitute.For<IDirectory>();
        listing.Exists(Arg.Any<string>()).Returns(static c => Directory.Exists(c.Arg<string>()));
        listing.CreateDirectory(Arg.Any<string>()).Returns(c => real.Directory.CreateDirectory(c.Arg<string>()));
        listing.EnumerateFiles(Arg.Any<string>()).Returns(c => Order(Directory.GetFiles(c.Arg<string>())));
        listing.GetFiles(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<EnumerationOptions>())
            .Returns(c => Order(Directory.GetFiles(c.ArgAt<string>(0), c.ArgAt<string>(1), c.ArgAt<EnumerationOptions>(2))));
        IFileSystem fileSystem = Substitute.For<IFileSystem>();
        fileSystem.File.Returns(real.File);
        fileSystem.Directory.Returns(listing);
        fileSystem.FileStream.Returns(real.FileStream);
        return fileSystem;
    }

    /// <summary>The real file system, except that File.Move and File.Delete match names ignoring case, as NTFS and APFS do.</summary>
    private static IFileSystem CaseInsensitiveFileSystem()
    {
        IFileSystem real = new RealFileSystem();
        static string? Find(string path) => Directory.EnumerateFiles(Path.GetDirectoryName(path)!)
            .FirstOrDefault(f => string.Equals(Path.GetFileName(f), Path.GetFileName(path), StringComparison.OrdinalIgnoreCase));
        IFile file = Substitute.For<IFile>();
        file.Exists(Arg.Any<string>()).Returns(static c => File.Exists(c.Arg<string>()));
        file.Create(Arg.Any<string>()).Returns(c => real.File.Create(c.Arg<string>()));
        file.ReadAllLines(Arg.Any<string>()).Returns(static c => File.ReadAllLines(c.Arg<string>()));
        file.When(static f => f.Delete(Arg.Any<string>())).Do(static c =>
        {
            if (Find(c.Arg<string>()) is { } existing) File.Delete(existing);
        });
        file.When(static f => f.Move(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())).Do(static c =>
        {
            if (Find(c.ArgAt<string>(1)) is { } existing) File.Delete(existing);
            File.Move(c.ArgAt<string>(0), c.ArgAt<string>(1), c.ArgAt<bool>(2));
        });
        IFileSystem fileSystem = Substitute.For<IFileSystem>();
        fileSystem.File.Returns(file);
        fileSystem.Directory.Returns(real.Directory);
        fileSystem.FileStream.Returns(real.FileStream);
        return fileSystem;
    }

    /// <summary>Copies or moves an era file to the name with its hash in upper case, a separate name only on a case-sensitive file system.</summary>
    private static void RenameToUpperCaseHash(string file, bool keepOriginal)
    {
        string name = Path.GetFileName(file);
        string hash = name.Split('-')[2][..8];
        string variant = Path.Combine(Path.GetDirectoryName(file)!, name.Replace(hash, hash.ToUpperInvariant()));
        Assume.That(variant, Is.Not.EqualTo(file));
        Assume.That(File.Exists(variant), Is.False, "needs a case-sensitive file system");
        if (keepOriginal) File.Copy(file, variant);
        else File.Move(file, variant);
    }
}
