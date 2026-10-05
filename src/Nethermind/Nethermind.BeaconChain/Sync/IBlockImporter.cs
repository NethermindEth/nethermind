// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.BeaconChain.Sync;

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
    /// The caller's <c>is_data_available</c> check lacks columns. The block is neither invalid nor recorded;
    /// retry when those columns arrive, not merely on the next tick.
    /// </summary>
    DataUnavailable,

    /// <summary>
    /// The block needs its parent's unverified full payload, or its parent is deferred for that reason
    /// (specs/gloas/fork-choice.md <c>on_block</c>: <c>is_parent_node_full</c> requires <c>is_payload_verified</c>).
    /// Proposer and signature checks have passed; the block is neither invalid nor recorded by fork choice.
    /// Retry after the missing envelope imports.
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

/// <summary>Runs state transitions, fork choice and persistence for one lineage.</summary>
/// <remarks>Call only from the sync orchestrator's single worker; the state is mutable.</remarks>
public interface IBlockImporter
{
    /// <summary>Whether the block is already known to fork choice.</summary>
    bool IsKnown(Hash256 blockRoot);

    /// <summary>
    /// Runs the block's fork-specific state transition, registers its attestations and attester slashings
    /// and the block with fork choice, then persists it. Fulu calls <c>engine_newPayload</c> through the transition;
    /// Gloas carries a bid and sends its payload through <see cref="ImportEnvelope"/>.
    /// </summary>
    /// <param name="verifySignatures">
    /// Disable for previously verified persisted blocks. A replayed Gloas child building on its parent's
    /// full payload also proves that payload was verified; record the parent accordingly instead of deferring.
    /// </param>
    BlockImportResult Import(ForkedSignedBeaconBlock block, Hash256 blockRoot, bool verifySignatures);

    /// <summary>Imports a requested range-sync or by-root block with signature verification.</summary>
    /// <remarks>Range sync regenerates states freely. By-root requests, which gossip can induce,
    /// use a separate small budget; neither consumes the gossip regeneration budget.</remarks>
    /// <param name="fetchedByRoot">Whether the block was fetched by root rather than by range sync.</param>
    BlockImportResult ImportRequested(ForkedSignedBeaconBlock block, Hash256 blockRoot, bool fetchedByRoot = false) => Import(block, blockRoot, verifySignatures: true);

    /// <summary>
    /// Spec <c>on_execution_payload_envelope</c> (specs/gloas/fork-choice.md): verifies the envelope
    /// against the post-state of the block it names, including the engine call, and on a VALID or
    /// optimistic verdict records that block's payload as verified.
    /// </summary>
    /// <remarks>
    /// A VALID verdict marks the block and its ancestors VALID, as a node's status is that of the payload its bid builds on (the envelope
    /// payload's <c>parent_hash</c>), and records the block's own payload as VALID apart from that status (specs/bellatrix/optimistic-sync.md).
    /// </remarks>
    /// <exception cref="System.InvalidOperationException">The engine notifier returned no verdict; a local fault, not a rejection.</exception>
    ExecutionPayloadEnvelopeImportResult ImportEnvelope(SignedExecutionPayloadEnvelope envelope);

    /// <summary>Checks the signature of a gossip envelope against the post-state of the block it names, without its data or execution payload.</summary>
    /// <returns><c>null</c> when that block's state is not held; otherwise whether the builder, or the self-building proposer, signed it.</returns>
    bool? VerifyEnvelopeSignature(SignedExecutionPayloadEnvelope envelope);

    /// <summary>Advances fork-choice time to the start of <paramref name="slot"/>; call at least once per slot.</summary>
    void OnSlotTick(ulong slot);

    /// <summary>Recomputes the fork-choice head, updates the canonical slot index, and maps the checkpoints to execution hashes.</summary>
    /// <remarks>
    /// A Gloas block maps to its bid's <c>parent_block_hash</c> (specs/gloas/fork-choice.md
    /// <c>notify_forkchoice_updated</c>), except a head that <c>get_head</c> resolves FULL, which maps to the
    /// bid's <c>block_hash</c>: only then does the head build on that payload.
    /// </remarks>
    HeadView ComputeHead();

    /// <summary>Whether gossip needs the worker to recompute the cached head.</summary>
    internal bool IsHeadStale => false;

    /// <summary>Why the last import refused its block, where its result alone does not say.</summary>
    ImportRefusal LastRefusal => ImportRefusal.None;

    /// <summary>Whether the last import failed an explicit gossip rejection check.</summary>
    internal bool RejectGossip => false;

    /// <summary>
    /// Applies a VALID or INVALID <c>forkchoiceUpdated</c> verdict on <paramref name="headExecutionHash"/>, the head hash sent for
    /// <paramref name="headRoot"/>, to the payloads it names; recompute the head afterwards. Other statuses change nothing.
    /// </summary>
    void OnForkchoiceUpdated(Hash256 headRoot, Hash256 headExecutionHash, PayloadStatusV1 status);

    /// <summary>Persists the finalized state and restart anchor, then prunes fork choice and block storage.</summary>
    void OnFinalized(CheckpointRef finalized);

    /// <summary>Feeds a gossip aggregate to fork choice once its aggregator is authenticated; invalid aggregates are counted and dropped.</summary>
    /// <returns>True when accepted, false for a gossip rejection, or null when validation cannot decide or must ignore.</returns>
    bool? OnGossipAggregate(SignedAggregateAndProof aggregate);

    /// <inheritdoc cref="OnGossipAggregate(SignedAggregateAndProof)"/>
    bool? OnGossipAggregate(SignedAggregateAndProofGloas aggregate);

    /// <summary>Feeds a gossip attester slashing to fork choice; invalid slashings are counted and dropped.</summary>
    /// <returns>True when accepted, false for a gossip rejection, or null when validation cannot decide or must ignore.</returns>
    bool? OnGossipAttesterSlashing(AttesterSlashing slashing);

    /// <inheritdoc cref="OnGossipAttesterSlashing(AttesterSlashing)"/>
    bool? OnGossipAttesterSlashing(AttesterSlashingGloas slashing);

    /// <summary>Feeds a gossip payload attestation to fork choice; invalid votes are counted and dropped.</summary>
    /// <returns>True when accepted, false for a gossip rejection, or null when validation cannot decide or must ignore.</returns>
    bool? OnGossipPayloadAttestation(PayloadAttestationMessage message);
}

/// <summary>Creates the importer once the anchor is known; lets tests script the consensus core.</summary>
public interface IBlockImporterFactory
{
    IBlockImporter Create(ForkedBeaconState anchorState, ForkedSignedBeaconBlock anchorBlock, Hash256 anchorRoot);
}
