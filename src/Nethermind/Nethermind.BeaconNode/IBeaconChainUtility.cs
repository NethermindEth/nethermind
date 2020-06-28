using System.Collections.Generic;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode
{
    public interface IBeaconChainUtility
    {
        /// <summary>
        /// Return the epoch during which validator activations and exits initiated in ``epoch`` take effect.
        /// </summary>
        Epoch ComputeActivationExitEpoch(Epoch epoch);

        /// <summary>
        /// Return the committee corresponding to ``indices``, ``seed``, ``index``, and committee ``count``.
        /// </summary>
        IReadOnlyList<ValidatorIndex> ComputeCommittee(IList<ValidatorIndex> indices, Bytes32 seed, ulong index,
            ulong count);

        /// <summary>
        /// Returns the domain for the 'domain_type' and 'fork_version'
        /// </summary>
        Domain ComputeDomain(DomainType domainType, ForkVersion? forkVersion = null);

        /// <summary>
        /// Return the epoch number of ``slot``.
        /// </summary>
        Epoch ComputeEpochAtSlot(Slot slot);

        /// <summary>
        /// Return from ``indices`` a random index sampled by effective balance.
        /// </summary>
        ValidatorIndex ComputeProposerIndex(BeaconState state, IList<ValidatorIndex> indices, Bytes32 seed);

        /// <summary>
        /// Return the shuffled validator index corresponding to ``seed`` (and ``index_count``).
        /// </summary>
        ValidatorIndex ComputeShuffledIndex(ValidatorIndex index, ulong indexCount, Bytes32 seed);

        /// <summary>
        /// Return the signing root of an object by calculating the root of the object-domain tree.
        /// </summary>
        Root ComputeSigningRoot(Root objectRoot, Domain domain);

        /// <summary>
        /// Return the start slot of 'epoch'
        /// </summary>
        Slot ComputeStartSlotOfEpoch(Epoch epoch);

        /// <summary>
        /// Check if ``validator`` is active.
        /// </summary>
        bool IsActiveValidator(Validator validator, Epoch epoch);

        /// <summary>
        /// Check if ``validator`` is eligible for activation.
        /// </summary>
        bool IsEligibleForActivation(BeaconState state, Validator validator);

        /// <summary>
        /// Check if ``validator`` is eligible to be placed into the activation queue.
        /// </summary>
        bool IsEligibleForActivationQueue(Validator validator);

        /// <summary>
        /// Check if ``data_1`` and ``data_2`` are slashable according to Casper FFG rules.
        /// </summary>
        bool IsSlashableAttestationData(AttestationData data1, AttestationData data2);

        /// <summary>
        /// Check if ``validator`` is slashable.
        /// </summary>
        bool IsSlashableValidator(Validator validator, Epoch epoch);

        /// <summary>
        /// Check if ``indexed_attestation`` has valid indices and signature.
        /// </summary>
        bool IsValidIndexedAttestation(BeaconState state, IndexedAttestation indexedAttestation, Domain domain);
    }
}