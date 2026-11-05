// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using NSubstitute;
using NSubstitute.ReturnsExtensions;
using Nethermind.EraE.Config;
using Nethermind.EraE.E2Store;
using EraException = Nethermind.Era1.Exceptions.EraException;
using Nethermind.EraE.Export;
using Nethermind.EraE.Store;
using NUnit.Framework;
using Testably.Abstractions;

namespace Nethermind.EraE.Test.Export;

public class EraExporterTests
{
    [TestCase(1, 0UL, 0UL, 1UL, 1)]
    [TestCase(3, 0UL, 2UL, 1UL, 3)]
    [TestCase(16, 0UL, 15UL, 16UL, 1)]
    [TestCase(32, 0UL, 31UL, 16UL, 2)]
    [TestCase(48, 8UL, 39UL, 16UL, 3)]
    public async Task Export_WithVaryingChainLength_CreatesCorrectNumberOfEpochFiles(
        int chainLength, ulong from, ulong to, ulong eraSize, int expectedEraFiles)
    {
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(chainLength)
            .AddSingleton<IEraEConfig>(new EraEConfig
            {
                MaxEraSize = eraSize,
                NetworkName = EraETestModule.TestNetwork
            })
            .Build();

        string tmpDirectory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();
        await sut.Export(tmpDirectory, from, to);

        string[] allFiles = System.IO.Directory.GetFiles(tmpDirectory);
        string[] eraFiles = allFiles.Where(f => f.EndsWith(EraPathUtils.FileExtension)).ToArray();
        Assert.That(eraFiles.Length, Is.EqualTo(expectedEraFiles));
    }

