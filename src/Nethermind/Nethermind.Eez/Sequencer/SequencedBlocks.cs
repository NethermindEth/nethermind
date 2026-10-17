// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Follower;
using Nethermind.Eez.Proving;
using Nethermind.Logging;

namespace Nethermind.Eez.Sequencer;

/// <summary>Produces the sequencer's blocks and commits them as the head.</summary>
public interface ISequencedBlocks
{
    /// <summary>A block of pool transactions on <paramref name="parent"/>.</summary>
    /// <exception cref="EezSequencerException">The node cannot produce a block on <paramref name="parent"/>.</exception>
    Task<BlockHeader> Live(BlockHeader parent, FollowerHeads heads, CancellationToken token);

    /// <summary>An empty block on <paramref name="parent"/>, as a Sync block with nothing cross-chain in it is.</summary>
    Task<BlockHeader> Empty(BlockHeader parent, FollowerHeads heads);
}

/// <summary>Commits each block into the node through its engine, as the head, with its witness recorded.</summary>
public sealed class SequencedBlocks(
    ILiveBlockProducer live,
    IDerivedBlockExecutor derived,
    IEezL2Engine engine,
    IWitnessRecorder witnesses,
    Address beneficiary,
    ILogManager logManager) : ISequencedBlocks
{
    private readonly ILogger _logger = logManager.GetClassLogger<SequencedBlocks>();

    public async Task<BlockHeader> Live(BlockHeader parent, FollowerHeads heads, CancellationToken token)
    {
        Block block = live.Produce(parent, token) ?? throw new EezSequencerException($"The node did not produce a block on {parent.ToString(BlockHeader.Format.Short)}.");
        await Commit(parent, block, heads);
        return block.Header;
    }

    public async Task<BlockHeader> Empty(BlockHeader parent, FollowerHeads heads)
    {
        Block block;
        using (IDerivedBlockSession session = derived.BeginSession())
        {
            block = session.Execute(parent, new DerivedBlock(beneficiary, [], []));
        }

        await Commit(parent, block, heads);
        return block.Header;
    }

    private async Task Commit(BlockHeader parent, Block block, FollowerHeads heads)
    {
        await engine.Insert(block);
        await heads.SetHead(block.Header);
        try
        {
            witnesses.Capture(parent, block);
        }
        catch (Exception e)
        {
            if (_logger.IsWarn) _logger.Warn($"The witness of block {block.ToString(Block.Format.Short)} was not recorded; it is generated when proven: {e.Message}");
        }

        if (_logger.IsDebug) _logger.Debug($"Sequenced L2 block {block.ToString(Block.Format.Short)} with {block.Transactions.Length} transactions.");
    }
}
