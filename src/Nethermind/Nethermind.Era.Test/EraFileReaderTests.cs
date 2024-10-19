// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.MemoryMappedFiles;
using DotNetty.Buffers;
using Nethermind.Blockchain.Era1;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Era1.Test;

public class EraFileReaderTests
{

    [Test]
    [Explicit]
    public Task Export_()
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

        foreach (var eraFile in eraFiles.Take(1))
        {
            var readFromFile = new List<(Block b, TxReceipt[] r, UInt256 td)>();

            using EraFileReader store = new EraFileReader(eraFile);
            var meta = store.CreateMetadata();
            var offset = meta.BlockOffset(0);


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

        return Task.CompletedTask;
    }
}
