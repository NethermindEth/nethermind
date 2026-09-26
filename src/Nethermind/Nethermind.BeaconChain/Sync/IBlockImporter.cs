// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.StateTransition;
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

    /// <summary>
    /// The execution layer could not be consulted, so the payload has no verdict. The block is not
    /// invalid and must be retried once the engine answers; nothing about it has been recorded.
    /// </summary>
    EngineUnavailable,

    /// <summary>
    /// The block's blob data is not (yet) available under the caller's <c>is_data_available</c>
    /// rule. The block is not invalid and nothing about it has been recorded, but unlike
    /// <see cref="EngineUnavailable"/> a blind retry will not help: it must be retried once the
    /// missing columns are held, not merely on the next tick.
    /// </summary>
    DataUnavailable,

    /// <summary>
    /// The block builds on its parent's full payload, whose envelope is not yet verified
    /// (specs/gloas/fork-choice.md <c>on_block</c>: <c>is_parent_node_full</c> requires
    /// <c>is_payload_verified</c>). The block is not invalid and nothing about it has been recorded;
    /// retry it once that envelope imports. Its proposer and proposer signature are verified first.
    /// </summary>
    ParentPayloadUnverified,
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
    /// Gossip-level proposer check: whether the block's claimed proposer matches the proposer
    /// lookahead of a state the importer holds (the Fulu lineage state, or a Gloas block's parent
    /// post-state). Returns <c>true</c> (defer to the state transition) when no held state's
    /// lookahead window covers the block's slot.
    /// </summary>
    bool IsExpectedProposer(ForkedSignedBeaconBlock block);

    /// <summary>
    /// Runs the block through the state transition of the fork its slot belongs to, registers it
    /// with fork choice along with its body attestations and attester slashings, and persists it.
    /// A Fulu block drives <c>engine_newPayload</c> via the transition hook; a Gloas block carries
    /// only a bid, and its payload reaches the engine through <see cref="ImportEnvelope"/>.
    /// </summary>
    /// <param name="verifySignatures">
    /// Skip for blocks replayed from the store, which were verified before being persisted. A
    /// replayed Gloas block that builds on its parent's full payload also proves that payload was
    /// verified, so the parent is recorded as such instead of deferring the block.
    /// </param>
    BlockImportResult Import(ForkedSignedBeaconBlock block, Hash256 blockRoot, bool verifySignatures);

    /// <summary>
    /// Spec <c>on_execution_payload_envelope</c> (specs/gloas/fork-choice.md): verifies the envelope
    /// against the post-state of the block it names, including the engine call, and on a VALID or
    /// optimistic verdict records that block's payload as verified.
    /// </summary>
    /// <remarks>
    /// Recording does not upgrade the block's execution status: the one-dimensional fork choice
    /// cannot undo a VALID that a later payload-status split would contradict.
    /// </remarks>
    /// <exception cref="System.InvalidOperationException">The engine notifier returned no verdict; a local fault, not a rejection.</exception>
    ExecutionPayloadEnvelopeImportResult ImportEnvelope(SignedExecutionPayloadEnvelope envelope);

    /// <summary>Advances fork-choice time to the start of <paramref name="slot"/>; call at least once per slot.</summary>
    void OnSlotTick(ulong slot);

    /// <summary>Recomputes the fork-choice head, updates the canonical slot index, and maps the checkpoints to execution hashes.</summary>
    /// <remarks>
    /// A Gloas block maps to its bid's <c>parent_block_hash</c> (specs/gloas/fork-choice.md
    /// <c>notify_forkchoice_updated</c>), except a head whose payload is verified, which maps to the
    /// bid's <c>block_hash</c>: only then has the execution layer received that payload.
    /// </remarks>
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

    /// <summary>Feeds a Gloas gossip aggregate to fork choice; invalid attestations are counted and dropped.</summary>
    void OnGossipAggregate(SignedAggregateAndProofGloas aggregate);

    /// <summary>Feeds a gossip attester slashing to fork choice; invalid slashings are dropped.</summary>
    void OnGossipAttesterSlashing(AttesterSlashing slashing);

    /// <summary>Feeds a Gloas gossip attester slashing to fork choice; invalid slashings are dropped.</summary>
    void OnGossipAttesterSlashing(AttesterSlashingGloas slashing);
}

/// <summary>Creates the importer once the anchor is known; lets tests script the consensus core.</summary>
public interface IBlockImporterFactory
{
    IBlockImporter Create(BeaconStateFulu anchorState, SignedBeaconBlock anchorBlock, Hash256 anchorRoot);
}
