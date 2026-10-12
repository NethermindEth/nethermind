// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Processing;

[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class OneTimeChainProcessorTests : ChainProcessorTestsBase
{
    private OneTimeChainProcessor _processor = null!;

    [SetUp]
    public void Setup() => _processor = new OneTimeChainProcessor(
        BlockTree,
        BranchProcessor,
        [PreprocessorStep],
        StateReader,
        LimboLogs.Instance);

    // Every caller passes ForceProcessing (ProducingBlock, ReadOnlyReplay): the block alone is processed on its
    // parent, the head is left untouched, and preprocessor steps run first.
    [Test]
    public void Process_runs_single_block_on_parent([Values(ProcessingOptions.ProducingBlock, TraceProcessingOptions.ReadOnlyReplay)] ProcessingOptions options)
    {
        Block block = BuildBlockOnHead();
        BranchProcessor.Allow(block);
        Suggest(block);

        Block? processed = _processor.Process(block, options, NullBlockTracer.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(processed, Is.Not.Null);
            Assert.That(BranchProcessor.ProcessedBranches, Is.EqualTo(new[] { new[] { block } }));
            Assert.That(BranchProcessor.LastBaseBlock?.Hash, Is.EqualTo(block.ParentHash));
            Assert.That(BranchProcessor.LastOptions, Is.EqualTo(options));
            Assert.That(PreprocessorStep.Recovered.Select(b => b.Hash), Does.Contain(block.Hash));
            Assert.That(BlockTree.Head!.Hash, Is.Not.EqualTo(block.Hash));
        }
    }

    [Test]
    public void Invalid_block_is_refused_and_kept_in_tree()
    {
        Block block = BuildBlockOnHead();
        BranchProcessor.AllowToFail(block);
        Suggest(block);

        Block? processed = _processor.Process(block, ProcessingOptions.ProducingBlock, NullBlockTracer.Instance);

        Assert.That(processed, Is.Null);
        Assert.That(BlockTree.FindBlock(block.Hash!, BlockTreeLookupOptions.None), Is.Not.Null);
    }

    [Test]
    public void Unknown_parent_is_skipped()
    {
        Block block = Build.A.Block
            .WithNumber(1)
            .WithParentHash(TestItem.KeccakA)
            .WithStateRoot(Keccak.EmptyTreeHash)
            .WithTotalDifficulty(2_000_000L)
            .TestObject;
        BranchProcessor.Allow(block);

        Block? processed = _processor.Process(block, ProcessingOptions.ProducingBlock, NullBlockTracer.Instance);

        Assert.That(processed, Is.Null);
        Assert.That(BranchProcessor.ProcessedBranches, Is.Empty);
    }
}
