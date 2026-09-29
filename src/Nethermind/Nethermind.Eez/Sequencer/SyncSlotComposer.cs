// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
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

/// <summary>Produces a slot's Sync block and posts the batch that settles it.</summary>
public interface ISyncSlotComposer
{
    /// <returns>The Sync block, committed as the head.</returns>
    Task<BlockHeader> Compose(BlockHeader parent, BundleTarget target, SyncSlotMode mode, FollowerHeads heads, CancellationToken token);
}

/// <summary>
/// Produces a slot's Sync block and the batch that settles everything since the cursor with it. With nothing
/// cross-chain to carry, the Sync block is empty and the batch is anchor-only. A batch still above the cursor, a backlog
/// longer than one batch settles, or a late slot each leave the Sync block empty with nothing posted: a second batch
/// from the same cursor would revert, a backlog is settled in grid-aligned chunks first, and a late proof misses its
/// block. Before any attester is asked, the batch passes the same settlement check the attesters run.
/// </summary>
public sealed class SyncSlotComposer(
    IBlockTree blockTree,
    ISequencedBlocks blocks,
    IWitnessRecorder witnesses,
    OptimisticLedger ledger,
    AttestationQuorum quorum,
    IQuorumRegistrationReader registrations,
    IPostBatchPoster poster,
    ISpecProvider specProvider,
    EezSettlementContext context,
    RollupTiming timing,
    ComposerSettings settings,
    ILogManager logManager) : ISyncSlotComposer
{
    private readonly ILogger _logger = logManager.GetClassLogger<SyncSlotComposer>();
    private readonly ILogManager _logManager = logManager;
    private ulong _lastCursor;

    public async Task<BlockHeader> Compose(BlockHeader parent, BundleTarget target, SyncSlotMode mode, FollowerHeads heads, CancellationToken token)
    {
        BlockHeader cursor = heads.Safe;
        if (cursor.Number < _lastCursor)
        {
            ledger.RollBack(cursor.Number);
        }

        _lastCursor = cursor.Number;
        ledger.ConfirmThrough(cursor.Number);
        if (ledger.TakeFailed(cursor.Number) is { } failed)
        {
            if (_logger.IsWarn) _logger.Warn($"The batch settling L2 block {failed.Batch.SyncHeight} did not settle{(failed.SlotSkipped ? " in its skipped slot" : "")}; the next slot posts again.");
            return await blocks.Empty(parent, heads);
        }

        if (ledger.Blocks(cursor.Number))
        {
            return await blocks.Empty(parent, heads);
        }

        ulong syncHeight = parent.Number + 1;
        if (syncHeight - cursor.Number > settings.MaxBlocksPerBatch)
        {
            await SettleChunk(cursor, syncHeight, token);
            return await blocks.Empty(parent, heads);
        }

        BlockHeader sync = await blocks.Empty(parent, heads);
        if (mode != SyncSlotMode.Empty)
        {
            await Settle(cursor, sync, mode == SyncSlotMode.Steady ? target : BundleTarget.NextBlock, token);
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
            PostBatch batch = AnchorBatch.Build(context.RollupId, cursor.Hash!, last.Hash!, Da(span), quorum.ProofSystems);
            ulong gas = PostBatchGas.Needed(batch, quorum.ProofSystems.Length);
            if (gas > settings.MaxPostBatchGas)
            {
                if (_logger.IsWarn) _logger.Warn($"The batch settling L2 blocks {cursor.Number + 1}-{last.Number} needs {gas} gas, over the {settings.MaxPostBatchGas} limit.");
                return;
            }

            proved = Prove(cursor, span);
            ProveRequest request = new(context.RollupId, cursor.Number + 1, last.Number, batch, proved);
            QuorumRegistration registration = await registrations.Read(quorum.Attesters, token);
            CheckLocally(request, registration);
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
            foreach (ProvedBlock block in proved)
            {
                block.Witness.Dispose();
            }
        }
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
            PostOutcome outcome = await poster.Observe(signed.Hash, block, target, settles.ValueHash256, token);
            if (outcome == PostOutcome.Settled)
            {
                ledger.MarkSettled(height);
                if (_logger.IsInfo) _logger.Info($"Batch {signed.Hash} settled L2 block {height} in L1 block {block}.");
            }
            else
            {
                ledger.MarkFailed(height, outcome == PostOutcome.SlotSkipped);
                if (_logger.IsWarn) _logger.Warn($"Batch {signed.Hash} settling L2 block {height} missed L1 block {block}: {outcome}.");
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

    /// <summary>
    /// Runs the attesters' own check on the batch the first registered attester is sent, so a batch nobody can attest
    /// is caught here. It re-executes the span statelessly from the same witnesses the attesters get.
    /// </summary>
    /// <exception cref="EezSettlementException">The batch claims something the span does not show.</exception>
    /// <exception cref="EezStatelessException">The span does not re-execute from its witnesses.</exception>
    private void CheckLocally(ProveRequest request, QuorumRegistration registration)
    {
        int first = Array.FindIndex(registration.VerificationKeys, static k => k != default);
        if (first < 0)
        {
            throw new ProveException(ProveFailureKind.Backend, "No configured attester is registered on the rollup manager.");
        }

        IAttester attester = quorum.Attesters[first];
        ProveRequest own = request.For(attester.ProofSystem);
        SettlementCheck check = new(specProvider, context with { ProofSystem = attester.ProofSystem, VerificationKey = registration.VerificationKeys[first] }, _logManager);
        EezStatelessBlock[] statelessBlocks = new EezStatelessBlock[own.Blocks.Count];
        (Hash256, Hash256)[] claims = new (Hash256, Hash256)[own.Blocks.Count];
        for (int i = 0; i < statelessBlocks.Length; i++)
        {
            statelessBlocks[i] = new EezStatelessBlock(own.Blocks[i].Rlp, own.Blocks[i].Witness);
            claims[i] = (own.Blocks[i].Hash, own.Blocks[i].ParentHash);
        }

        byte[] calldata = EezCalldata.EncodePostAndVerifyBatch(own.Batch);
        check.Verify(calldata, check.Execute(calldata, statelessBlocks, claims, own.FromBlock));
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

    private ProvedBlock[] Prove(BlockHeader cursor, List<Block> span)
    {
        ProvedBlock[] proved = new ProvedBlock[span.Count];
        BlockHeader parent = cursor;
        for (int i = 0; i < span.Count; i++)
        {
            Block block = span[i];
            proved[i] = new ProvedBlock(block.Number, block.Hash!, block.ParentHash!, Rlp.Encode(block).Bytes, witnesses.Get(parent, block));
            parent = block.Header;
        }

        return proved;
    }

    private static DaBlock[] Da(List<Block> span)
    {
        DaBlock[] da = new DaBlock[span.Count];
        for (int i = 0; i < da.Length; i++)
        {
            Block block = span[i];
            byte[][] transactions = new byte[block.Transactions.Length][];
            for (int j = 0; j < transactions.Length; j++)
            {
                transactions[j] = TxDecoder.Instance.Encode(block.Transactions[j], RlpBehaviors.SkipTypedWrapping).Bytes;
            }

            da[i] = new DaBlock(block.Beneficiary!, block.Header.ExtraData, transactions);
        }

        return da;
    }
}

/// <summary>How much one batch may settle.</summary>
public sealed record ComposerSettings(ulong MaxBlocksPerBatch, ulong MaxPostBatchGas);
