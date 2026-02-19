// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Evm.Benchmark.GasBenchmarks;
using NUnit.Framework;

namespace Nethermind.Evm.Benchmark.Test;

[TestFixture]
public class BlockBenchmarkHelperTests
{
    [Test]
    public void CreateBenchmarkBlocksConfig_Disables_CachePrecompiles()
    {
        BlocksConfig config = BlockBenchmarkHelper.CreateBenchmarkBlocksConfig();

        Assert.That(config.CachePrecompilesOnBlockProcessing, Is.False);
    }

    [Test]
    public void CreateBenchmarkBlocksConfig_Returns_New_Instance_Each_Call()
    {
        BlocksConfig config1 = BlockBenchmarkHelper.CreateBenchmarkBlocksConfig();
        BlocksConfig config2 = BlockBenchmarkHelper.CreateBenchmarkBlocksConfig();

        Assert.That(config1, Is.Not.SameAs(config2));
    }

    [Test]
    public void GetImportProcessingOptions_StoreReceipts_When_Configured()
    {
        ReceiptConfig config = new() { StoreReceipts = true };

        ProcessingOptions options = BlockBenchmarkHelper.GetImportProcessingOptions(config);

        Assert.That(options.HasFlag(ProcessingOptions.StoreReceipts), Is.True);
    }

    [Test]
    public void GetImportProcessingOptions_None_When_NoReceipts()
    {
        ReceiptConfig config = new() { StoreReceipts = false };

        ProcessingOptions options = BlockBenchmarkHelper.GetImportProcessingOptions(config);

        Assert.That(options, Is.EqualTo(ProcessingOptions.None));
    }

    [Test]
    public void GetNewPayloadProcessingOptions_IncludesEthereumMerge()
    {
        ReceiptConfig config = new() { StoreReceipts = false };

        ProcessingOptions options = BlockBenchmarkHelper.GetNewPayloadProcessingOptions(config);

        Assert.That(options.HasFlag(ProcessingOptions.EthereumMerge), Is.True);
    }

    [Test]
    public void GetNewPayloadProcessingOptions_IncludesStoreReceipts_When_Configured()
    {
        ReceiptConfig config = new() { StoreReceipts = true };

        ProcessingOptions options = BlockBenchmarkHelper.GetNewPayloadProcessingOptions(config);

        Assert.That(options.HasFlag(ProcessingOptions.EthereumMerge), Is.True);
        Assert.That(options.HasFlag(ProcessingOptions.StoreReceipts), Is.True);
    }

    [Test]
    public void GetBlockBuildingProcessingOptions_ProducingBlock_When_NotOnMainState()
    {
        BlocksConfig config = new() { BuildBlocksOnMainState = false };

        ProcessingOptions options = BlockBenchmarkHelper.GetBlockBuildingProcessingOptions(config);

        Assert.That(options, Is.EqualTo(ProcessingOptions.ProducingBlock));
    }

    [Test]
    public void GetBlockBuildingProcessingOptions_NoValidation_StoreReceipts_DoNotUpdateHead_When_OnMainState()
    {
        BlocksConfig config = new() { BuildBlocksOnMainState = true };

        ProcessingOptions options = BlockBenchmarkHelper.GetBlockBuildingProcessingOptions(config);

        Assert.That(options.HasFlag(ProcessingOptions.NoValidation), Is.True);
        Assert.That(options.HasFlag(ProcessingOptions.StoreReceipts), Is.True);
        Assert.That(options.HasFlag(ProcessingOptions.DoNotUpdateHead), Is.True);
    }

    [Test]
    public void GetBlockBuildingBlockchainProcessorOptions_NoReceipts_When_NotOnMainState()
    {
        BlocksConfig config = new() { BuildBlocksOnMainState = false };

        Nethermind.Consensus.Processing.BlockchainProcessor.Options options =
            BlockBenchmarkHelper.GetBlockBuildingBlockchainProcessorOptions(config);

        Assert.That(options, Is.EqualTo(Nethermind.Consensus.Processing.BlockchainProcessor.Options.NoReceipts));
    }

    [Test]
    public void GetBlockBuildingBlockchainProcessorOptions_Default_When_OnMainState()
    {
        BlocksConfig config = new() { BuildBlocksOnMainState = true };

        Nethermind.Consensus.Processing.BlockchainProcessor.Options options =
            BlockBenchmarkHelper.GetBlockBuildingBlockchainProcessorOptions(config);

        Assert.That(options, Is.EqualTo(Nethermind.Consensus.Processing.BlockchainProcessor.Options.Default));
    }

    [Test]
    public void CreateReceiptStorage_Returns_InMemory_When_StoreReceipts()
    {
        ReceiptConfig config = new() { StoreReceipts = true };

        Nethermind.Blockchain.Receipts.IReceiptStorage storage = BlockBenchmarkHelper.CreateReceiptStorage(config);

        Assert.That(storage, Is.Not.Null);
        Assert.That(storage, Is.InstanceOf<Nethermind.Blockchain.Receipts.InMemoryReceiptStorage>());
    }

    [Test]
    public void CreateReceiptStorage_Returns_Null_When_NoReceipts()
    {
        ReceiptConfig config = new() { StoreReceipts = false };

        Nethermind.Blockchain.Receipts.IReceiptStorage storage = BlockBenchmarkHelper.CreateReceiptStorage(config);

        Assert.That(storage, Is.SameAs(Nethermind.Blockchain.Receipts.NullReceiptStorage.Instance));
    }
}
