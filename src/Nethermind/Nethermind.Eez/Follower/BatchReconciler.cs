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

/// <summary>
/// Brings the local chain in line with one settled batch: rebuilds the blocks it settles from its DA, keeps the local
/// blocks that already match, replays the first one that does not and everything after it, and checks that L1's stored
/// commitment is a block of the result. A batch whose first steps a competing batch in the same L1 block already made
/// resumes: its settled effects are appended to the Sync block that batch committed.
/// </summary>
public sealed class BatchReconciler(
    IBlockTree blockTree,
    IStateReader stateReader,
    IDerivedBlockExecutor executor,
    IEezL2Engine engine,
    EezSettlementContext context,
    ILogManager logManager)
{
    private readonly ILogger _logger = logManager.GetClassLogger<BatchReconciler>();

    /// <param name="cursor">The last L2 block the previous settled batch ended at.</param>
    /// <returns>The L2 block L1's commitment now names: the new cursor.</returns>
    /// <exception cref="EezFollowerException">The local chain cannot be brought in line with what L1 settled.</exception>
    public async Task<BlockHeader> Reconcile(ScannedBatch scanned, BlockHeader cursor, FollowerHeads heads)
    {
        L1Settlement settlement = scanned.Settlement;
        DaPayload payload = Decode(scanned.Batch);
        ulong blockCount = (ulong)payload.Span.BlockCount;
        BlockHeader anchor = FindAnchor(cursor, blockCount, settlement.EntryState);
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
        DerivedBlock[] derived = Derive(payload, settlement, parent);

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

    private DaPayload Decode(L1Batch batch)
    {
        DaPayload payload;
        try
        {
            payload = DaPayloadCodec.Decode(batch.CallData);
        }
        catch (EezSettlementException e)
        {
            throw new EezFollowerException($"Batch {batch.TransactionHash} settled rollup {context.RollupId} with DA that does not decode: {e.Message}", e);
        }

        if (payload.RollupId != context.RollupId || payload.Span.BlockCount == 0)
        {
            throw new EezFollowerException($"Batch {batch.TransactionHash} settled rollup {context.RollupId} with DA for rollup {payload.RollupId} " +
                $"covering {payload.Span.BlockCount} blocks.");
        }

        return payload;
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

    private DerivedBlock[] Derive(DaPayload payload, L1Settlement settlement, BlockHeader parent)
    {
        ulong systemNonce = stateReader.TryGetAccount(parent, EezConstants.SystemAddress, out AccountStruct account) ? (ulong)account.Nonce : 0;
        try
        {
            return BatchDerivation.Derive(payload, settlement.Effects, systemNonce, context.ChainId, context.RollupId);
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
            if (local is not null && Matches(local, derived[i]))
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

        heads.Safe = parent;
        await engine.UpdateForkchoice(parent.Hash!, parent.Hash!, heads.Finalized.Hash!);
        if (_logger.IsWarn) _logger.Warn($"Safe L2 head retreats to {parent.ToString(BlockHeader.Format.Short)} to replace block {height}.");
    }

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
        await engine.UpdateForkchoice(block.Hash!, heads.Safe.Hash!, heads.Finalized.Hash!);
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

    private static bool Matches(Block local, DerivedBlock derived)
    {
        if (local.Beneficiary != derived.Beneficiary || !local.ExtraData.AsSpan().SequenceEqual(derived.ExtraData)
            || local.Transactions.Length != derived.Transactions.Length)
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
