// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Eez.Follower;

/// <summary>Where the follower takes the unsafe head from, ahead of what L1 settled.</summary>
public interface IUnsafeHeadSource
{
    /// <summary>Moves the head to the latest blocks the source offers that build on <see cref="FollowerHeads.Safe"/>.</summary>
    Task Advance(FollowerHeads heads, CancellationToken token);
}

/// <summary>No unsafe head: the head is the last derived block.</summary>
public sealed class NullUnsafeHeadSource : IUnsafeHeadSource
{
    public static readonly NullUnsafeHeadSource Instance = new();

    private NullUnsafeHeadSource()
    {
    }

    public Task Advance(FollowerHeads heads, CancellationToken token) => Task.CompletedTask;
}

/// <summary>
/// Takes the sequencer's latest blocks as the unsafe head, before L1 settles them. The sequencer is trusted for
/// nothing beyond that: its blocks must have the header derivation builds, they are executed and validated like any
/// other, they must build on the local chain at or above the safe head, and it never moves safe or finalized. When L1
/// later settles something else, derivation replaces them.
/// </summary>
public sealed class UnsafeHeadFollower(
    IEezSequencerApi sequencer,
    IBlockTree blockTree,
    IEezL2Engine engine,
    ISpecProvider specProvider,
    EezSettlementContext context,
    ILogManager logManager) : IUnsafeHeadSource
{
    /// <summary>
    /// How far the sequencer may be ahead of the local head for its blocks to be fetched; further behind, the follower
    /// catches up from L1 first.
    /// </summary>
    public const int MaxDepth = 1024;

    private readonly ILogger _logger = logManager.GetClassLogger<UnsafeHeadFollower>();
    private Hash256? _refused;

    /// <exception cref="L1SourceIncompleteException">The sequencer cannot be read; the next poll retries.</exception>
    /// <exception cref="EezEngineUnavailableException">The node has not taken the blocks yet; the next poll retries.</exception>
    public async Task Advance(FollowerHeads heads, CancellationToken token)
    {
        EezL1Block latest = await sequencer.GetLatestBlock(token) ?? throw new L1SourceIncompleteException(0, "The sequencer does not report its latest block.");
        if (latest.Hash == _refused || latest.Hash == heads.Head.Hash || latest.Number <= heads.Safe.Number)
        {
            return;
        }

        if (latest.Number > heads.Head.Number + MaxDepth)
        {
            if (_logger.IsDebug) _logger.Debug($"Sequencer head {latest.Number} is more than {MaxDepth} blocks ahead; following L1 until it is closer.");
            return;
        }

        if (await Branch(latest.Hash, heads.Safe, token) is not { } blocks)
        {
            Refuse(latest, $"it does not build on the local chain at or above safe {heads.Safe.Number}");
            return;
        }

        if (blocks.Count == 0)
        {
            return;
        }

        if (Mismatch(blocks) is { } mismatch)
        {
            Refuse(latest, mismatch);
            return;
        }

        try
        {
            for (int i = blocks.Count - 1; i >= 0; i--)
            {
                await engine.Insert(blocks[i]);
            }

            await heads.SetHead(blocks[0].Header);
        }
        catch (EezFollowerException e)
        {
            Refuse(latest, e.Message);
            return;
        }

        if (_logger.IsDebug) _logger.Debug($"L2 unsafe head {latest.Number} ({latest.Hash}) from the sequencer.");
    }

    /// <summary>A sequencer block derivation would never rebuild is the sequencer's fault, not the follower's: it keeps following L1.</summary>
    private void Refuse(in EezL1Block latest, string reason)
    {
        _refused = latest.Hash;
        if (_logger.IsWarn) _logger.Warn($"Ignoring sequencer head {latest.Hash} ({latest.Number}): {reason}.");
    }

    /// <summary>
    /// The sequencer's blocks from <paramref name="head"/> down to the first one whose parent is canonical, newest first;
    /// <see langword="null"/> when that parent is below <paramref name="safe"/> or the walk runs past <see cref="MaxDepth"/>.
    /// Blocks the node already holds off the canonical chain, such as the ones a head reset left, are taken locally.
    /// </summary>
    private async Task<List<Block>?> Branch(Hash256 head, BlockHeader safe, CancellationToken token)
    {
        List<Block> blocks = [];
        Hash256 next = head;
        while (blocks.Count < MaxDepth)
        {
            if (blockTree.FindHeader(next) is { } known && blockTree.IsMainChain(known))
            {
                return known.Number >= safe.Number ? blocks : null;
            }

            Block block = blockTree.FindBlock(next) ?? await Fetch(next, token);
            if (block.Hash != next)
            {
                return null;
            }

            blocks.Add(block);
            next = block.ParentHash!;
        }

        return null;
    }

    private async Task<Block> Fetch(Hash256 hash, CancellationToken token)
    {
        byte[] rlp = await sequencer.GetRawBlock(hash, token) ?? throw new L1SourceIncompleteException(0, $"The sequencer does not serve block {hash}.");
        return Rlp.Decode<Block>(rlp) ?? throw new L1SourceIncompleteException(0, $"The sequencer served no block for {hash}.");
    }

    /// <summary>The first header derivation would not build, from the oldest block up.</summary>
    private string? Mismatch(List<Block> blocks)
    {
        BlockHeader parent = blockTree.FindHeader(blocks[^1].ParentHash!)!;
        for (int i = blocks.Count - 1; i >= 0; i--)
        {
            Block block = blocks[i];
            if (DerivedHeader.Mismatch(block, parent, specProvider.GetSpec(block.Header), context) is { } field)
            {
                return $"block {block.Number} has {field}";
            }

            parent = block.Header;
        }

        return null;
    }
}
