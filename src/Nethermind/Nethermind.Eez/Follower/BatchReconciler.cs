// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;

namespace Nethermind.Eez.Follower;

/// <summary>Brings the local chain in line with what L1 settled.</summary>
public interface IBatchReconciler
{
    /// <param name="settling">The L1 block that settled <paramref name="scanned"/>.</param>
    /// <param name="cursor">Where the previous settled batch ended.</param>
    /// <returns>Where L1's commitment now stands: the new cursor.</returns>
    /// <exception cref="EezFollowerException">The local chain cannot be brought in line with what L1 settled.</exception>
    Task<SettledHead> Reconcile(ScannedBatch scanned, L1Anchor settling, SettledHead cursor, FollowerHeads heads);
}

/// <summary>The L2 block a settlement ended at and the prevRandao it puts in effect for the blocks after it.</summary>
public readonly record struct SettledHead(BlockHeader Endpoint, Hash256 Seed);

/// <summary>
/// Brings the local chain in line with one settled batch: rebuilds the blocks it settles from its DA, keeps the local
/// blocks that already match, replays the first one that does not and everything after it, and checks that L1's stored
/// commitment is a block of the result. A batch whose first steps a competing batch in the same L1 block already made
/// resumes: its settled effects are appended to the Sync block that batch committed. Each derived block becomes the
/// head before the next is built, since a block's state is only readable once it is canonical.
/// </summary>
public sealed class BatchReconciler(
    IBlockTree blockTree,
    IStateReader stateReader,
    IDerivedBlockExecutor executor,
    IEezL2Engine engine,
    IBatchDerivation derivation,
    IDerivedHeaderRules headerRules,
    EezSettlementContext context,
    ILogManager logManager) : IBatchReconciler
{
    private readonly ILogger _logger = logManager.GetClassLogger<BatchReconciler>();

    public async Task<SettledHead> Reconcile(ScannedBatch scanned, L1Anchor settling, SettledHead cursor, FollowerHeads heads)
    {
        BlockHeader endpoint = await Settle(scanned, settling, cursor, heads);
        try
        {
            return new SettledHead(endpoint, headerRules.Seed(endpoint, settling, cursor.Seed));
        }
        catch (EezSettlementException e)
        {
            throw Diverged(endpoint.Number, e.Message);
        }
    }

    private async Task<BlockHeader> Settle(ScannedBatch scanned, L1Anchor settling, SettledHead cursor, FollowerHeads heads)
    {
        L1Settlement settlement = scanned.Settlement;
        DecodedBatch batch = Decode(scanned.Batch);
        ulong blockCount = batch.BlockCount;
        BlockHeader anchor = FindAnchor(cursor.Endpoint, blockCount, settlement.EntryState);
        if (anchor.Hash! == settlement.FinalState)
        {
            return anchor;
        }

        if (anchor.Hash! != settlement.EntryState)
        {
            throw Diverged(anchor.Number, $"its hash {anchor.Hash} is neither the batch's entry state {settlement.EntryState} nor its final state");
        }

        bool resumed = settlement.Start > 0;
        ulong from = resumed ? anchor.Number : anchor.Number + 1;
        BlockHeader parent = Canonical(from - 1);
        ulong candidateParent = resumed ? CandidateParentOfResumed(from, blockCount) : anchor.Number;
        DerivedBlock[] derived = Derive(batch, settlement, parent, new DerivationInputs(0, SeedAt(from, cursor), Canonical(candidateParent), settling, resumed));

        Dictionary<ulong, Hash256> replayed = [];
        using (IDerivedBlockSession session = executor.BeginSession())
        {
            if (resumed)
            {
                await Resume(session, from, parent, derived[^1], settlement, heads, replayed);
            }
            else
            {
                await Replay(session, from, parent, derived, settlement, heads, replayed);
            }
        }

        ulong to = resumed ? from : anchor.Number + blockCount;
        return SettledEnd(anchor.Number, to, settlement.FinalState, replayed);
    }

    private DecodedBatch Decode(L1Batch batch)
    {
        try
        {
            return derivation.Decode(batch.DaStream);
        }
        catch (EezSettlementException e)
        {
            throw new EezFollowerException($"Batch {batch.TransactionHash} settled rollup {context.RollupId} with DA that does not decode: {e.Message}", e);
        }
    }

    /// <summary>
    /// The block the batch builds on: the one whose hash is its entry state, at most one batch below the cursor, since
    /// a competing composer's batch may start below it. Otherwise the cursor itself.
    /// </summary>
    private BlockHeader FindAnchor(BlockHeader cursor, ulong blockCount, in ValueHash256 entryState)
    {
        BlockHeader candidate = cursor;
        for (ulong walked = 0; walked <= blockCount; walked++)
        {
            if (candidate.Hash! == entryState)
            {
                return candidate;
            }

            if (candidate.Number == 0)
            {
                break;
            }

            candidate = Canonical(candidate.Number - 1);
        }

        return cursor;
    }

    /// <summary>
    /// The prevRandao of a block derived at <paramref name="number"/>: the cursor's seed above it, and at or below it the
    /// seed the block there was accepted with, since a settled block carries the seed of the last endpoint before it.
    /// </summary>
    private Hash256 SeedAt(ulong number, SettledHead cursor) =>
        number > cursor.Endpoint.Number ? cursor.Seed : Canonical(number).MixHash ?? throw Diverged(number, "it carries no prevRandao");

    /// <summary>A resumed batch appends to its Sync block, but its whole range still started a block count below it.</summary>
    private ulong CandidateParentOfResumed(ulong syncBlock, ulong blockCount) =>
        syncBlock >= blockCount ? syncBlock - blockCount : throw Diverged(syncBlock, $"a resumed batch of {blockCount} blocks would start before genesis");

    private DerivedBlock[] Derive(DecodedBatch batch, L1Settlement settlement, BlockHeader parent, DerivationInputs inputs)
    {
        ulong systemNonce = stateReader.TryGetAccount(parent, EezConstants.SystemAddress, out AccountStruct account) ? (ulong)account.Nonce : 0;
        try
        {
            return batch.Derive(settlement.Effects, inputs with { SystemNonce = systemNonce });
        }
        catch (EezSettlementException e)
        {
            throw Diverged(parent.Number + 1, $"its batch's DA does not derive: {e.Message}");
        }
    }

    /// <summary>Keeps the leading local blocks that match the derived ones and replays from the first that does not.</summary>
    private async Task Replay(IDerivedBlockSession session, ulong from, BlockHeader parent, DerivedBlock[] derived, L1Settlement settlement,
        FollowerHeads heads, Dictionary<ulong, Hash256> replayed)
    {
        Block? first = blockTree.FindBlock(from, BlockTreeLookupOptions.RequireCanonical);
        bool staleBoundary = first is null || first.ParentHash != parent.Hash;
        Block? last = blockTree.FindBlock(from + (ulong)derived.Length - 1, BlockTreeLookupOptions.RequireCanonical);
        if (!staleBoundary && last?.Hash == settlement.FinalState)
        {
            return;
        }

        bool replaying = staleBoundary;
        for (int i = 0; i < derived.Length; i++)
        {
            ulong number = from + (ulong)i;
            Block? local = replaying ? null : blockTree.FindBlock(number, BlockTreeLookupOptions.RequireCanonical);
            if (local is not null && Matches(local, parent, derived[i]))
            {
                parent = local.Header;
                continue;
            }

            if (!replaying)
            {
                await RetreatSafeBelow(number, parent, heads);
            }

            replaying = true;
            parent = await Commit(session, parent, derived[i], heads, replayed);
        }
    }

    /// <summary>Rewrites the Sync block a competing same-block batch committed, appending this batch's settled effects.</summary>
    private async Task Resume(IDerivedBlockSession session, ulong height, BlockHeader parent, DerivedBlock content, L1Settlement settlement,
        FollowerHeads heads, Dictionary<ulong, Hash256> replayed)
    {
        Block existing = blockTree.FindBlock(height, BlockTreeLookupOptions.RequireCanonical) is { } block && block.ParentHash == parent.Hash
            ? block
            : throw Diverged(height, "the Sync block a resumed batch appends to is missing or reorganized");
        if (existing.Hash! == settlement.FinalState)
        {
            return;
        }

        await RetreatSafeBelow(height, parent, heads);
        byte[][] local = Encode(existing);
        int kept = local.Length >= content.Transactions.Length && EndsWith(local, content.Transactions)
            ? local.Length - content.Transactions.Length
            : local.Length;
        byte[][] transactions = new byte[kept + content.Transactions.Length][];
        Array.Copy(local, transactions, kept);
        content.Transactions.CopyTo(transactions, kept);
        if (_logger.IsInfo) _logger.Info($"Rewriting Sync block {height} with the {content.Transactions.Length} transactions a resumed batch settled.");
        await Commit(session, parent, content with { Transactions = transactions }, heads, replayed);
    }

    /// <summary>
    /// Moves safe to <paramref name="parent"/> before a block at <paramref name="height"/> is replaced, since safe must
    /// stay an ancestor of the head. Finalized never moves back, so a replacement at or below it is a divergence.
    /// </summary>
    private async Task RetreatSafeBelow(ulong height, BlockHeader parent, FollowerHeads heads)
    {
        if (heads.Safe.Number < height)
        {
            return;
        }

        if (heads.Finalized.Number >= height)
        {
            throw Diverged(height, $"replacing it would rewrite the chain at or below the finalized block {heads.Finalized.Number}");
        }

        await heads.RetreatSafe(parent);
        if (_logger.IsWarn) _logger.Warn($"Safe L2 head retreats to {parent.ToString(BlockHeader.Format.Short)} to replace block {height}.");
    }

    /// <summary>Executes and inserts one derived block and makes it the head, so the next block can be built on its state.</summary>
    private async Task<BlockHeader> Commit(IDerivedBlockSession session, BlockHeader parent, DerivedBlock derived, FollowerHeads heads,
        Dictionary<ulong, Hash256> replayed)
    {
        Block block;
        try
        {
            block = session.Execute(parent, derived);
        }
        catch (EezSettlementException e)
        {
            throw Diverged(parent.Number + 1, e.Message);
        }

        await engine.Insert(block);
        await heads.SetHead(block.Header);
        replayed[block.Number] = block.Hash!;
        if (_logger.IsDebug) _logger.Debug($"Derived L2 block {block.ToString(Block.Format.Short)}.");
        return block.Header;
    }

    /// <summary>
    /// The block L1's commitment names: searched from the top of the batch's range, since a partial settlement ends
    /// below it and leaves the blocks above as head only.
    /// </summary>
    private BlockHeader SettledEnd(ulong anchor, ulong to, in ValueHash256 finalState, Dictionary<ulong, Hash256> replayed)
    {
        for (ulong number = to; number >= anchor && number <= to; number--)
        {
            BlockHeader header = replayed.TryGetValue(number, out Hash256? hash)
                ? blockTree.FindHeader(hash, BlockTreeLookupOptions.None, number) ?? Canonical(number)
                : Canonical(number);
            if (header.Hash! == finalState)
            {
                return header;
            }

            if (number == 0)
            {
                break;
            }
        }

        throw Diverged(to, $"no block from {anchor} to {to} has the hash {finalState} L1 settled");
    }

    private BlockHeader Canonical(ulong number) =>
        blockTree.FindHeader(number, BlockTreeLookupOptions.RequireCanonical) ?? throw Diverged(number, "it is missing from the local chain");

    /// <summary>Whether <paramref name="local"/> is the block derivation builds, so a sequencer block with any other header is replaced.</summary>
    private bool Matches(Block local, BlockHeader parent, DerivedBlock derived)
    {
        if (local.Beneficiary != derived.Beneficiary || !local.ExtraData.AsSpan().SequenceEqual(derived.ExtraData) || local.MixHash != derived.PrevRandao
            || local.Transactions.Length != derived.Transactions.Length
            || headerRules.Mismatch(local, parent) is not null)
        {
            return false;
        }

        byte[][] encoded = Encode(local);
        for (int i = 0; i < encoded.Length; i++)
        {
            if (!encoded[i].AsSpan().SequenceEqual(derived.Transactions[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static byte[][] Encode(Block block)
    {
        byte[][] encoded = new byte[block.Transactions.Length][];
        for (int i = 0; i < encoded.Length; i++)
        {
            encoded[i] = TxDecoder.Instance.Encode(block.Transactions[i], RlpBehaviors.SkipTypedWrapping).Bytes;
        }

        return encoded;
    }

    private static bool EndsWith(byte[][] transactions, byte[][] suffix)
    {
        int offset = transactions.Length - suffix.Length;
        for (int i = 0; i < suffix.Length; i++)
        {
            if (!transactions[offset + i].AsSpan().SequenceEqual(suffix[i]))
            {
                return false;
            }
        }

        return true;
    }

    private EezFollowerException Diverged(ulong number, string reason) =>
        new($"Local L2 block {number} diverges from what L1 settled for rollup {context.RollupId}: {reason}.");
}
