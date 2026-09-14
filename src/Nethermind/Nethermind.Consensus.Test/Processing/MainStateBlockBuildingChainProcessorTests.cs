// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Processing;

[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class MainStateBlockBuildingChainProcessorTests : ChainProcessorTestsBase
{
    private MainStateBlockBuildingChainProcessor _processor = null!;

    [SetUp]
    public void Setup() => _processor = new MainStateBlockBuildingChainProcessor(
        BlockTree,
        BranchProcessor,
        [PreprocessorStep],
        StateReader,
        LimboLogs.Instance);

    [TestCase(ProcessingOptions.ProducingBlock, true, 2_000_000)]
    [TestCase(ProcessingOptions.ReadOnlyChain, true, 2_000_000)]
    [TestCase(ProcessingOptions.None, false, 1)]
    public void Process_follows_head_and_option_semantics(ProcessingOptions options, bool expectProcessed, long totalDifficulty)
    {
        Block block = BuildBlockOnHead(totalDifficulty);
        BranchProcessor.Allow(block);
        Suggest(block);

        Block? processed = _processor.Process(block, options, NullBlockTracer.Instance);

        Assert.That(processed, expectProcessed ? Is.Not.Null : Is.Null);
        Assert.That(BlockTree.Head!.Hash, Is.Not.EqualTo(block.Hash));
        Assert.That(BranchProcessor.ProcessedBranches, expectProcessed ? Is.Not.Empty : Is.Empty);
    }

    // The second case is what BlockProducerBase uses with BuildBlocksOnMainState.
    [TestCase(ProcessingOptions.ReadOnlyChain)]
    [TestCase(ProcessingOptions.NoValidation | ProcessingOptions.StoreReceipts | ProcessingOptions.DoNotUpdateHead)]
    public void Invalid_block_is_refused_and_kept_in_block_tree(ProcessingOptions options)
    {
        Block block = BuildBlockOnHead();
        BranchProcessor.AllowToFail(block);
        Suggest(block);

        Block? processed = _processor.Process(block, options, NullBlockTracer.Instance);

        Assert.That(processed, Is.Null);
        Assert.That(BlockTree.FindBlock(block.Hash!, BlockTreeLookupOptions.None), Is.Not.Null);
    }

    [Test]
    public void Preprocessor_steps_run_before_processing()
    {
        Block block = BuildBlockOnHead();
        BranchProcessor.Allow(block);
        Suggest(block);

        _processor.Process(block, ProcessingOptions.ProducingBlock, NullBlockTracer.Instance);

        Assert.That(PreprocessorStep.Recovered.Select(b => b.Hash), Does.Contain(block.Hash));
    }
}
