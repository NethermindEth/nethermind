// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.ValidatorApi;

/// <summary>What a validator client needs from the beacon node: the Beacon API routes a validator client calls, as typed in-process calls.</summary>
/// <remarks>
/// <para>
/// This is the seam between the node, which knows the chain, and validator code, which knows the keys and the schedule.
/// Both the Beacon REST handlers and the embedded duty code consume it; neither reaches past it into the import worker.
/// Each member names the route it corresponds to (ethereum/beacon-APIs).
/// </para>
/// <para>
/// Thread safety: every member may be called from any thread and concurrently; the implementation marshals to the import
/// worker or reads immutable snapshots.
/// </para>
/// <para>
/// Failures the caller can act on are thrown as <see cref="BeaconNodeApiException"/>. A batch submission instead returns
/// the items it refused, and still publishes the others.
/// </para>
/// <para>Covers the Fulu duties. Signing is not part of it: the node never sees a key.</para>
/// </remarks>
public interface IBeaconNodeValidatorApi
{
    /// <summary><c>GET /eth/v1/node/syncing</c>: whether the node is fit to hand out duty data.</summary>
    ValueTask<SyncingStatus> GetSyncingStatus(CancellationToken cancellationToken);

    /// <summary><c>POST /eth/v1/beacon/states/head/validators</c>, filtered by public key: the registry entries of the given keys in the head state.</summary>
    /// <returns>One entry for each key found; a key not in the registry has none.</returns>
    ValueTask<IReadOnlyList<ValidatorSummary>> GetValidators(IReadOnlyList<BlsPublicKey> publicKeys, CancellationToken cancellationToken);

    /// <summary><c>GET /eth/v1/beacon/blocks/head/root</c>: the head block root, which a sync-committee message signs.</summary>
    ValueTask<Hash256> GetHeadBlockRoot(CancellationToken cancellationToken);

    /// <summary><c>POST /eth/v1/validator/duties/attester/{epoch}</c>: the attestation slot and committee of each given validator.</summary>
    /// <param name="epoch">At most the epoch after the current one.</param>
    /// <returns>
    /// One duty for each given validator that is active in <paramref name="epoch"/>. The dependent root is
    /// <c>get_block_root_at_slot(state, compute_start_slot_at_epoch(epoch - 1) - 1)</c>.
    /// </returns>
    ValueTask<DutiesResult<AttesterDuty>> GetAttesterDuties(ulong epoch, IReadOnlyList<ulong> validatorIndices, CancellationToken cancellationToken);

    /// <summary><c>GET /eth/v2/validator/duties/proposer/{epoch}</c>: the proposer of every slot in the epoch, for all validators.</summary>
    /// <param name="epoch">At most the epoch after the current one (EIP-7917 proposer lookahead).</param>
    /// <returns>The dependent root is <c>get_block_root_at_slot(state, compute_start_slot_at_epoch(epoch - 1) - 1)</c>.</returns>
    ValueTask<DutiesResult<ProposerDuty>> GetProposerDuties(ulong epoch, CancellationToken cancellationToken);

    /// <summary><c>POST /eth/v1/validator/duties/sync/{epoch}</c>: the sync-committee positions of each given validator.</summary>
    /// <param name="epoch">An epoch in the current or the next sync-committee period.</param>
    /// <returns>One duty for each given validator that is in the committee; the result has no dependent root.</returns>
    ValueTask<DutiesResult<SyncDuty>> GetSyncDuties(ulong epoch, IReadOnlyList<ulong> validatorIndices, CancellationToken cancellationToken);

    /// <summary><c>GET /eth/v1/validator/attestation_data</c>: what a validator attesting in <paramref name="slot"/> signs (phase0/validator.md "Attestation data").</summary>
    /// <param name="slot">The current slot, or one within the gossip clock allowance of it.</param>
    /// <param name="committeeIndex">Kept for the route's shape; since Electra (EIP-7549) the returned <c>data.index</c> is 0 for every committee.</param>
    /// <exception cref="BeaconNodeApiException"><see cref="BeaconNodeApiError.NotReady"/> while the node is syncing or its head is optimistic.</exception>
    ValueTask<AttestationData> ProduceAttestationData(ulong slot, ulong committeeIndex, CancellationToken cancellationToken);

    /// <summary><c>POST /eth/v2/beacon/pool/attestations</c>: validates the attestations as gossip, publishes each on its subnet and feeds it to fork choice.</summary>
    /// <returns>The attestations refused; empty when all were published.</returns>
    ValueTask<IReadOnlyList<SubmissionFailure>> SubmitAttestations(IReadOnlyList<SingleAttestation> attestations, CancellationToken cancellationToken);

