// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.Sync;

/// <summary>The outcome of running a block through the import pipeline.</summary>
public enum BlockImportResult
{
    Imported,
    AlreadyKnown,

    /// <summary>The parent is unknown to fork choice; the caller may backfill it by root and retry.</summary>
    UnknownParent,

    /// <summary>The block failed the state transition or a fork-choice assertion and was dropped.</summary>
    Invalid,
}

/// <summary>The current fork-choice head and checkpoints mapped to execution block hashes for <c>forkchoiceUpdated</c>.</summary>
/// <param name="HeadExecutionHash"><c>null</c> only for a pre-merge head, which cannot occur on Fulu-era mainnet.</param>
public sealed record HeadView(
    Hash256 HeadRoot,
    ulong HeadSlot,
    Hash256? HeadExecutionHash,
    Hash256? JustifiedExecutionHash,
    Hash256? FinalizedExecutionHash,
    CheckpointRef Justified,
    CheckpointRef Finalized);

/// <summary>
/// The consensus core of the import pipeline: the state transition, fork choice, and their
/// persistence. Everything here mutates single-lineage state and must be called from the sync
/// orchestrator's single worker only.
/// </summary>
public interface IBlockImporter
{
    /// <summary>Whether the block is already known to fork choice.</summary>
    bool IsKnown(Hash256 blockRoot);

    /// <summary>
    /// Gossip-level proposer check: whether the block's claimed proposer matches the lineage
    /// state's proposer lookahead. Returns <c>true</c> (defer to the state transition) when the
    /// block's slot is outside the lookahead window of the current lineage state.
    /// </summary>
    bool IsExpectedProposer(SignedBeaconBlock block);

    /// <summary>
    /// Runs the block through the state transition (driving <c>engine_newPayload</c> via the
    /// transition hook), registers it with fork choice along with its body attestations and
    /// attester slashings, and persists it.
    /// </summary>
    /// <param name="verifySignatures">Skip for blocks replayed from the store, which were verified before being persisted.</param>
    BlockImportResult Import(SignedBeaconBlock block, Hash256 blockRoot, bool verifySignatures);

    /// <summary>Advances fork-choice time to the start of <paramref name="slot"/>; call at least once per slot.</summary>
    void OnSlotTick(ulong slot);

    /// <summary>Recomputes the fork-choice head, updates the canonical slot index, and maps the checkpoints to execution hashes.</summary>
    HeadView ComputeHead();

    /// <summary>Propagates an INVALID <c>forkchoiceUpdated</c> verdict into fork choice; recompute the head afterwards.</summary>
    void OnInvalidExecutionPayload(Hash256 blockRoot, Hash256? latestValidHash);

    /// <summary>
    /// Reacts to a finalized-checkpoint advance: persists the finalized state, advances the
    /// persisted anchor so restarts resume there, and prunes fork choice and the block store.
    /// </summary>
    void OnFinalized(CheckpointRef finalized);

    /// <summary>Feeds a gossip aggregate to fork choice; invalid attestations are counted and dropped.</summary>
    void OnGossipAggregate(SignedAggregateAndProof aggregate);

    /// <summary>Feeds a gossip attester slashing to fork choice; invalid slashings are dropped.</summary>
    void OnGossipAttesterSlashing(AttesterSlashing slashing);
}

/// <summary>Creates the importer once the anchor is known; lets tests script the consensus core.</summary>
public interface IBlockImporterFactory
{
    IBlockImporter Create(BeaconStateFulu anchorState, SignedBeaconBlock anchorBlock, Hash256 anchorRoot);
}
