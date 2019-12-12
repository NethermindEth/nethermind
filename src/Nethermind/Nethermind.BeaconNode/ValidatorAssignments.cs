//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethermind.BeaconNode.Configuration;
using Nethermind.BeaconNode.Containers;
using Nethermind.BeaconNode.Storage;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode
{
    public class ValidatorAssignments
    {
        private readonly ILogger<ValidatorAssignments> _logger;
        private readonly IOptionsMonitor<TimeParameters> _timeParameterOptions;
        private readonly BeaconChainUtility _beaconChainUtility;
        private readonly BeaconStateAccessor _beaconStateAccessor;
        private readonly BeaconStateTransition _beaconStateTransition;
        private readonly ForkChoice _forkChoice;
        private readonly IStoreProvider _storeProvider;

        public ValidatorAssignments(ILogger<ValidatorAssignments> logger,
            IOptionsMonitor<TimeParameters> timeParameterOptions,
            BeaconChainUtility beaconChainUtility,
            BeaconStateAccessor beaconStateAccessor,
            BeaconStateTransition beaconStateTransition,
            ForkChoice forkChoice,
            IStoreProvider storeProvider)
        {
            _logger = logger;
            _timeParameterOptions = timeParameterOptions;
            _beaconChainUtility = beaconChainUtility;
            _beaconStateAccessor = beaconStateAccessor;
            _beaconStateTransition = beaconStateTransition;
            _forkChoice = forkChoice;
            _storeProvider = storeProvider;
        }
        
        public bool CheckIfValidatorActive(BeaconState state, ValidatorIndex validatorIndex)
        {
            if ((int) validatorIndex >= state.Validators.Count) return false;
            
            Validator validator = state.Validators[(int)validatorIndex];
            Epoch currentEpoch = _beaconStateAccessor.GetCurrentEpoch(state);
            bool isActive = _beaconChainUtility.IsActiveValidator(validator, currentEpoch);
            return isActive;
        }

        /// <summary>
        /// Return the committee assignment in the ``epoch`` for ``validator_index``.
        /// ``assignment`` returned is a tuple of the following form:
        /// * ``assignment[0]`` is the list of validators in the committee
        /// * ``assignment[1]`` is the index to which the committee is assigned
        ///     * ``assignment[2]`` is the slot at which the committee is assigned
        ///     Return None if no assignment.
        /// </summary>
        public CommitteeAssignment GetCommitteeAssignment(BeaconState state, Epoch epoch, ValidatorIndex validatorIndex)
        {
            Epoch nextEpoch = _beaconStateAccessor.GetCurrentEpoch(state) + Epoch.One;
            if (epoch > nextEpoch)
            {
                throw new ArgumentOutOfRangeException(nameof(epoch), epoch, $"Committee epoch cannot be greater than next epoch {nextEpoch}.");
            }

            Slot startSlot = _beaconChainUtility.ComputeStartSlotOfEpoch(epoch);
            ulong endSlot = startSlot + _timeParameterOptions.CurrentValue.SlotsPerEpoch;
            for (Slot slot = startSlot; slot < endSlot; slot += Slot.One)
            {
                ulong committeeCount = _beaconStateAccessor.GetCommitteeCountAtSlot(state, slot);
                for (CommitteeIndex index = CommitteeIndex.Zero; index < new CommitteeIndex(committeeCount); index += CommitteeIndex.One)
                {
                    IReadOnlyList<ValidatorIndex> committee = _beaconStateAccessor.GetBeaconCommittee(state, slot, index);
                    if (committee.Contains(validatorIndex))
                    {
                        CommitteeAssignment committeeAssignment = new CommitteeAssignment(committee, index, slot);
                        return committeeAssignment;
                    }
                }
            }

            return CommitteeAssignment.None;
        }
        
        public async Task<ValidatorDuty> GetValidatorDutyAsync(BlsPublicKey validatorPublicKey, Epoch epoch)
        {
            if (!_storeProvider.TryGetStore(out IStore? retrievedStore))
            {
                throw new Exception("Beacon chain is currently syncing or waiting for genesis.");
            }

            IStore store = retrievedStore!;
            Hash32 head = await _forkChoice.GetHeadAsync(store);
            if (!store.TryGetBlockState(head, out BeaconState? headState))
            {
                throw new Exception($"Head state {head} not found.");
            }
            
            Epoch currentEpoch = _beaconStateAccessor.GetCurrentEpoch(headState!);
            Epoch nextEpoch = currentEpoch + Epoch.One;

            if (epoch == Epoch.None)
            {
                epoch = currentEpoch;
            } 
            else if (epoch > nextEpoch)
            {
                throw new ArgumentOutOfRangeException(nameof(epoch), epoch,
                    $"Duties cannot look ahead more than the next epoch {nextEpoch}.");
            }

            Slot startSlot = _beaconChainUtility.ComputeStartSlotOfEpoch(epoch);
            Slot endSlot = startSlot + new Slot(_timeParameterOptions.CurrentValue.SlotsPerEpoch);
            BeaconState state;
            if (epoch == nextEpoch)
            {
                // Clone for next or current, so that it can be safely mutated (transitioned forward)
                state = BeaconState.Clone(headState!);
                _beaconStateTransition.ProcessSlots(state, startSlot);
            }
            else if (epoch == currentEpoch)
            {
                state = BeaconState.Clone(headState!);
            }
            else
            {
                Hash32 endRoot = _forkChoice.GetAncestor(store, head, endSlot);
                if (!store.TryGetBlockState(endRoot, out BeaconState? endState))
                {
                    throw new Exception($"State {endRoot} for slot {endSlot} not found.");
                }
                state = endState!;
            }

            Slot attestationSlot = Slot.None;
            Shard attestationShard = Shard.Zero;
            Slot blockProposalSlot = Slot.None;

            ValidatorIndex validatorIndex = FindValidatorIndexByPublicKey(state, validatorPublicKey);

            // Check state
            (attestationSlot, blockProposalSlot) = CheckStateDuty(state, validatorIndex, attestationSlot, blockProposalSlot);

            // Check historical states
            Slot previousSlot = state.Slot - Slot.One;
            IReadOnlyList<Hash32> blockRoots = state.BlockRoots;
            while (previousSlot >= startSlot && (attestationSlot == Slot.None || blockProposalSlot == Slot.None))
            {
                int index = (int) (previousSlot % _timeParameterOptions.CurrentValue.SlotsPerEpoch);
                Hash32 historicalRoot = blockRoots[index];
                if (!store.TryGetBlockState(historicalRoot, out BeaconState? historicalState))
                {
                    throw new Exception($"Historical state {historicalRoot} for slot {previousSlot} not found.");
                }

                (attestationSlot, blockProposalSlot) =
                    CheckStateDuty(historicalState!, validatorIndex, attestationSlot, blockProposalSlot);

                previousSlot -= Slot.One;
            }
            
            // Check future states
            Slot nextSlot = state.Slot + Slot.One;
            while (nextSlot <= endSlot && (attestationSlot == Slot.None || blockProposalSlot == Slot.None))
            {
                _beaconStateTransition.ProcessSlots(state, nextSlot);
                
                (attestationSlot, blockProposalSlot) =
                    CheckStateDuty(state, validatorIndex, attestationSlot, blockProposalSlot);

                nextSlot += Slot.One;
            }
            
            ValidatorDuty validatorDuty = new ValidatorDuty(validatorPublicKey, attestationSlot, attestationShard, blockProposalSlot);
            return validatorDuty;
        }

        private (Slot attestationSlot, Slot blockProposalSlot) CheckStateDuty(BeaconState state, 
            ValidatorIndex validatorIndex, Slot attestationSlot, Slot blockProposalSlot)
        {
            // check attestation
            if (attestationSlot == Slot.None)
            {
                ulong committeeCount = _beaconStateAccessor.GetCommitteeCountAtSlot(state, state.Slot);
                for (CommitteeIndex index = CommitteeIndex.Zero;
                    index < new CommitteeIndex(committeeCount);
                    index += CommitteeIndex.One)
                {
                    IReadOnlyList<ValidatorIndex> committee =
                        _beaconStateAccessor.GetBeaconCommittee(state, state.Slot, index);
                    if (committee.Contains(validatorIndex))
                    {
                        attestationSlot = state.Slot;
                    }
                }
            }

            // check proposer
            if (blockProposalSlot == Slot.None)
            {
                bool isProposer = IsProposer(state, validatorIndex);
                if (isProposer)
                {
                    blockProposalSlot = state.Slot;
                }
            }

            return (attestationSlot, blockProposalSlot);
        }

        public bool IsProposer(BeaconState state, ValidatorIndex validatorIndex)
        {
            ValidatorIndex stateProposerIndex = _beaconStateAccessor.GetBeaconProposerIndex(state);
            return stateProposerIndex.Equals(validatorIndex);
        }
        
        private ValidatorIndex FindValidatorIndexByPublicKey(BeaconState state, BlsPublicKey validatorPublicKey)
        {
            for (var index = 0; index < state.Validators.Count; index++)
            {
                if (state.Validators[index].PublicKey.Equals(validatorPublicKey))
                {
                    return new ValidatorIndex((ulong)index);
                }
            }
            return ValidatorIndex.None;
        }

    }
}