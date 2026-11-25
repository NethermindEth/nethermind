// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.ValidatorClient;

/// <summary>Signs duty objects with validator keys that this process never holds.</summary>
/// <remarks>
/// <para>
/// Each member takes the object itself, not its signing root: a signer that keeps slashing protection has to see what it signs.
/// The implementation derives the signing domain from the object's epoch and the chain's fork schedule.
/// </para>
/// <para>Thread safety: every member may be called from any thread and concurrently.</para>
/// </remarks>
public interface IValidatorSigner
{
    /// <summary>Signs an attestation under <c>DOMAIN_BEACON_ATTESTER</c> of its target epoch (phase0/validator.md "Aggregate signature").</summary>
    /// <exception cref="ValidatorSignerException">The signer refused, such as for a slashable vote or an unknown key, or could not be reached.</exception>
    ValueTask<BlsSignature> SignAttestation(BlsPublicKey publicKey, AttestationData data, CancellationToken cancellationToken);

    /// <summary>Signs <paramref name="slot"/> under <c>DOMAIN_SELECTION_PROOF</c> of its epoch: the selection proof that decides whether the validator aggregates (phase0/validator.md "Aggregation selection").</summary>
    /// <exception cref="ValidatorSignerException">The signer refused, such as for an unknown key, or could not be reached.</exception>
    ValueTask<BlsSignature> SignAggregationSlot(BlsPublicKey publicKey, ulong slot, CancellationToken cancellationToken);

    /// <summary>Signs an aggregate and its selection proof under <c>DOMAIN_AGGREGATE_AND_PROOF</c> of the aggregate's epoch (phase0/validator.md "Broadcast aggregate").</summary>
    /// <exception cref="ValidatorSignerException">The signer refused, such as for an unknown key, or could not be reached.</exception>
    ValueTask<BlsSignature> SignAggregateAndProof(BlsPublicKey publicKey, AggregateAndProof aggregateAndProof, CancellationToken cancellationToken);

    /// <summary>Signs <paramref name="beaconBlockRoot"/> under <c>DOMAIN_SYNC_COMMITTEE</c> of the epoch of <paramref name="slot"/>: the sync committee message of the slot (altair/validator.md "Prepare sync committee message").</summary>
    /// <exception cref="ValidatorSignerException">The signer refused, such as for an unknown key, or could not be reached.</exception>
    ValueTask<BlsSignature> SignSyncCommitteeMessage(BlsPublicKey publicKey, ulong slot, Hash256 beaconBlockRoot, CancellationToken cancellationToken);

    /// <summary>
    /// Signs the <c>SyncAggregatorSelectionData</c> of <paramref name="slot"/> and <paramref name="subcommitteeIndex"/> under
    /// <c>DOMAIN_SYNC_COMMITTEE_SELECTION_PROOF</c> of the slot's epoch: the selection proof that decides whether the validator aggregates
    /// its subcommittee (altair/validator.md "Aggregation selection").
    /// </summary>
    /// <exception cref="ValidatorSignerException">The signer refused, such as for an unknown key, or could not be reached.</exception>
    ValueTask<BlsSignature> SignSyncCommitteeSelectionProof(BlsPublicKey publicKey, ulong slot, ulong subcommitteeIndex, CancellationToken cancellationToken);

    /// <summary>Signs a contribution and its selection proof under <c>DOMAIN_CONTRIBUTION_AND_PROOF</c> of the contribution's epoch (altair/validator.md "Broadcast sync committee contribution").</summary>
    /// <exception cref="ValidatorSignerException">The signer refused, such as for an unknown key, or could not be reached.</exception>
    ValueTask<BlsSignature> SignContributionAndProof(BlsPublicKey publicKey, ContributionAndProof contributionAndProof, CancellationToken cancellationToken);

    /// <summary>Signs <paramref name="epoch"/> under <c>DOMAIN_RANDAO</c> of that epoch: the randao reveal of a block proposed in it (phase0/validator.md "Randao reveal").</summary>
    /// <exception cref="ValidatorSignerException">The signer refused, such as for an unknown key, or could not be reached.</exception>
    ValueTask<BlsSignature> SignRandaoReveal(BlsPublicKey publicKey, ulong epoch, CancellationToken cancellationToken);

    /// <summary>Signs a block, given as its header, under <c>DOMAIN_BEACON_PROPOSER</c> of its slot's epoch (phase0/validator.md "Signature").</summary>
    /// <remarks>A header has the root of its block, and is all a signer's slashing protection needs to see of it.</remarks>
    /// <exception cref="ValidatorSignerException">The signer refused, such as for a slashable proposal or an unknown key, or could not be reached.</exception>
    ValueTask<BlsSignature> SignBlock(BlsPublicKey publicKey, BeaconBlockHeader blockHeader, CancellationToken cancellationToken);
}

/// <summary>A signature that was not produced; the duty it was for is skipped.</summary>
/// <param name="refused">Whether the signer answered and declined, as opposed to failing or being unreachable.</param>
public sealed class ValidatorSignerException(string message, bool refused, Exception? innerException) : Exception(message, innerException)
{
    public bool Refused { get; } = refused;
}
