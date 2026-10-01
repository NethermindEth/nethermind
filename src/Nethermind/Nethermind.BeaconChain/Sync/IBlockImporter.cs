// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.BeaconChain.Sync;

/// <summary>The outcome of running a block through the import pipeline.</summary>
/// <summary>The cause of an import refusal that its <see cref="BlockImportResult"/> does not tell apart.</summary>
public enum ImportRefusal
{
    None,

    /// <summary>The state the block builds on is not held, and this slot's budget for regenerating it is spent; a later slot can import it.</summary>
    RegenerationBudget,

    /// <summary>
    /// A fork-choice <c>on_block</c> check against this node's own store refused the block, such as the finalized slot or
    /// descent from the finalized checkpoint, which says nothing of the block's own data.
    /// </summary>
    LocalAdmission,
}

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
    /// <c>is_payload_verified</c>), or on a parent that is itself deferred for that reason. The block is
    /// not invalid and fork choice has not recorded it; retry it once that envelope imports. Its proposer
    /// and proposer signature are verified first.
    /// </summary>
    ParentPayloadUnverified,

    /// <summary>
    /// fork-choice.md on_block: the block is within MAXIMUM_GOSSIP_CLOCK_DISPARITY but waits for its slot tick.
    /// Untrusted blocks have passed their state transition and proposer signature verification; fork choice has not recorded them.
    /// </summary>
    FutureSlot,
}

/// <summary>The current fork-choice head and checkpoints mapped to execution block hashes for <c>forkchoiceUpdated</c>.</summary>
/// <param name="HeadExecutionHash"><c>null</c> only for a pre-merge head, which cannot occur on Fulu-era mainnet.</param>
/// <param name="HeadPayloadFull">Whether <c>get_head</c> resolved the head <c>PAYLOAD_STATUS_FULL</c>.</param>
public sealed record HeadView(
    Hash256 HeadRoot,
    ulong HeadSlot,
    Hash256? HeadExecutionHash,
    Hash256? JustifiedExecutionHash,
    Hash256? FinalizedExecutionHash,
    CheckpointRef Justified,
    CheckpointRef Finalized,
    bool HeadPayloadFull = false);

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

    /// <summary>Imports, with signatures verified, a block this node requested from a peer: by range sync or by a by-root backfill.</summary>
    /// <remarks>
    /// Such a block is not charged to the per-slot budget for regenerating missing states that blocks from gossip share. A
    /// range-sync block regenerates freely; a block fetched by root, which a gossip block can name, has a small budget of its own.
    /// </remarks>
    /// <param name="fetchedByRoot">Whether the block was fetched by root rather than by range sync.</param>
    BlockImportResult ImportRequested(ForkedSignedBeaconBlock block, Hash256 blockRoot, bool fetchedByRoot = false) => Import(block, blockRoot, verifySignatures: true);

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
    /// <c>notify_forkchoice_updated</c>), except a head that <c>get_head</c> resolves FULL, which maps to the
    /// bid's <c>block_hash</c>: only then does the head build on that payload.
    /// </remarks>
    HeadView ComputeHead();

    /// <summary>Why the last import refused its block, where its result alone does not say.</summary>
    ImportRefusal LastRefusal => ImportRefusal.None;

    /// <summary>
    /// Applies a VALID or INVALID <c>forkchoiceUpdated</c> verdict on <paramref name="headExecutionHash"/>, the head hash sent for
    /// <paramref name="headRoot"/>, to the payloads it names; recompute the head afterwards. Other statuses change nothing.
    /// </summary>
    void OnForkchoiceUpdated(Hash256 headRoot, Hash256 headExecutionHash, PayloadStatusV1 status);

    /// <summary>
    /// Reacts to a finalized-checkpoint advance: persists the finalized state, advances the
    /// persisted anchor so restarts resume there, and prunes fork choice and the block store.
    /// </summary>
    void OnFinalized(CheckpointRef finalized);

    /// <summary>Feeds a gossip aggregate to fork choice once its aggregator is authenticated; invalid aggregates are counted and dropped.</summary>
    /// <returns>Whether fork choice accepted the aggregate, its selection proof and signatures included.</returns>
    bool OnGossipAggregate(SignedAggregateAndProof aggregate);

    /// <summary>Feeds a Gloas gossip aggregate to fork choice once its aggregator is authenticated; invalid aggregates are counted and dropped.</summary>
    /// <returns>Whether fork choice accepted the aggregate, its selection proof and signatures included.</returns>
    bool OnGossipAggregate(SignedAggregateAndProofGloas aggregate);

    /// <summary>Feeds a gossip attester slashing to fork choice; invalid slashings are counted and dropped.</summary>
    /// <returns>Whether fork choice accepted the slashing, its signatures included.</returns>
    bool OnGossipAttesterSlashing(AttesterSlashing slashing);

    /// <summary>Feeds a Gloas gossip attester slashing to fork choice; invalid slashings are counted and dropped.</summary>
    /// <returns>Whether fork choice accepted the slashing, its signatures included.</returns>
    bool OnGossipAttesterSlashing(AttesterSlashingGloas slashing);

    /// <summary>Feeds a gossip payload attestation to fork choice; invalid votes are counted and dropped.</summary>
    /// <returns>Whether fork choice verified and recorded the vote, its PTC membership and signature included.</returns>
    bool OnGossipPayloadAttestation(PayloadAttestationMessage message);
}

/// <summary>Creates the importer once the anchor is known; lets tests script the consensus core.</summary>
public interface IBlockImporterFactory
{
    IBlockImporter Create(ForkedBeaconState anchorState, ForkedSignedBeaconBlock anchorBlock, Hash256 anchorRoot);
}
