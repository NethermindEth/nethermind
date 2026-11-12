// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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

/// <summary>The unsafe block's relationship to current canonical L1 history.</summary>
public enum UnsafeBlockPreparationResult
{
    Ready,
    ParentReorganized,
    SourceNotCanonical,
}

/// <summary>Prepares execution evidence before an unsafe block enters the engine.</summary>
public interface IUnsafeBlockPreparation
{
    /// <returns>Whether the block is ready, its parent lost L1 ancestry, or its source disagrees with canonical L1.</returns>
    ValueTask<UnsafeBlockPreparationResult> Prepare(BlockHeader parent, Block block, CancellationToken token);
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
    IDerivedHeaderRules headerRules,
    ILogManager logManager,
    IUnsafeBlockPreparation? preparation = null) : IUnsafeHeadSource
{
    /// <summary>
    /// The most candidate ancestors inspected before requiring a new source snapshot.
    /// </summary>
    public const int MaxDepth = 1024;

    internal const int MaxBlocksPerAdvance = 32;

    private readonly ILogger _logger = logManager.GetClassLogger<UnsafeHeadFollower>();
    private Hash256? _refused;
    private int _downloadLimit = MaxBlocksPerAdvance;

    /// <exception cref="L1SourceIncompleteException">The sequencer cannot be read; the next poll retries.</exception>
    /// <exception cref="EezEngineUnavailableException">The node has not taken the blocks yet; the next poll retries.</exception>
    public async Task Advance(FollowerHeads heads, CancellationToken token)
    {
        EezL1Block latest = await sequencer.GetLatestBlock(token) ?? throw new L1SourceIncompleteException(0, "The sequencer does not report its latest block.");
        if (latest.Hash is null) throw new L1SourceIncompleteException(latest.Number, "The sequencer returned a head without a hash.");
        if (latest.Hash == _refused || latest.Number < heads.Safe.Number) return;
        if (latest.Hash == heads.Head.Hash)
        {
            await SetExistingHead(heads.Head, heads, token);
            return;
        }

        BlockHeader? ancestor = await CommonAncestor(latest, heads, token);
        if (ancestor is null)
        {
            Refuse(latest, $"it conflicts with safe {heads.Safe.Number}");
            return;
        }
        if (ancestor.Number == latest.Number)
        {
            await SetExistingHead(ancestor, heads, token);
            return;
        }
        ulong targetNumber = ancestor.Number + Math.Min(latest.Number - ancestor.Number, (ulong)_downloadLimit);
        EezL1Block target = targetNumber == latest.Number ? latest : await ReadNumber(targetNumber, token);

        List<Block>? blocks;
        try
        {
            blocks = await Branch(target.Hash, heads.Safe, heads.Head.Number, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _downloadLimit = Math.Max(1, _downloadLimit / 2);
            throw;
        }
        if (blocks is null)
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

        Hash256? previousHead = heads.Head.Hash;
        try
        {
            BlockHeader parent = blockTree.FindHeader(blocks[^1].ParentHash!)!;
            for (int i = blocks.Count - 1; i >= 0; i--)
            {
                token.ThrowIfCancellationRequested();
                if (preparation is not null)
                {
                    UnsafeBlockPreparationResult prepared = await preparation.Prepare(parent, blocks[i], token);
                    if (prepared != UnsafeBlockPreparationResult.Ready)
                    {
                        if (prepared == UnsafeBlockPreparationResult.ParentReorganized || blockTree.IsMainChain(blocks[i].Header))
                            await heads.Reset(heads.Safe);
                        if (_logger.IsWarn) _logger.Warn($"Unsafe block {blocks[i].Hash} disagrees with canonical L1 history; waiting for the source.");
                        return;
                    }
                }
                await engine.Insert(blocks[i]);
                await heads.SetHead(blocks[i].Header);
                parent = blocks[i].Header;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (heads.Head.Hash == previousHead) _downloadLimit = Math.Max(1, _downloadLimit / 2);
            throw;
        }
        catch (Exception e) when (e is EezFollowerException or EezSettlementException)
        {
            Refuse(latest, e.Message);
            return;
        }

        if (blocks.Count >= _downloadLimit && _downloadLimit < MaxBlocksPerAdvance) _downloadLimit++;
        if (_logger.IsDebug) _logger.Debug($"L2 unsafe head {heads.Head.Number} ({heads.Head.Hash}) from the sequencer.");
    }

    private async Task SetExistingHead(BlockHeader header, FollowerHeads heads, CancellationToken token)
    {
        if (preparation is not null && header.Number > heads.Safe.Number)
        {
            Block current = blockTree.FindBlock(header.Hash!)
                ?? throw new L1SourceIncompleteException(header.Number, "The known unsafe block is unavailable.");
            BlockHeader parent = blockTree.FindHeader(current.ParentHash!)
                ?? throw new L1SourceIncompleteException(header.Number, "The known unsafe parent is unavailable.");
            if (await preparation.Prepare(parent, current, token) != UnsafeBlockPreparationResult.Ready)
            {
                if (_logger.IsWarn) _logger.Warn($"Known unsafe block {header.Hash} lost canonical L1 history; returning to safe.");
                await heads.Reset(heads.Safe);
                return;
            }
        }
        await heads.SetHead(header);
    }

    private async Task<BlockHeader?> CommonAncestor(EezL1Block latest, FollowerHeads heads, CancellationToken token)
    {
        ulong high = Math.Min(latest.Number, heads.Head.Number);
        EezL1Block candidate = high == latest.Number ? latest : await ReadNumber(high, token);
        if (blockTree.FindHeader(high) is { } known && known.Hash == candidate.Hash) return known;
        EezL1Block safe = high == heads.Safe.Number ? candidate : await ReadNumber(heads.Safe.Number, token);
        if (safe.Hash != heads.Safe.Hash) return null;

        ulong low = heads.Safe.Number;
        while (high - low > 1)
        {
            ulong middle = low + (high - low) / 2;
            candidate = await ReadNumber(middle, token);
            if (blockTree.FindHeader(middle)?.Hash == candidate.Hash) low = middle;
            else high = middle;
        }
        return blockTree.FindHeader(low);
    }

    private async Task<EezL1Block> ReadNumber(ulong number, CancellationToken token)
    {
        EezL1Block block = await sequencer.GetBlockByNumber(number, token)
            ?? throw new L1SourceIncompleteException(number, $"The sequencer does not serve block {number}.");
        if (block.Hash is null || block.Number != number) throw new L1SourceIncompleteException(number, "The sequencer returned another block height.");
        return block;
    }

    /// <summary>A sequencer block derivation would never rebuild is the sequencer's fault, not the follower's: it keeps following L1.</summary>
    private void Refuse(in EezL1Block latest, string reason)
    {
        _refused = latest.Hash;
        if (_logger.IsWarn) _logger.Warn($"Ignoring sequencer head {latest.Hash} ({latest.Number}): {reason}.");
    }

    /// <summary>
    /// The sequencer's blocks from <paramref name="head"/> down to the executed canonical prefix, newest first;
    /// <see langword="null"/> when that parent is below <paramref name="safe"/> or the source hash differs.
    /// Blocks the node already holds off the canonical chain, such as the ones a head reset left, are taken locally.
    /// </summary>
    /// <exception cref="L1SourceIncompleteException">The walk exceeds <see cref="MaxDepth"/> and needs a fresh source snapshot.</exception>
    private async Task<List<Block>?> Branch(Hash256 head, BlockHeader safe, ulong executedHeight, CancellationToken token)
    {
        List<Block> blocks = [];
        Hash256 next = head;
        while (blocks.Count < MaxDepth)
        {
            if (blockTree.FindHeader(next) is { } known && known.Number <= executedHeight && blockTree.IsMainChain(known))
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

        if (blockTree.FindHeader(next) is { } ancestor && ancestor.Number <= executedHeight && blockTree.IsMainChain(ancestor))
            return ancestor.Number >= safe.Number ? blocks : null;
        throw new L1SourceIncompleteException(safe.Number, "Unsafe ancestry exceeds the walk limit; retry with a fresh source snapshot.");
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
            if (headerRules.Mismatch(block, parent) is { } field)
            {
                return $"block {block.Number} has {field}";
            }

            parent = block.Header;
        }

        return null;
    }
}
