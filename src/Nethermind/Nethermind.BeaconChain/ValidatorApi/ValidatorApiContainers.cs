// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.BeaconChain.ValidatorApi;

/// <summary>Beacon API <c>GET /eth/v1/node/syncing</c>.</summary>
/// <param name="HeadSlot">The slot of the node's current head.</param>
/// <param name="SyncDistance">How many slots the head is behind the wall-clock slot.</param>
/// <param name="IsSyncing">Whether the node is still catching up; duties must not be performed while it is.</param>
/// <param name="IsOptimistic">Whether the head's execution payload is not yet verified (bellatrix/optimistic-sync.md); duties must not be performed while it is.</param>
/// <param name="ExecutionLayerOffline">Whether the execution layer cannot be reached.</param>
public sealed record SyncingStatus(ulong HeadSlot, ulong SyncDistance, bool IsSyncing, bool IsOptimistic, bool ExecutionLayerOffline);

/// <summary>A validator in the head state, as needed to map a public key to its registry index.</summary>
/// <param name="ActivationEpoch"><c>FAR_FUTURE_EPOCH</c> while the validator is not yet scheduled for activation.</param>
/// <param name="ExitEpoch"><c>FAR_FUTURE_EPOCH</c> while the validator has not initiated an exit.</param>
public sealed record ValidatorSummary(BlsPublicKey PublicKey, ulong ValidatorIndex, ulong ActivationEpoch, ulong ExitEpoch, bool Slashed);

/// <summary>The duties of one epoch together with what they depend on.</summary>
/// <param name="DependentRoot">
/// The block root that decided these duties; a head whose chain has a different root at that position has different duties.
/// <c>null</c> where the Beacon API defines none (sync-committee duties).
/// </param>
/// <param name="ExecutionOptimistic">Whether the duties were read from a head whose execution payload is not yet verified.</param>
public sealed record DutiesResult<TDuty>(Hash256? DependentRoot, bool ExecutionOptimistic, IReadOnlyList<TDuty> Duties);

/// <summary>Beacon API <c>AttesterDuty</c>.</summary>
/// <param name="CommitteeIndex">The index of the committee within its slot.</param>
/// <param name="CommitteeLength">The number of validators in that committee.</param>
/// <param name="CommitteesAtSlot">The number of committees in <paramref name="Slot"/>, needed by <c>compute_subnet_for_attestation</c>.</param>
/// <param name="ValidatorCommitteeIndex">The validator's position within the committee.</param>
public sealed record AttesterDuty(
    BlsPublicKey PublicKey,
    ulong ValidatorIndex,
    ulong CommitteeIndex,
    ulong CommitteeLength,
    ulong CommitteesAtSlot,
    ulong ValidatorCommitteeIndex,
    ulong Slot);

/// <summary>Beacon API <c>ProposerDuty</c>.</summary>
public sealed record ProposerDuty(BlsPublicKey PublicKey, ulong ValidatorIndex, ulong Slot);

/// <summary>Beacon API <c>SyncDuty</c>.</summary>
/// <param name="ValidatorSyncCommitteeIndices">Every position the validator holds in the sync committee; a validator can hold several.</param>
public sealed record SyncDuty(BlsPublicKey PublicKey, ulong ValidatorIndex, IReadOnlyList<ulong> ValidatorSyncCommitteeIndices);

/// <summary>Beacon API <c>BeaconCommitteeSubscription</c>: asks the node to be ready on a committee's attestation subnet for one slot.</summary>
/// <param name="IsAggregator">Whether the node must also collect that committee's attestations, to serve <see cref="IBeaconNodeValidatorApi.GetAggregateAttestation"/>.</param>
public sealed record BeaconCommitteeSubscription(ulong ValidatorIndex, ulong CommitteeIndex, ulong CommitteesAtSlot, ulong Slot, bool IsAggregator);

/// <summary>Beacon API <c>SyncCommitteeSubscription</c>.</summary>
/// <param name="UntilEpoch">The first epoch the subscription no longer covers.</param>
public sealed record SyncCommitteeSubscription(ulong ValidatorIndex, IReadOnlyList<ulong> SyncCommitteeIndices, ulong UntilEpoch);

/// <summary>Beacon API <c>prepare_beacon_proposer</c> entry: where a validator's execution rewards go when it proposes.</summary>
public sealed record ProposerPreparation(ulong ValidatorIndex, Address FeeRecipient);

/// <summary>Beacon API <c>BlockContents</c>: an unsigned block and the blob data of its execution payload.</summary>
/// <param name="KzgProofs">The cell proofs of <paramref name="Blobs"/> (EIP-7594), <c>CELLS_PER_EXT_BLOB</c> for each blob.</param>
/// <param name="ExecutionPayloadValue">The payload's value to the fee recipient, in Wei.</param>
/// <param name="ConsensusBlockValue">The block's consensus-layer rewards to the proposer, in Wei.</param>
public sealed record ProducedBlock(
    BeaconBlock Block,
    IReadOnlyList<byte[]> Blobs,
    IReadOnlyList<byte[]> KzgProofs,
    UInt256 ExecutionPayloadValue,
    UInt256 ConsensusBlockValue);

/// <summary>Beacon API <c>SignedBlockContents</c>: a signed block and the blob data its data column sidecars are built from.</summary>
public sealed record SignedBlockContents(SignedBeaconBlock SignedBlock, IReadOnlyList<byte[]> Blobs, IReadOnlyList<byte[]> KzgProofs);

/// <summary>One item of a submitted batch that the node refused.</summary>
/// <param name="Index">The item's position in the submitted list.</param>
public sealed record SubmissionFailure(int Index, string Message);

/// <summary>Why a <see cref="IBeaconNodeValidatorApi"/> call could not be served; each maps to one Beacon API status code.</summary>
public enum BeaconNodeApiError
{
    /// <summary>The request itself is wrong, such as an epoch too far ahead (400).</summary>
    InvalidRequest,

    /// <summary>What was asked for is not known to the node (404).</summary>
    NotFound,

    /// <summary>The node does not serve this call yet (501).</summary>
    NotSupported,

    /// <summary>The node is syncing or its head is optimistic, so it must not hand out duty data (503).</summary>
    NotReady,
}

/// <summary>A refusal of a <see cref="IBeaconNodeValidatorApi"/> call that the caller can act on.</summary>
public sealed class BeaconNodeApiException(BeaconNodeApiError error, string message) : Exception(message)
{
    public BeaconNodeApiError Error { get; } = error;
}
