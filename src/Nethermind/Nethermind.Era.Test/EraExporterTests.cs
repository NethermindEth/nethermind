// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using NSubstitute;
using NSubstitute.ReturnsExtensions;
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;

namespace Nethermind.Era1.Test;
public class EraExporterTests
{
    [TestCase(EraWriter.MaxEra1Size + 1)]
    [TestCase(0)]
    [TestCase(-1)]
    public void Export_SizeIsGreaterThan8192OrLessThan1_ThrowArgumentOutOfRange(int size)
    {
        IFileSystem fileSystem = Substitute.For<IFileSystem>();
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        EraExporter sut = new(fileSystem, blockTree, receiptStorage, specProvider, "test");

        Assert.That(()=> sut.Export("",0,0, size), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [TestCase(1, 1, 1)]
    [TestCase(3, 1, 3)]
    [TestCase(16, 16, 1)]
    [TestCase(32, 16, 2)]
    [TestCase(8192 * 2 + 1, 8192, 3)]
    public async Task Export_ChainHasDifferentLength_CorrectNumberOfFilesCreated(int chainlength, int size, int expectedNumberOfFiles)
    {
        var fileSystem = new MockFileSystem();
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(chainlength).TestObject;
        IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        EraExporter sut = new(fileSystem, blockTree, receiptStorage, specProvider, "abc");

        await sut.Export("test", 0, chainlength - 1,size);

        Assert.That(fileSystem.AllFiles.Count, Is.EqualTo(expectedNumberOfFiles));
    }

    [TestCase(1, 1)]
    [TestCase(2, 2)]
    [TestCase(2, 3)]
    [TestCase(99, 999)]
    public void Export_ExportBeyondAvailableBlocks_ThrowEraException(int chainLength, int to)
    {
        var fileSystem = new MockFileSystem();
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(chainLength).TestObject;
        IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        EraExporter sut = new(fileSystem, blockTree, receiptStorage, specProvider, "abc");

        Assert.That(()=> sut.Export("test", 0, to), Throws.TypeOf<EraException>());
    }

    [Test]
    public void Export_ReceiptsAreNull_ThrowEraException()
    {
        var fileSystem = new MockFileSystem();
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(10).TestObject;
        IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
        receiptStorage.Get(Arg.Any<Block>()).ReturnsNull();
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        EraExporter sut = new(fileSystem, blockTree, receiptStorage, specProvider, "abc");

        Assert.That(() => sut.Export("test", 0, 1), Throws.TypeOf<EraException>());
    }

    [Test]
    public void VerifyEraFiles_FilesAndAccumulatorsAreDifferentLength_ThrowArgumentException()
    {
        EraExporter sut = new(Substitute.For<IFileSystem>(), Substitute.For<IBlockTree>(), Substitute.For<IReceiptStorage>(), Substitute.For<ISpecProvider>(), "abc");

        Assert.That(() => sut.VerifyEraFiles(["abc", "abc"], [ [0x0] ]), Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public void VerifyEraFiles_AccumulatorsHaveInvalidSize_ThrowArgumentException()
    {
        EraExporter sut = new(Substitute.For<IFileSystem>(), Substitute.For<IBlockTree>(), Substitute.For<IReceiptStorage>(), Substitute.For<ISpecProvider>(), "abc");

        Assert.That(() => sut.VerifyEraFiles(["abc"], [[0x0]]), Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public async Task VerifyEraFiles_VerifyAccumulatorsWithExpected_DoesNotThrow()
    {
        const int ChainLength = 128;
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(ChainLength).TestObject;
        const string NetworkName = "test";
        var fileSystem = new MockFileSystem();
        EraExporter sut = new(fileSystem, blockTree, Substitute.For<IReceiptStorage>(), Substitute.For<ISpecProvider>(), NetworkName);
        string destinationPath = "abc";
        await sut.Export(destinationPath!, 0, ChainLength - 1, 16);
        var accumulators = new List<byte[]>();
        var eraFiles = fileSystem.AllFiles.ToArray();
        foreach (var file in eraFiles)
        {
            using var reader = await EraReader.Create(fileSystem.File.OpenRead(file));
            accumulators.Add(await reader.ReadAccumulator());
        }

        Assert.DoesNotThrowAsync(() => sut.VerifyEraFiles(eraFiles, accumulators.ToArray()));
    }

    [Test]
    public async Task VerifyEraFiles_VerifyAccumulatorsWithUnexpected_ThrowEraVerificationException()
    {
        const int ChainLength = 64;
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(ChainLength).TestObject;
        const string NetworkName = "test";
        var fileSystem = new MockFileSystem();
        EraExporter sut = new(fileSystem, blockTree, Substitute.For<IReceiptStorage>(), Substitute.For<ISpecProvider>(), NetworkName);
        var destinationPath = "abc";
        const int EpochSize = 16;
        await sut.Export(destinationPath!, 0, ChainLength - 1, EpochSize);
        var accumulators = Enumerable.Range(1, ChainLength / EpochSize).Select(i => new byte[32]);

        Assert.That(
            () => sut.VerifyEraFiles(fileSystem.AllFiles.ToArray(), accumulators.ToArray()),
            Throws.TypeOf<EraVerificationException>());
    }
}
