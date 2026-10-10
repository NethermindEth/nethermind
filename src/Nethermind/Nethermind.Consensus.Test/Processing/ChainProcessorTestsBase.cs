// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Processing;

/// <summary>
/// Shared fixture for the env-scoped <see cref="IBlockchainProcessor"/> implementations: a block tree with a
/// processed genesis, a state reader that has state for everything, and a branch processor mock that only
/// accepts explicitly allowed blocks.
/// </summary>
public abstract class ChainProcessorTestsBase
{
    protected class BranchProcessorMock : IBranchProcessor
    {
        private readonly ConcurrentDictionary<Hash256, bool> _allowed = [];
        private readonly ConcurrentDictionary<Hash256, bool> _allowedToFail = [];

        public List<Block[]> ProcessedBranches { get; } = [];
        public BlockHeader? LastBaseBlock { get; private set; }
        public ProcessingOptions? LastOptions { get; private set; }

        public Block[] Process(BlockHeader? baseBlock, IReadOnlyList<Block> suggestedBlocks, ProcessingOptions processingOptions, IBlockTracer blockTracer, CancellationToken token)
        {
            LastBaseBlock = baseBlock;
            LastOptions = processingOptions;
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
        public event EventHandler<BlockExecutedEventArgs>? BlockExecuted;

        public event EventHandler<BlockProcessedEventArgs>? BlockProcessed;
        public event EventHandler<BlocksProcessingEventArgs>? BlocksProcessing;
        public event EventHandler<BranchProcessingCompletedEventArgs>? BranchProcessingCompleted;
        public event EventHandler<BlockEventArgs>? BlockProcessing;
#pragma warning restore CS0067
    }

    protected class PreprocessorStepMock : IBlockPreprocessorStep
    {
        public List<Block> Recovered { get; } = [];
        public void RecoverData(Block block) => Recovered.Add(block);
    }

    protected BlockTree BlockTree { get; private set; } = null!;
    protected IStateReader StateReader { get; private set; } = null!;
    protected BranchProcessorMock BranchProcessor { get; private set; } = null!;
    protected PreprocessorStepMock PreprocessorStep { get; private set; } = null!;

    [SetUp]
    public void SetUpChain()
    {
        BlockTree = Build.A.BlockTree().WithoutSettingHead.TestObject;
        Block genesis = Build.A.Block.Genesis.TestObject;
        genesis.Header.TotalDifficulty = genesis.Header.Difficulty;
        BlockTree.SuggestBlock(genesis);
        BlockTree.TryUpdateMainChain(genesis.Header, wereProcessed: true, preloadedBlocks: [genesis]);

        StateReader = Substitute.For<IStateReader>();
        StateReader.HasStateForBlock(Arg.Any<BlockHeader?>()).Returns(true);

        BranchProcessor = new BranchProcessorMock();
        PreprocessorStep = new PreprocessorStepMock();
    }

    protected Block BuildBlockOnHead(long totalDifficulty = 2_000_000)
    {
        BlockHeader head = BlockTree.Head!.Header;
        Block block = Build.A.Block
            .WithParent(head)
            .WithStateRoot(Keccak.EmptyTreeHash)
            .WithTotalDifficulty(totalDifficulty)
            .TestObject;
        block.Header.Hash = block.Header.CalculateHash();
        return block;
    }

    protected void Suggest(Block block) =>
        Assert.That(BlockTree.SuggestBlock(block, BlockTreeSuggestOptions.None), Is.EqualTo(AddBlockResult.Added));
}
