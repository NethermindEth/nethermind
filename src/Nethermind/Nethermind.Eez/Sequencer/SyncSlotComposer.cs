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
using Nethermind.Eez.Execution.Stateless;
using Nethermind.Eez.Follower;
using Nethermind.Eez.Posting;
using Nethermind.Eez.Proving;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Eez.Sequencer;

public enum SyncSlotMode
{
    /// <summary>The slot is on time: the batch is pinned to the next L1 block.</summary>
    Steady,

    /// <summary>The sequencer is catching up: the batch settles past blocks in the next L1 block that takes it.</summary>
    Catchup,

    /// <summary>The slot is too late for a proof to land in it: the Sync block is produced and nothing is posted.</summary>
    Empty,
}

/// <summary>What one slot's Sync block is composed for.</summary>
/// <param name="Target">The L1 block the batch settles in; only a steady slot is pinned.</param>
/// <param name="L1Followed">An L1 block the follower has read, so a batch the observer saw settle at or below it and
/// the cursor has not reached did not stay on L1.</param>
public readonly record struct SlotPlan(SyncSlotMode Mode, BundleTarget Target, ulong L1Followed)
{
    public static SlotPlan Steady(BundleTarget target, ulong l1Followed) => new(SyncSlotMode.Steady, target, l1Followed);

    public static SlotPlan Catchup(ulong l1Followed) => new(SyncSlotMode.Catchup, BundleTarget.NextBlock, l1Followed);

    public static SlotPlan Empty(ulong l1Followed) => new(SyncSlotMode.Empty, BundleTarget.NextBlock, l1Followed);
}

/// <summary>Produces a slot's Sync block and posts the batch that settles it.</summary>
public interface ISyncSlotComposer
{
    /// <returns>The Sync block, committed as the head.</returns>
    Task<BlockHeader> Compose(BlockHeader parent, SlotPlan plan, FollowerHeads heads, CancellationToken token);
}