    /// <summary><c>POST /eth/v1/validator/beacon_committee_subscriptions</c>: prepares the node's attestation subnets for upcoming duties.</summary>
    ValueTask SubscribeToBeaconCommittees(IReadOnlyList<BeaconCommitteeSubscription> subscriptions, CancellationToken cancellationToken);

    /// <summary><c>GET /eth/v2/validator/aggregate_attestation</c>: the best aggregate the node holds for the given vote.</summary>
    /// <param name="attestationDataRoot">The <c>hash_tree_root</c> of the <see cref="AttestationData"/> to aggregate.</param>
    /// <exception cref="BeaconNodeApiException"><see cref="BeaconNodeApiError.NotFound"/> when the node holds no matching attestation.</exception>
    ValueTask<Attestation> GetAggregateAttestation(ulong slot, Hash256 attestationDataRoot, ulong committeeIndex, CancellationToken cancellationToken);

    /// <summary><c>POST /eth/v2/validator/aggregate_and_proofs</c>: validates the aggregates as gossip and publishes them on <c>beacon_aggregate_and_proof</c>.</summary>
    /// <returns>The aggregates refused; empty when all were published.</returns>
    ValueTask<IReadOnlyList<SubmissionFailure>> SubmitAggregateAndProofs(IReadOnlyList<SignedAggregateAndProof> aggregates, CancellationToken cancellationToken);

    /// <summary><c>POST /eth/v1/validator/prepare_beacon_proposer</c>: sets the fee recipient the node builds payloads with for each validator.</summary>
    ValueTask PrepareBeaconProposers(IReadOnlyList<ProposerPreparation> preparations, CancellationToken cancellationToken);

    /// <summary><c>GET /eth/v3/validator/blocks/{slot}</c>: builds an unsigned block on the current head for the slot's proposer to sign.</summary>
    /// <param name="randaoReveal">The proposer's signature over the slot's epoch.</param>
    /// <param name="graffiti">The 32 bytes to place in the block body.</param>
    /// <exception cref="BeaconNodeApiException"><see cref="BeaconNodeApiError.NotReady"/> while the node is syncing or its head is optimistic.</exception>
    ValueTask<ProducedBlock> ProduceBlock(ulong slot, BlsSignature randaoReveal, Hash256 graffiti, CancellationToken cancellationToken);

    /// <summary><c>POST /eth/v2/beacon/blocks</c>: validates the block as gossip, publishes it and its data column sidecars, and imports it.</summary>
    /// <exception cref="BeaconNodeApiException"><see cref="BeaconNodeApiError.InvalidRequest"/> when the block fails validation; it is not published.</exception>
    ValueTask PublishBlock(SignedBlockContents contents, CancellationToken cancellationToken);

    /// <summary><c>POST /eth/v1/validator/sync_committee_subscriptions</c>: prepares the node's sync-committee subnets for the given validators.</summary>
    ValueTask SubscribeToSyncCommittees(IReadOnlyList<SyncCommitteeSubscription> subscriptions, CancellationToken cancellationToken);

    /// <summary><c>POST /eth/v1/beacon/pool/sync_committees</c>: validates the messages as gossip and publishes each on its sync-committee subnets.</summary>
    /// <returns>The messages refused; empty when all were published.</returns>
    ValueTask<IReadOnlyList<SubmissionFailure>> SubmitSyncCommitteeMessages(IReadOnlyList<SyncCommitteeMessage> messages, CancellationToken cancellationToken);

    /// <summary><c>GET /eth/v1/validator/sync_committee_contribution</c>: the best contribution the node holds for one subcommittee's vote.</summary>
    /// <exception cref="BeaconNodeApiException"><see cref="BeaconNodeApiError.NotFound"/> when the node holds no matching message.</exception>
    ValueTask<SyncCommitteeContribution> ProduceSyncCommitteeContribution(ulong slot, ulong subcommitteeIndex, Hash256 beaconBlockRoot, CancellationToken cancellationToken);

    /// <summary><c>POST /eth/v1/validator/contribution_and_proofs</c>: validates the contributions as gossip and publishes them on <c>sync_committee_contribution_and_proof</c>.</summary>
    /// <returns>The contributions refused; empty when all were published.</returns>
    ValueTask<IReadOnlyList<SubmissionFailure>> SubmitContributionAndProofs(IReadOnlyList<SignedContributionAndProof> contributions, CancellationToken cancellationToken);
}
