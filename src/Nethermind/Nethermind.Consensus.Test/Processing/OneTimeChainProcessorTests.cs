// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Processing;

[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class OneTimeChainProcessorTests
{
    private class BranchProcessorMock : IBranchProcessor
    {
        private readonly ConcurrentDictionary<Hash256, bool> _allowed = [];
        private readonly ConcurrentDictionary<Hash256, bool> _allowedToFail = [];

        public List<Block[]> ProcessedBranches { get; } = [];

        public Block[] Process(BlockHeader? baseBlock, IReadOnlyList<Block> suggestedBlocks, ProcessingOptions processingOptions, IBlockTracer blockTracer, CancellationToken token)
        {
            foreach (Block block in suggestedBlocks)
            {
                if (!_allowed.ContainsKey(block.Hash!))
                {
                    if (_allowedToFail.TryRemove(block.Hash!, out _))
                    {
                        throw new InvalidBlockException(block, "allowed to fail");
                    }

                    throw new InvalidOperationException($"Block {block.Hash} was not allowed to process");
                }
            }

            ProcessedBranches.Add(suggestedBlocks.ToArray());
            return suggestedBlocks.ToArray();
        }

        public void Allow(Block block) => _allowed[block.Hash!] = true;
        public void AllowToFail(Block block) => _allowedToFail[block.Hash!] = true;

#pragma warning disable CS0067
        public event EventHandler<BlockProcessedEventArgs>? BlockProcessed;
        public event EventHandler<BlocksProcessingEventArgs>? BlocksProcessing;
        public event EventHandler<BranchProcessingCompletedEventArgs>? BranchProcessingCompleted;
        public event EventHandler<BlockEventArgs>? BlockProcessing;
#pragma warning restore CS0067
    }

    private class PreprocessorStepMock : IBlockPreprocessorStep
    {
        public List<Block> Recovered { get; } = [];
        public void RecoverData(Block block) => Recovered.Add(block);
    }

    private BlockTree _blockTree = null!;
    private Block _genesis = null!;
    private IStateReader _stateReader = null!;
    private BranchProcessorMock _branchProcessor = null!;
    private PreprocessorStepMock _preprocessorStep = null!;
    private OneTimeChainProcessor _processor = null!;

    [SetUp]
    public void Setup()
    {
        _blockTree = Build.A.BlockTree().WithoutSettingHead.TestObject;
        _genesis = Build.A.Block.Genesis.TestObject;
        _genesis.Header.TotalDifficulty = _genesis.Header.Difficulty;
        _blockTree.SuggestBlock(_genesis);
        _blockTree.TryUpdateMainChain(_genesis.Header, wereProcessed: true, preloadedBlocks: [_genesis]);

        _stateReader = Substitute.For<IStateReader>();
        _stateReader.HasStateForBlock(Arg.Any<BlockHeader?>()).Returns(true);

        _branchProcessor = new BranchProcessorMock();
        _preprocessorStep = new PreprocessorStepMock();
        _processor = new OneTimeChainProcessor(
            _blockTree,
            _branchProcessor,
            [_preprocessorStep],
            _stateReader,
            LimboLogs.Instance);
    }

    private Block BuildBlockOnHead(long totalDifficulty = 2_000_000)
    {
        BlockHeader head = _blockTree.Head!.Header;
        Block block = Build.A.Block
            .WithParent(head)
            .WithStateRoot(Keccak.EmptyTreeHash)
            .WithTotalDifficulty(totalDifficulty)
            .TestObject;
        block.Header.Hash = block.Header.CalculateHash();
        return block;
    }

    private void Suggest(Block block) =>
        Assert.That(_blockTree.SuggestBlock(block, BlockTreeSuggestOptions.None), Is.EqualTo(AddBlockResult.Added));

    [TestCase(ProcessingOptions.ProducingBlock, true, 2_000_000)]
    [TestCase(ProcessingOptions.ReadOnlyChain, true, 2_000_000)]
    [TestCase(ProcessingOptions.None, false, 1)]
    public void Process_follows_head_and_option_semantics(ProcessingOptions options, bool expectProcessed, long totalDifficulty)
    {
        Block block = BuildBlockOnHead(totalDifficulty);
        _branchProcessor.Allow(block);
        Suggest(block);

        Block? processed = _processor.Process(block, options, NullBlockTracer.Instance);

        Assert.That(processed, expectProcessed ? Is.Not.Null : Is.Null);
        Assert.That(_blockTree.Head!.Hash, Is.Not.EqualTo(block.Hash));
        Assert.That(_branchProcessor.ProcessedBranches, expectProcessed ? Is.Not.Empty : Is.Empty);
    }

    // The second case is what BlockProducerBase uses with BuildBlocksOnMainState.
    [TestCase(ProcessingOptions.ReadOnlyChain)]
    [TestCase(ProcessingOptions.NoValidation | ProcessingOptions.StoreReceipts | ProcessingOptions.DoNotUpdateHead)]
    public void Invalid_block_is_refused_and_kept_in_block_tree(ProcessingOptions options)
    {
        Block block = BuildBlockOnHead();
        _branchProcessor.AllowToFail(block);
        Suggest(block);

        Block? processed = _processor.Process(block, options, NullBlockTracer.Instance);

        Assert.That(processed, Is.Null);
        Assert.That(_blockTree.FindBlock(block.Hash!, BlockTreeLookupOptions.None), Is.Not.Null);
    }

    [Test]
    public void Preprocessor_steps_run_before_processing()
    {
        Block block = BuildBlockOnHead();
        _branchProcessor.Allow(block);
        Suggest(block);

        _processor.Process(block, ProcessingOptions.ProducingBlock, NullBlockTracer.Instance);

        Assert.That(_preprocessorStep.Recovered.Select(b => b.Hash), Does.Contain(block.Hash));
    }
}