/// <summary>
/// Produces a slot's Sync block and the batch that settles everything since the cursor with it. With nothing
/// cross-chain to carry, the Sync block is empty and the batch is anchor-only. A batch still above the cursor or a late
/// slot leaves the Sync block empty with nothing posted: a second batch from the same cursor would revert, and a late
/// proof misses its block. A backlog longer than one batch settles, in blocks or in gas, is settled in grid-aligned
/// chunks first, to whichever L1 block takes them. Before any attester is asked, the batch passes the same settlement
/// check the attesters run.
/// </summary>
public sealed class SyncSlotComposer(
    IBlockTree blockTree,
    ISequencedBlocks blocks,
    IWitnessRecorder witnesses,
    OptimisticLedger ledger,
    AttestationQuorum quorum,
    IQuorumRegistrationReader registrations,
    IPostBatchPoster poster,
    IBatchCheck check,
    EezSettlementContext context,
    RollupTiming timing,
    ComposerSettings settings,
    ILogManager logManager) : ISyncSlotComposer
{
    private readonly ILogger _logger = logManager.GetClassLogger<SyncSlotComposer>();
    private ulong _lastCursor;

    public async Task<BlockHeader> Compose(BlockHeader parent, SlotPlan plan, FollowerHeads heads, CancellationToken token)
    {
        BlockHeader cursor = heads.Safe;
        if (cursor.Number < _lastCursor)
        {
            ledger.RollBack(cursor.Number);
        }

        _lastCursor = cursor.Number;
        ledger.ConfirmThrough(cursor.Number);
        ledger.ExpireUnconfirmed(cursor.Number, plan.L1Followed);
        witnesses.ForgetThrough(heads.Finalized.Number);
        if (ledger.TakeFailed(cursor.Number) is { } failed)
        {
            if (_logger.IsWarn) _logger.Warn($"The batch settling L2 block {failed.Batch.SyncHeight} did not settle{(failed.SlotSkipped ? " in its skipped slot" : "")}; the next slot posts again.");
            return await blocks.Empty(parent, heads);
        }

        if (ledger.Blocks(cursor.Number))
        {
            if (_logger.IsDebug) _logger.Debug($"A batch above the cursor {cursor.Number} is still unsettled; the Sync block at {parent.Number + 1} posts nothing.");
            return await blocks.Empty(parent, heads);
        }

        ulong syncHeight = parent.Number + 1;
        if (syncHeight - cursor.Number > settings.MaxBlocksPerBatch)
        {
            await SettleChunk(cursor, syncHeight, token);
            return await blocks.Empty(parent, heads);
        }

        BlockHeader sync = await blocks.Empty(parent, heads);
        if (plan.Mode != SyncSlotMode.Empty)
        {
            await Settle(cursor, sync, plan.Target, token);
        }

        return sync;
    }

    /// <summary>Settles the oldest part of a long backlog, up to a past Sync block on the slot grid.</summary>
    private async Task SettleChunk(BlockHeader cursor, ulong syncHeight, CancellationToken token)
    {
        if (timing.HistoricalChunkBoundary(cursor.Number, syncHeight, settings.MaxBlocksPerBatch) is not { } boundary
            || blockTree.FindHeader(boundary, BlockTreeLookupOptions.RequireCanonical) is not { } terminal)
        {
            if (_logger.IsWarn) _logger.Warn($"No Sync block between the cursor {cursor.Number} and {syncHeight} can end a chunk of {settings.MaxBlocksPerBatch} blocks.");
            return;
        }

        if (_logger.IsInfo) _logger.Info($"Settling the backlog from L2 block {cursor.Number} to {terminal.Number} ahead of {syncHeight}.");
        await Settle(cursor, terminal, BundleTarget.NextBlock, token);
    }

    /// <summary>
    /// Proves and posts the anchor-only batch settling everything after <paramref name="cursor"/> up to
    /// <paramref name="last"/>. Nothing here stops the node: a batch that cannot be posted now is posted by a later slot.
    /// </summary>
    private async Task Settle(BlockHeader cursor, BlockHeader last, BundleTarget target, CancellationToken token)
    {
        ProvedBlock[] proved = [];
        try
        {
            List<Block> span = Span(cursor, last);
            PostBatch batch = AnchorBatch.Build(context.RollupId, cursor.Hash!, last.Hash!, AnchorBatch.Da(span), quorum.ProofSystems);
            if (PostBatchGas.Needed(batch, quorum.ProofSystems.Length) > settings.MaxPostBatchGas)
            {
                if (FittingPrefix(cursor, span) is not { } fitting)
                {
                    if (_logger.IsError) _logger.Error($"No chunk of L2 blocks {cursor.Number + 1}-{last.Number} ending on the slot grid fits the {settings.MaxPostBatchGas} gas of one batch.");
                    return;
                }

                if (_logger.IsInfo) _logger.Info($"L2 blocks {cursor.Number + 1}-{last.Number} need more than one batch's gas; settling up to {fitting.Last.Number} first.");
                (span, batch, last, target) = (fitting.Span, fitting.Batch, fitting.Last, BundleTarget.NextBlock);
            }

            proved = new ProvedBlock[span.Count];
            Prove(cursor, span, proved);
            ProveRequest request = new(context.RollupId, cursor.Number + 1, last.Number, batch, proved);
            QuorumRegistration registration = await registrations.Read(quorum.Attesters, token);
            check.Check(request, registration, quorum.Attesters);
            Attestation[] attestations = await quorum.Attest(request, registration, target, token);
            await Post(batch, attestations, last, target, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is EezSettlementException or EezStatelessException)
        {
            if (_logger.IsError) _logger.Error($"The batch settling L2 blocks {cursor.Number + 1}-{last.Number} fails the settlement check; it is not posted: {e.Message}");
        }
        catch (Exception e)
        {
            if (_logger.IsWarn) _logger.Warn($"L2 blocks {cursor.Number + 1}-{last.Number} were not posted this slot: {e.Message}");
        }
        finally
        {
            foreach (ProvedBlock? block in proved)
            {
                block?.Witness.Dispose();
            }
        }
    }

    /// <summary>
    /// The longest start of <paramref name="span"/> that ends on the slot grid of its last block and fits one batch's
    /// gas, since derivation rebuilds a Sync block by its position. Each Live block's DA budget makes one slot fit.
    /// </summary>
    private (List<Block> Span, PostBatch Batch, BlockHeader Last)? FittingPrefix(BlockHeader cursor, List<Block> span)
    {
        for (int count = span.Count - (int)timing.K; count > 0; count -= (int)timing.K)
        {
            List<Block> prefix = span[..count];
            BlockHeader last = prefix[^1].Header;
            PostBatch batch = AnchorBatch.Build(context.RollupId, cursor.Hash!, last.Hash!, AnchorBatch.Da(prefix), quorum.ProofSystems);
            if (PostBatchGas.Needed(batch, quorum.ProofSystems.Length) <= settings.MaxPostBatchGas)
            {
                return (prefix, batch, last);
            }
        }

        return null;
    }

    private async Task Post(PostBatch batch, Attestation[] attestations, BlockHeader last, BundleTarget target, CancellationToken token)
    {
        Address[] proofSystems = new Address[attestations.Length];
        byte[][] proofs = new byte[attestations.Length][];
        ulong[] indexes = new ulong[attestations.Length];
        for (int i = 0; i < attestations.Length; i++)
        {
            (proofSystems[i], proofs[i], indexes[i]) = (attestations[i].ProofSystem, attestations[i].Proof, (ulong)i);
        }

        PostBatch attested = batch with
        {
            ProofSystems = proofSystems,
            RollupIdsWithProofSystems = [new RollupProofSystems(context.RollupId, indexes)],
            Proofs = proofs,
        };
        SignedPostBatch signed = await poster.Sign(EezCalldata.EncodePostAndVerifyBatch(attested), token);
        ulong height = last.Number;
        ledger.Begin(new PostedBatch(height, signed.Hash, last));
        _ = Follow(signed, height, last.Hash!, target, token);
    }

    /// <summary>Submits the batch and records what became of it; the cursor has the final word.</summary>
    private async Task Follow(SignedPostBatch signed, ulong height, Hash256 settles, BundleTarget target, CancellationToken token)
    {
        try
        {
            ulong block = await poster.Submit([signed.Raw], target, token);
            PostResult result = await poster.Observe(signed.Hash, block, target, settles.ValueHash256, token);
            if (result.Outcome == PostOutcome.Settled)
            {
                ledger.MarkSettled(height, result.L1Block);
                if (_logger.IsInfo) _logger.Info($"Batch {signed.Hash} settled L2 block {height} in L1 block {result.L1Block}.");
            }
            else
            {
                ledger.MarkFailed(height, result.Outcome == PostOutcome.SlotSkipped);
                if (_logger.IsWarn) _logger.Warn($"Batch {signed.Hash} settling L2 block {height} missed L1 block {block}: {result.Outcome}.");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            ledger.MarkFailed(height, false);
            if (_logger.IsWarn) _logger.Warn($"Batch {signed.Hash} settling L2 block {height} was not posted: {e.Message}");
        }
    }

    /// <summary>The blocks after <paramref name="cursor"/> up to <paramref name="last"/>, oldest first, by parent hash.</summary>
    private List<Block> Span(BlockHeader cursor, BlockHeader last)
    {
        List<Block> span = new((int)(last.Number - cursor.Number));
        Hash256 next = last.Hash!;
        while (next != cursor.Hash)
        {
            Block block = blockTree.FindBlock(next, BlockTreeLookupOptions.None)
                ?? throw new EezFollowerException($"Block {next} between the cursor {cursor.Number} and {last.Number} is missing.");
            if (block.Number <= cursor.Number)
            {
                throw new EezFollowerException($"Block {last.Number} does not descend from the cursor {cursor.ToString(BlockHeader.Format.Short)}.");
            }

            span.Add(block);
            next = block.ParentHash!;
        }

        span.Reverse();
        return span;
    }

    /// <summary>Fills <paramref name="proved"/> block by block, so the witnesses already read are disposed if a later one fails.</summary>
    private void Prove(BlockHeader cursor, List<Block> span, ProvedBlock[] proved)
    {
        BlockHeader parent = cursor;
        for (int i = 0; i < span.Count; i++)
        {
            Block block = span[i];
            proved[i] = new ProvedBlock(block.Number, block.Hash!, block.ParentHash!, Rlp.Encode(block).Bytes, witnesses.Get(parent, block));
            parent = block.Header;
        }
    }
}

/// <summary>How much one batch may settle.</summary>
public sealed record ComposerSettings(ulong MaxBlocksPerBatch, ulong MaxPostBatchGas);
