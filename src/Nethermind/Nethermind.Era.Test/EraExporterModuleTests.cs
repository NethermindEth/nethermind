// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Db.Blooms;
using Nethermind.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Era1.Test;
public class EraExporterModuleTests
{
    private string? _destinationPath;

    [OneTimeSetUp]
    public void Setup()
    {
        _destinationPath = "testfolder_" + DateTime.UtcNow.Ticks;

    }
    [OneTimeTearDown]
    public void Cleanup()
    {
        if (Directory.Exists(_destinationPath))
        {
            Directory.Delete(_destinationPath, true);
        }
    }

    [Test]
    public async Task ExportAChainAndVerifyAccumulators()
    {
        IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();

        const int ChainLength = 512;

        BlockTree blockTree = Build.A.BlockTree().OfChainLength(ChainLength).TestObject;

        const string NetworkName = "test";
        EraExporter sut = new(new FileSystem(), blockTree, receiptStorage, specProvider, NetworkName);

        await sut.Export(_destinationPath!, 0, ChainLength - 1, 16);

        var accumulators = new List<byte[]>();
        var eraFiles = EraReader.GetAllEraFiles(_destinationPath!, NetworkName).ToArray();
        foreach (var file in eraFiles)
        {
            using var reader = await EraReader.Create(file);
            accumulators.Add(await reader.ReadAccumulator());
        }

        Assert.DoesNotThrowAsync(()=> sut.VerifyEraFiles(eraFiles, accumulators.ToArray()));
    }

}
