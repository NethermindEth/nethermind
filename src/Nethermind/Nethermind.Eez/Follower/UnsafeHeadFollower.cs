// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Eez.Follower;

/// <summary>Where the follower takes the unsafe head from, ahead of what L1 settled.</summary>
public interface IUnsafeHeadSource
{
    /// <summary>Moves the head to the latest blocks the source offers that build on <see cref="FollowerHeads.Safe"/>.</summary>
    Task Advance(FollowerHeads heads);
}

/// <summary>No unsafe head: the head is the last derived block.</summary>
public sealed class NullUnsafeHeadSource : IUnsafeHeadSource
{
    public static readonly NullUnsafeHeadSource Instance = new();

    private NullUnsafeHeadSource()
    {
    }

    public Task Advance(FollowerHeads heads) => Task.CompletedTask;
}

/// <summary>
/// Takes the sequencer's latest blocks as the unsafe head, before L1 settles them. The sequencer is trusted for
/// nothing beyond that: its blocks are executed and validated like any other, they must build on the local chain at
/// or above the safe head, and it never moves safe or finalized. When L1 later settles something else, derivation
/// replaces them.
/// </summary>
public sealed class UnsafeHeadFollower(IEezSequencerApi sequencer, IBlockTree blockTree, IEezL2Engine engine, ILogManager logManager) : IUnsafeHeadSource
{
    /// <summary>How far back the sequencer's chain is walked to find where it joins the local one.</summary>
    public const int MaxDepth = 1024;

    private readonly ILogger _logger = logManager.GetClassLogger<UnsafeHeadFollower>();
    private Hash256? _lastSeen;

    /// <exception cref="L1SourceIncompleteException">The sequencer cannot be read; the next poll retries.</exception>
    public async Task Advance(FollowerHeads heads)
    {
        EezL1Block latest = await sequencer.GetLatestBlock() ?? throw new L1SourceIncompleteException(0, "The sequencer does not report its latest block.");
        if (latest.Hash == _lastSeen || latest.Number <= heads.Safe.Number)
        {
            return;
        }

        List<Block>? blocks = await Branch(latest.Hash, heads.Safe);
        _lastSeen = latest.Hash;
        if (blocks is null)
        {
            if (_logger.IsWarn) _logger.Warn($"Ignoring sequencer head {latest.Hash} ({latest.Number}): it does not build on the local chain at or above safe {heads.Safe.Number}.");
            return;
        }

        try
        {
            for (int i = blocks.Count - 1; i >= 0; i--)
            {
                await engine.Insert(blocks[i]);
            }

            await engine.UpdateForkchoice(latest.Hash, heads.Safe.Hash!, heads.Finalized.Hash!);
        }
        catch (EezFollowerException e)
        {
            // An invalid sequencer block is the sequencer's fault, not the follower's: keep following L1.
            if (_logger.IsWarn) _logger.Warn($"Ignoring sequencer head {latest.Hash} ({latest.Number}): {e.Message}");
            return;
        }

        if (_logger.IsDebug) _logger.Debug($"L2 unsafe head {latest.Number} ({latest.Hash}) from the sequencer.");
    }

    /// <summary>
    /// The sequencer's blocks from <paramref name="head"/> down to the first one whose parent the local chain holds,
    /// newest first; <see langword="null"/> when that parent is not a canonical block at or above <paramref name="safe"/>.
    /// </summary>
    private async Task<List<Block>?> Branch(Hash256 head, BlockHeader safe)
    {
        List<Block> blocks = [];
        Hash256 next = head;
        while (blocks.Count < MaxDepth)
        {
            if (blockTree.FindHeader(next) is { } known)
            {
                return known.Number >= safe.Number && blockTree.IsMainChain(known) ? blocks : null;
            }

            byte[] rlp = await sequencer.GetRawBlock(next) ?? throw new L1SourceIncompleteException(0, $"The sequencer does not serve block {next}.");
            Block block = Rlp.Decode<Block>(rlp) ?? throw new L1SourceIncompleteException(0, $"The sequencer served no block for {next}.");
            if (block.Hash != next)
            {
                return null;
            }

            blocks.Add(block);
            next = block.ParentHash!;
        }

        return null;
    }
}
