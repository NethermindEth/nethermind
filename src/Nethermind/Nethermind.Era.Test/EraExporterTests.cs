// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Specs;
using NSubstitute;
using NSubstitute.ReturnsExtensions;
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using FluentAssertions;
using Nethermind.Blockchain.Era1;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;

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
        EraExporter sut = new(fileSystem, blockTree, receiptStorage, specProvider, LimboLogs.Instance, "test");

        Assert.That(() => sut.Export("", 0, 0, size), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [TestCase(1, 1, 1)]
    [TestCase(3, 1, 3)]
    [TestCase(16, 16, 1)]
    [TestCase(32, 16, 2)]
    [TestCase(64 * 2 + 1, 64, 3)]
    public async Task Export_ChainHasDifferentLength_CorrectNumberOfFilesCreated(int chainlength, int size, int expectedNumberOfFiles)
    {
        var fileSystem = new MockFileSystem();
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(chainlength).TestObject;
        IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        EraExporter sut = new(fileSystem, blockTree, receiptStorage, specProvider, LimboLogs.Instance, "abc");

        await sut.Export("test", 0, chainlength - 1, size, createAccumulator: false);
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
        EraExporter sut = new(fileSystem, blockTree, receiptStorage, specProvider, LimboLogs.Instance, "abc");

        Assert.That(() => sut.Export("test", 0, to), Throws.TypeOf<EraException>());
    }

    [Test]
    public void Export_ReceiptsAreNull_ThrowEraException()
    {
        var fileSystem = new MockFileSystem();
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(10).TestObject;
        IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
        receiptStorage.Get(Arg.Any<Block>()).ReturnsNull();
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        EraExporter sut = new(fileSystem, blockTree, receiptStorage, specProvider, LimboLogs.Instance, "abc");

        Assert.That(() => sut.Export("test", 0, 1), Throws.TypeOf<EraException>());
    }

    [Test]
    [Explicit]
    public async Task Export_()
    {
        //var fileSystem = new MockFileSystem();
        //const int ChainLength = 8192;
        //BlockTree blockTree = Build.A.BlockTree().OfChainLength(ChainLength).TestObject;
        //IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
        /// ISpecProvider specProvider = Substitute.For<ISpecProvider>();

        //EraExporter sut = new(fileSystem, blockTree, receiptStorage, specProvider, NetworkName);

        //await sut.Export("test", 0, ChainLength - 1, 8192);
        /*
        const string NetworkName = "holesky";
        foreach (var item in EraReader.GetAllEraFiles(@"/home/amirul/sataworkspace/geth-holesky/era-export", NetworkName))
        {
            Console.Error.WriteLine($"{item}");
            using EraReader reader = await EraReader.Create(item);
            using E2StoreStream store = new E2StoreStream(File.OpenRead(item));
            store.Seek(0, SeekOrigin.Begin);
            var expectedAccumulator = await reader.ReadAccumulator();
            Assert.That(await reader.VerifyAccumulator(expectedAccumulator, HoleskySpecProvider.Instance), Is.True);
        }
        */

        var eraFiles = EraPathUtils.GetAllEraFiles("geth", "mainnet");

        Assert.That(eraFiles.Count(), Is.GreaterThan(0));

        var specProvider = new ChainSpecBasedSpecProvider(new ChainSpec
        {
            SealEngineType = SealEngineType.BeaconChain,
            Parameters = new ChainParameters()
        });

        foreach (var era in eraFiles.Take(1))
        {
            var readFromFile = new List<(Block b, TxReceipt[] r, UInt256 td)>();

            using E2StoreStream store = new E2StoreStream(File.OpenRead(era));
            while (store.Position < store.StreamLength)
            {
                long startingPosition = store.Position;
                var entry = await store.ReadEntry(null);
                store.Seek(store.Position + entry.Length, SeekOrigin.Begin);
                Console.Error.WriteLine($"{startingPosition} {entry.Type} {entry.Length}");
            }

            store.Seek(0, SeekOrigin.Begin);
            var meta = await store.GetMetadata(default);
            var offset = meta.BlockOffset(0);
            Console.Error.WriteLine($"The offset is {offset}");



            /*
            using var destination = new MemoryStream();
            using var builder = EraWriter.Create(destination, specProvider);
            await foreach ((Block b, TxReceipt[] r, UInt256 td) in eraEnumerator)
            {
                await builder.Add(b, r, td);
                readFromFile.Add((b, r, td));
            }
            await builder.Finalize();

            using EraReader exportedToImported = await EraReader.Create(destination);
            int i = 0;
            await foreach ((Block b, TxReceipt[] r, UInt256 td) in exportedToImported)
            {
                Assert.That(i, Is.LessThan(readFromFile.Count()), "Exceeded the block count read from the file.");
                b.Should().BeEquivalentTo(readFromFile[i].b);
                r.Should().BeEquivalentTo(readFromFile[i].r);
                Assert.That(td, Is.EqualTo(readFromFile[i].td));
                i++;
            }
            */
        }
    }
}
