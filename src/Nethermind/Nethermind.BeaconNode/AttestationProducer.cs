using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nethermind.Core2;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode
{
    public class AttestationProducer
    {
        private readonly ILogger _logger;
        private readonly ICryptographyService _cryptographyService;
        private readonly BeaconChainUtility _beaconChainUtility;
        private readonly BeaconStateAccessor _beaconStateAccessor;
        private readonly BeaconStateTransition _beaconStateTransition;
        private readonly IForkChoice _forkChoice;
        private readonly IStore _store;

        public AttestationProducer(ILogger<AttestationProducer> logger,
            ICryptographyService cryptographyService,
            BeaconChainUtility beaconChainUtility,
            BeaconStateAccessor beaconStateAccessor,
            BeaconStateTransition beaconStateTransition,
            IForkChoice forkChoice,
            IStore store)
        {
            _logger = logger;
            _cryptographyService = cryptographyService;
            _beaconChainUtility = beaconChainUtility;
            _beaconStateAccessor = beaconStateAccessor;
            _beaconStateTransition = beaconStateTransition;
            _forkChoice = forkChoice;
            _store = store;
        }

        public async Task<Attestation> NewAttestationAsync(BlsPublicKey validatorPublicKey,
            bool proofOfCustodyBit, Slot slot, Shard shard,
            CancellationToken cancellationToken)
        {
            Root head = await _store.GetHeadAsync().ConfigureAwait(false);
            BeaconBlock headBlock = (await _store.GetSignedBlockAsync(head).ConfigureAwait(false)).Message;
            BeaconState parentState = await _store.GetBlockStateAsync(head).ConfigureAwait(false);
            
            // Clone state (will mutate) and process outstanding slots
            BeaconState headState = BeaconState.Clone(parentState);
            _beaconStateTransition.ProcessSlots(headState, slot);

            // Set attestation_data.index = index where index is the index associated with the validator's committee.
            ValidatorIndex? validatorIndex = FindValidatorIndexByPublicKey(headState, validatorPublicKey);

            if (validatorIndex == null)
            {
                throw new Exception($"Can not find validator index for public key {validatorPublicKey}");
            }

            // TODO: May need a more efficient way that looping through every committee, and then every member,
            // to try and find the committee and position within the committee.
            // Some of this may already be cached in Validator Assignments (generally stable for an epoch),
            // but not with the index within the committee. Easy enough to extend and use the same cache.
            CommitteeIndex? committeeIndex = null;
            int committeeSize = 0;
            int committeeMemberIndexOfValidator = 0;
            ulong committeeCount = _beaconStateAccessor.GetCommitteeCountAtSlot(headState, headState.Slot);
            for (CommitteeIndex index = CommitteeIndex.Zero;
                index < new CommitteeIndex(committeeCount);
                index += CommitteeIndex.One)
            {
                IReadOnlyList<ValidatorIndex> committee =
                    _beaconStateAccessor.GetBeaconCommittee(headState, headState.Slot, index);
                for (int committeeMemberIndex = 0; committeeMemberIndex < committee.Count; committeeMemberIndex++)
                {
                    if (committee[committeeMemberIndex] == validatorIndex)
                    {
                        committeeIndex = index;
                        committeeSize = committee.Count;
                        committeeMemberIndexOfValidator = committeeMemberIndex;
                        break;
                    }
                }
                
                if (committeeIndex != null)
                {
                    break;
                }
            }

            if (committeeIndex == null)
            {
                throw new Exception($"Can not find committee for validator index {committeeIndex}");
            }

            Root beaconBlockRoot = _cryptographyService.HashTreeRoot(headBlock);

            Checkpoint source = headState.CurrentJustifiedCheckpoint;
            
            Epoch currentEpoch = _beaconStateAccessor.GetCurrentEpoch(headState);
            Slot startSlot = _beaconChainUtility.ComputeStartSlotOfEpoch(currentEpoch);
            Root epochBoundaryBlockRoot;
            if (startSlot == headState.Slot)
            {
                epochBoundaryBlockRoot = beaconBlockRoot;
            }
            else
            {
                epochBoundaryBlockRoot = _beaconStateAccessor.GetBlockRootAtSlot(headState, startSlot);
            }
            Checkpoint target = new Checkpoint(currentEpoch, epochBoundaryBlockRoot);
            
            AttestationData attestationData = new AttestationData(slot, committeeIndex.Value, beaconBlockRoot, source, target);

            var aggregationBits = new BitArray(committeeSize);
            aggregationBits.Set(committeeMemberIndexOfValidator, true);
            
            var attestation = new Attestation(aggregationBits, attestationData, BlsSignature.Zero);
            
            return attestation;
        }
        
        // De-duplicate from ValidatorAssignments
        private ValidatorIndex? FindValidatorIndexByPublicKey(BeaconState state, BlsPublicKey validatorPublicKey)
        {
            for (int index = 0; index < state.Validators.Count; index++)
            {
                if (state.Validators[index].PublicKey.Equals(validatorPublicKey))
                {
                    return new ValidatorIndex((ulong) index);
                }
            }

            return ValidatorIndex.None;
        }
    }
}