    [Test]
    public async Task Export_WhenLegacyEraeFileExists_SkipsEpochWithoutWritingEre()
    {
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(16)
            .AddSingleton<IEraEConfig>(new EraEConfig { MaxEraSize = 16, NetworkName = EraETestModule.TestNetwork })
            .Build();

        string tmpDirectory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();
        await sut.Export(tmpDirectory, 0, 15);

        string ereFile = System.IO.Directory.GetFiles(tmpDirectory, $"*{EraPathUtils.FileExtension}").Single();
        string eraeFile = System.IO.Path.ChangeExtension(ereFile, EraPathUtils.LegacyFileExtension);
        System.IO.File.Move(ereFile, eraeFile);

        await sut.Export(tmpDirectory, 0, 15);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(System.IO.File.Exists(eraeFile), Is.True, "pre-existing legacy .erae file must be kept on re-export");
            Assert.That(System.IO.Directory.GetFiles(tmpDirectory, $"*{EraPathUtils.FileExtension}"), Is.Empty,
                "epoch already present as .erae must be skipped, not re-exported as a duplicate .ere");
        }
    }

    [TestCase(0UL, 31UL, 32UL, 63UL, false)]
    [TestCase(32UL, 63UL, 0UL, 31UL, false)]
    [TestCase(0UL, 47UL, 16UL, 63UL, false)]
    [TestCase(0UL, 31UL, 32UL, 63UL, true)]
    [TestCase(0UL, 40UL, 0UL, 63UL, false)]
    [TestCase(8UL, 15UL, 0UL, 15UL, false)]
    public async Task Export_IntoDirectoryWithEarlierExport_WritesManifestsOfWholeDirectory(
        ulong firstFrom, ulong firstTo, ulong secondFrom, ulong secondTo, bool removeFirstEraFile)
    {
        const ulong eraSize = 16;
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(64).Build();
        string directory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();
        await sut.Export(directory, firstFrom, firstTo);
        ulong from = Math.Min(firstFrom, secondFrom);
        if (removeFirstEraFile)
        {
            File.Delete(Directory.GetFiles(directory, $"*{EraPathUtils.FileExtension}").Order().First());
            from += eraSize;
        }

        await sut.Export(directory, secondFrom, secondTo);

        ulong to = Math.Max(firstTo, secondTo);
        using TempPath reference = TempPath.GetTempDirectory();
        await sut.Export(reference.Path, from, to);

        using (Assert.EnterMultipleScope())
        {
            foreach (string manifest in new[] { EraExporter.AccumulatorFileName, EraExporter.ChecksumsSHA256FileName, EraExporter.ChecksumsFileName })
            {
                Assert.That(await File.ReadAllLinesAsync(Path.Combine(directory, manifest)),
                    Is.EqualTo(await File.ReadAllLinesAsync(Path.Combine(reference.Path, manifest))), manifest);
            }

            Assert.That(Directory.GetFiles(directory).Count(EraPathUtils.IsEraFile), Is.EqualTo((int)((to - from) / eraSize + 1)), "one file per epoch");
            await AssertReadsBack(container, directory, from, to);
        }
    }

    [TestCase(false, 0UL, 0UL, 15UL)]
    [TestCase(false, 0UL, 4UL, 12UL)]
    [TestCase(true, 1UL, 1UL, 15UL)]
    public async Task Export_WhenFileCoversRequestedBlocks_ReusesIt(bool postMerge, ulong firstFrom, ulong from, ulong to)
    {
        await using IContainer container = (postMerge
            ? EraETestModule.BuildContainerBuilderWithPostMergeBlockTreeOfLength(16)
            : EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(16)).Build();
        string directory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();
        await sut.Export(directory, firstFrom, 15);
        string[] files = Directory.GetFiles(directory);
        string[] checksums = await File.ReadAllLinesAsync(Path.Combine(directory, EraExporter.ChecksumsSHA256FileName));
        DropReceipts(container, firstFrom, 15);

        await sut.Export(directory, from, to);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Directory.GetFiles(directory), Is.EquivalentTo(files));
            Assert.That(await File.ReadAllLinesAsync(Path.Combine(directory, EraExporter.ChecksumsSHA256FileName)), Is.EqualTo(checksums));
        }
    }

    [TestCase("-noproofs.ere")]
    [TestCase(".erae")]
    public async Task Export_WhenEpochHasFinishedPlaceholder_RewritesEpoch(string suffix)
    {
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(16).Build();
        string directory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();
        await sut.Export(directory, 0, 15);
        string finalName = Path.GetFileName(Directory.GetFiles(directory, $"*{EraPathUtils.FileExtension}").Single());

        // Finished and listed by the manifests: only its name tells that the export never published it.
        string placeholderName = $"{EraETestModule.TestNetwork}-00000-00000000{suffix}";
        File.Move(Path.Combine(directory, finalName), Path.Combine(directory, placeholderName));
        foreach (string manifest in new[] { EraExporter.AccumulatorFileName, EraExporter.ChecksumsSHA256FileName })
        {
            string path = Path.Combine(directory, manifest);
            await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path)).Replace(finalName, placeholderName));
        }

        await sut.Export(directory, 0, 15);

        Assert.That(Directory.GetFiles(directory).Where(EraPathUtils.IsEraFile).Select(Path.GetFileName), Is.EqualTo(new[] { finalName }));
        await AssertReadsBack(container, directory, 0, 15);
    }

    [Test]
    public async Task Export_WhenWiderLegacyFileExists_ReusesItAndDeletesOnlyFilesItSupersedes()
    {
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(16).Build();
        string directory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();
        using TempPath partial = TempPath.GetTempDirectory();
        await sut.Export(partial.Path, 0, 8);
        await sut.Export(directory, 0, 15);
        string full = Directory.GetFiles(directory, $"*{EraPathUtils.FileExtension}").Single();
        string legacy = Path.ChangeExtension(full, EraPathUtils.LegacyFileExtension);
        File.Move(full, legacy);

        string partialFile = Directory.GetFiles(partial.Path, $"*{EraPathUtils.FileExtension}").Single();
        File.Copy(partialFile, Path.Combine(directory, Path.GetFileName(partialFile)));
        await File.WriteAllTextAsync(Path.Combine(directory, EraPathUtils.Filename(EraETestModule.TestNetwork, 0, Keccak.Zero)), "interrupted");
        string hash = Path.GetFileName(partialFile).Split('-')[2];
        string[] unrelated = [$"xyz-00000-{hash}-noproofs.ere", $"abc-00001-{hash}-noproofs.ere", $"abc-00000-{hash}.era1", $"abc-00000-{hash}.txt", $"abc-00000-{hash}-noproofs.ere.bak"];
        foreach (string name in unrelated) File.Copy(partialFile, Path.Combine(directory, name));
        DropReceipts(container, 0, 15);

        await sut.Export(directory, 0, 8);

        Assert.That(Directory.GetFiles(directory).Select(Path.GetFileName), Is.EquivalentTo(unrelated.Append(Path.GetFileName(legacy))
            .Concat([EraExporter.AccumulatorFileName, EraExporter.ChecksumsSHA256FileName, EraExporter.ChecksumsFileName])));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Export_WhenEarlierFileWouldLoseBlocksOrIsUnreadable_ThrowsAndChangesNothing(bool unreadable)
    {
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(48).Build();
        string directory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();
        await sut.Export(directory, 32, 40);
        if (unreadable) await File.WriteAllTextAsync(Path.Combine(directory, "abc-00002-12345678-noproofs.ere"), "not an era file");
        Dictionary<string, byte[]> before = Directory.GetFiles(directory).ToDictionary(static f => f, File.ReadAllBytes);

        Assert.That(() => sut.Export(directory, unreadable ? 32UL : 40UL, 47),
            Throws.TypeOf<EraException>().With.Message.Contains(unreadable ? "12345678" : "(blocks 32-40)"));
        Assert.That(Directory.GetFiles(directory).ToDictionary(static f => f, File.ReadAllBytes), Is.EqualTo(before));
    }

    [Test]
    public async Task Export_WhenMoveFails_KeepsEarlierFile()
    {
        IFileSystem real = new RealFileSystem();
        bool failMoves = false;
        IFile file = Substitute.For<IFile>();
        file.Exists(Arg.Any<string>()).Returns(static c => File.Exists(c.Arg<string>()));
        file.Create(Arg.Any<string>()).Returns(c => real.File.Create(c.Arg<string>()));
        file.ReadAllLines(Arg.Any<string>()).Returns(static c => File.ReadAllLines(c.Arg<string>()));
        file.When(static f => f.Delete(Arg.Any<string>())).Do(static c => File.Delete(c.Arg<string>()));
        file.When(static f => f.Move(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())).Do(c =>
        {
            if (failMoves) throw new IOException("move failed");
            File.Move(c.ArgAt<string>(0), c.ArgAt<string>(1), c.ArgAt<bool>(2));
        });
        IFileSystem fileSystem = Substitute.For<IFileSystem>();
        fileSystem.File.Returns(file);
        fileSystem.Directory.Returns(real.Directory);
        fileSystem.FileStream.Returns(real.FileStream);

        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(48).AddSingleton(fileSystem).Build();
        string directory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();
        await sut.Export(directory, 32, 40);
        Dictionary<string, byte[]> before = Directory.GetFiles(directory).ToDictionary(static f => f, File.ReadAllBytes);
        failMoves = true;

        Assert.That(() => sut.Export(directory, 32, 47), Throws.TypeOf<IOException>());
        Assert.That(Directory.GetFiles(directory).ToDictionary(static f => f, File.ReadAllBytes), Is.EqualTo(before));
    }

    [Test]
    public async Task Export_WhenCalled_CreatesMetadataFile([Values("checksums_sha256.txt", "checksums.txt", "accumulators.txt")] string fileName)
    {
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(32).Build();

        string tmpDirectory = container.ResolveTempDirPath();
        await container.Resolve<IEraExporter>().Export(tmpDirectory, 0UL, 0UL);

        Assert.That(System.IO.File.Exists(System.IO.Path.Combine(tmpDirectory, fileName)), Is.True);
    }

    [Test]
    public async Task Export_WhenCalled_ChecksumsFileHasCorrectFormat()
    {
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(32).Build();

        string tmpDirectory = container.ResolveTempDirPath();
        await container.Resolve<IEraExporter>().Export(tmpDirectory, 0UL, 0UL);

        string[] lines = await System.IO.File.ReadAllLinesAsync(
            System.IO.Path.Combine(tmpDirectory, EraExporter.ChecksumsSHA256FileName));

        foreach (string line in lines)
        {
            ReadOnlySpan<char> span = line.AsSpan();
            int spaceIdx = span.IndexOf(' ');
            using (Assert.EnterMultipleScope())
            {
                Assert.That(spaceIdx, Is.GreaterThan(0), "each line should be '<hash> <filename>'");
                Assert.That(span[(spaceIdx + 1)..].EndsWith(EraPathUtils.FileExtension.AsSpan()), Is.True);
            }
        }
    }

    [Test]
    public async Task Export_WhenCalled_EraFilesHaveCorrectExtension()
    {
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(16).Build();

        string tmpDirectory = container.ResolveTempDirPath();
        await container.Resolve<IEraExporter>().Export(tmpDirectory, 0UL, 0UL);

        string[] eraFiles = System.IO.Directory.GetFiles(tmpDirectory, $"*{EraPathUtils.FileExtension}");
        Assert.That(eraFiles, Is.Not.Empty);
        foreach (string eraFile in eraFiles)
        {
            Assert.That(eraFile, Does.EndWith(EraPathUtils.FileExtension));
        }
    }

    [Test]
    public async Task Export_WithToExceedingHeadBlock_ThrowsArgumentException()
    {
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(5).Build();

        string tmpDirectory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();

        Assert.That(() => sut.Export(tmpDirectory, 0UL, 999UL), Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public async Task Export_WithFromExceedingTo_ThrowsArgumentException()
    {
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(32).Build();

        string tmpDirectory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();

        Assert.That(() => sut.Export(tmpDirectory, 20UL, 10UL), Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public async Task Export_WhenReceiptsStorageReturnsNull_ThrowsEraException()
    {
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(10)
            .AddSingleton<IReceiptStorage>(Substitute.For<IReceiptStorage>())
            .Build();

        container.Resolve<IReceiptStorage>()
            .Get(Arg.Any<Block>(), Arg.Any<bool>(), Arg.Any<bool>())
            .ReturnsNull();

        IEraExporter sut = container.Resolve<IEraExporter>();
        string tmpDirectory = container.ResolveTempDirPath();

        Assert.That(() => sut.Export(tmpDirectory, 0UL, 1UL), Throws.TypeOf<EraException>());
    }

    [Test]
    public async Task Export_WithToZero_ExportsUpToHead()
    {
        const int chainLength = 32;
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(chainLength).Build();

        string tmpDirectory = container.ResolveTempDirPath();
        await container.Resolve<IEraExporter>().Export(tmpDirectory, 0UL, to: 0UL);

        string[] eraFiles = System.IO.Directory.GetFiles(tmpDirectory, $"*{EraPathUtils.FileExtension}");
        Assert.That(eraFiles, Is.Not.Empty, "exporting to 0 should default to head");
    }

    [Test]
    public void Export_WithDestinationAsExistingFile_ThrowsArgumentException()
    {
        using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(10).Build();

        string tmpFile = container.ResolveTempFilePath();
        System.IO.File.WriteAllText(tmpFile, "existing");

        IEraExporter sut = container.Resolve<IEraExporter>();
        Assert.That(() => sut.Export(tmpFile, 0UL, 0UL), Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public async Task Export_WithPostMergeChain_ProducesEpochWithNoTdProofOrAccumulatorEntries()
    {
        const ulong chainLength = 16;
        await using IContainer container = EraETestModule.BuildContainerBuilderWithPostMergeBlockTreeOfLength(chainLength).Build();

        string tmpDirectory = container.ResolveTempDirPath();
        // Export from block 1: genesis is pre-merge in all block trees, so starting at 1 ensures a pure post-merge epoch.
        await container.Resolve<IEraExporter>().Export(tmpDirectory, 1UL, 0UL);

        string[] eraFiles = Directory.GetFiles(tmpDirectory, $"*{EraPathUtils.FileExtension}");
        Assert.That(eraFiles, Has.Length.EqualTo(1), "one epoch for blocks 1-15");

        AssertNoPreMergeEntries(eraFiles[0]);
    }

    [Test]
    public async Task Export_WhenPoSSwitcherMarksGenesisPostMerge_ProducesEpochWithNoTdProofOrAccumulatorEntries()
    {
        Block genesis = Build.A.Block.Genesis
            .WithTimestamp(1_742_212_800)
            .WithDifficulty(BlockHeaderBuilder.DefaultDifficulty)
            .TestObject;
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(genesis);
        blockTree.FindBlock(0UL, BlockTreeLookupOptions.DoNotCreateLevelIfMissing).Returns(genesis);

        IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
        receiptStorage.Get(genesis, true, false).Returns([]);

        IPoSSwitcher poSSwitcher = Substitute.For<IPoSSwitcher>();
        poSSwitcher.IsPostMerge(genesis.Header).Returns(true);

        await using IContainer container = EraETestModule.BuildContainerBuilder()
            .AddSingleton<IBlockTree>(blockTree)
            .AddSingleton<IReceiptStorage>(receiptStorage)
            .AddSingleton<IPoSSwitcher>(poSSwitcher)
            .AddSingleton<IEraEConfig>(new EraEConfig { MaxEraSize = 16, NetworkName = EraETestModule.TestNetwork })
            .Build();

        string tmpDirectory = container.ResolveTempDirPath();
        await container.Resolve<IEraExporter>().Export(tmpDirectory, 0UL, 0UL);

        string eraFile = Directory.GetFiles(tmpDirectory, $"*{EraPathUtils.FileExtension}").Single();
        AssertNoPreMergeEntries(eraFile);
        poSSwitcher.Received(1).IsPostMerge(genesis.Header);
    }

    [Test]
    public void Export_WhenLastBlockBodyNotAvailable_ThrowsInvalidOperationException()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        Block head = Build.A.Block.WithNumber(10).TestObject;
        blockTree.Head.Returns(head);
        blockTree.FindBlock(10, BlockTreeLookupOptions.DoNotCreateLevelIfMissing).ReturnsNull();

        using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(1)
            .AddSingleton<IBlockTree>(blockTree)
            .Build();

        string tmpDirectory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();

        Assert.That(() => sut.Export(tmpDirectory, 0UL, 10UL), Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public async Task Export_WithPartialRange_CreatesOnlyEpochsInRange()
    {
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(48)
            .AddSingleton<IEraEConfig>(new EraEConfig { MaxEraSize = 16, NetworkName = EraETestModule.TestNetwork })
            .Build();

        string tmpDirectory = container.ResolveTempDirPath();
        await container.Resolve<IEraExporter>().Export(tmpDirectory, 16UL, 31UL);

        string[] eraFiles = System.IO.Directory.GetFiles(tmpDirectory, $"*{EraPathUtils.FileExtension}");
        Assert.That(eraFiles.Length, Is.EqualTo(1), "only one epoch covers blocks 16-31");
    }

    private static async Task AssertReadsBack(IContainer container, string directory, ulong from, ulong to)
    {
        using IEraStore store = container.Resolve<IEraStoreFactory>().Create(directory, null);
        for (ulong number = from; number <= to; number++)
        {
            Block source = container.Resolve<IBlockTree>().FindBlock(number, BlockTreeLookupOptions.None)!;
            (Block? block, TxReceipt[]? receipts) = await store.FindBlockAndReceipts(number);
            Assert.That(block?.Hash, Is.EqualTo(source.Hash), $"block {number}");
            Assert.That(receipts?.Select(static r => r.GasUsedTotal),
                Is.EqualTo(container.Resolve<IReceiptStorage>().Get(source).Select(static r => r.GasUsedTotal)), $"receipts of block {number}");
        }
    }

    /// <summary>Makes a rewrite of these blocks fail, so an export that succeeds proves the epoch was reused.</summary>
    private static void DropReceipts(IContainer container, ulong from, ulong to)
    {
        for (ulong number = from; number <= to; number++)
            container.Resolve<IReceiptStorage>().RemoveReceipts(container.Resolve<IBlockTree>().FindBlock(number, BlockTreeLookupOptions.None)!);
    }

    private static void AssertNoPreMergeEntries(string eraFile)
    {
        List<ushort> types = [];
        foreach (EraFileFormatComplianceTests.EntryRecord entry in EraFileFormatComplianceTests.ReadAllEntries(eraFile))
        {
            types.Add(entry.Type);
        }

        Assert.That(types, Does.Not.Contain(EntryTypes.Proof), "post-merge epochs have no Proof entries");
        Assert.That(types, Does.Not.Contain(EntryTypes.TotalDifficulty), "post-merge epochs have no TotalDifficulty entries");
        Assert.That(types, Does.Not.Contain(EntryTypes.AccumulatorRoot), "post-merge epochs have no AccumulatorRoot entry");
    }
}
