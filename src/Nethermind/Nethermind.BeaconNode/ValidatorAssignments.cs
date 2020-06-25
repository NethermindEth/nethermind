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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethermind.Core2;
using Nethermind.Core2.Api;
using Nethermind.Core2.Configuration;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Nethermind.Logging.Microsoft;

namespace Nethermind.BeaconNode
{
    public class ValidatorAssignments
    {
        private readonly IBeaconChainUtility _beaconChainUtility;
        private readonly BeaconStateAccessor _beaconStateAccessor;
        private readonly BeaconStateTransition _beaconStateTransition;
        private readonly IForkChoice _forkChoice;
        private readonly ILogger<ValidatorAssignments> _logger;
        private readonly IStore _store;
        private readonly IOptionsMonitor<TimeParameters> _timeParameterOptions;
        private readonly ValidatorAssignmentsCache _validatorAssignmentsCache;

        public ValidatorAssignments(ILogger<ValidatorAssignments> logger,
            IOptionsMonitor<TimeParameters> timeParameterOptions,
            IBeaconChainUtility beaconChainUtility,
            BeaconStateAccessor beaconStateAccessor,
            BeaconStateTransition beaconStateTransition,
            IForkChoice forkChoice,
            IStore store,
            ValidatorAssignmentsCache validatorAssignmentsCache)
        {
            _logger = logger;
            _timeParameterOptions = timeParameterOptions;
            _beaconChainUtility = beaconChainUtility;
            _beaconStateAccessor = beaconStateAccessor;
            _beaconStateTransition = beaconStateTransition;
            _forkChoice = forkChoice;
            _store = store;
            _validatorAssignmentsCache = validatorAssignmentsCache;
        }

        public bool CheckIfValidatorActive(BeaconState state, ValidatorIndex validatorIndex)
        {
            if ((int) validatorIndex >= state.Validators.Count)
            {
                return false;
            }

            Validator validator = state.Validators[(int) validatorIndex];
            Epoch currentEpoch = _beaconStateAccessor.GetCurrentEpoch(state);
            bool isActive = _beaconChainUtility.IsActiveValidator(validator, currentEpoch);
            return isActive;
        }

        /// <summary>
        ///     Return the committee assignment in the ``epoch`` for ``validator_index``.
        ///     ``assignment`` returned is a tuple of the following form:
        ///     * ``assignment[0]`` is the list of validators in the committee
        ///     * ``assignment[1]`` is the index to which the committee is assigned
        ///     * ``assignment[2]`` is the slot at which the committee is assigned
        ///     Return None if no assignment.
        /// </summary>
        public CommitteeAssignment GetCommitteeAssignment(BeaconState state, Epoch epoch, ValidatorIndex validatorIndex)
        {
            Epoch nextEpoch = _beaconStateAccessor.GetCurrentEpoch(state) + Epoch.One;
            if (epoch > nextEpoch)
            {
                throw new ArgumentOutOfRangeException(nameof(epoch), epoch,
                    $"Committee epoch cannot be greater than next epoch {nextEpoch}.");
            }

            TimeParameters timeParameters = _timeParameterOptions.CurrentValue;
            Slot startSlot = _beaconChainUtility.ComputeStartSlotOfEpoch(epoch);
            ulong endSlot = startSlot + timeParameters.SlotsPerEpoch;
            for (Slot slot = startSlot; slot < endSlot; slot += Slot.One)
            {
                ulong committeeCount = _beaconStateAccessor.GetCommitteeCountAtSlot(state, slot);
                for (CommitteeIndex index = CommitteeIndex.Zero;
                    index < new CommitteeIndex(committeeCount);
                    index += CommitteeIndex.One)
                {
                    IReadOnlyList<ValidatorIndex> committee =
                        _beaconStateAccessor.GetBeaconCommittee(state, slot, index);
                    if (committee.Contains(validatorIndex))
                    {
                        CommitteeAssignment committeeAssignment = new CommitteeAssignment(committee, index, slot);
                        return committeeAssignment;
                    }
                }
            }

            return CommitteeAssignment.None;
        }

        public async Task<IList<ValidatorDuty>> GetValidatorDutiesAsync(
            IList<BlsPublicKey> validatorPublicKeys,
            Epoch? optionalEpoch)
        {
            Root head = await _forkChoice.GetHeadAsync(_store).ConfigureAwait(false);
            BeaconState headState = await _store.GetBlockStateAsync(head).ConfigureAwait(false);

            Epoch currentEpoch = _beaconStateAccessor.GetCurrentEpoch(headState);
            Epoch epoch = optionalEpoch ?? currentEpoch;

            Epoch nextEpoch = currentEpoch + Epoch.One;
            if (epoch > nextEpoch)
            {
                throw new ArgumentOutOfRangeException(nameof(epoch), epoch,
                    $"Duties cannot look ahead more than the next epoch {nextEpoch}.");
            }

            Slot startSlot = _beaconChainUtility.ComputeStartSlotOfEpoch(epoch);
            Root epochStartRoot = await _store.GetAncestorAsync(head, startSlot);
            TimeParameters timeParameters = _timeParameterOptions.CurrentValue;

            (Root epochStartRoot, Epoch epoch) cacheKey = (epochStartRoot, epoch);
            ConcurrentDictionary<BlsPublicKey, ValidatorDuty> dutiesForEpoch =
                await _validatorAssignmentsCache.Cache.GetOrCreateAsync(cacheKey, entry =>
                {
                    entry.SlidingExpiration = TimeSpan.FromSeconds(2 * timeParameters.SecondsPerSlot);
                    return Task.FromResult(new ConcurrentDictionary<BlsPublicKey, ValidatorDuty>());
                }).ConfigureAwait(false);

            IEnumerable<BlsPublicKey> missingValidators = validatorPublicKeys.Except(dutiesForEpoch.Keys);
            if (missingValidators.Any())
            {
                if (_logger.IsDebug())
                    LogDebug.GettingMissingValidatorDutiesForCache(_logger, missingValidators.Count(), epoch,
                        epochStartRoot,
                        null);

                BeaconState storedState = await _store.GetBlockStateAsync(epochStartRoot);

                // Clone, so that it can be safely mutated (transitioned forward)
                BeaconState state = BeaconState.Clone(storedState);

                // Transition to start slot, of target epoch (may have been a skip slot, i.e. stored state root may have been older)
                _beaconStateTransition.ProcessSlots(state, startSlot);

                // Check validators are valid (if not duties are empty).
                IList<DutyDetails> dutyDetailsList = new List<DutyDetails>();
                foreach (BlsPublicKey validatorPublicKey in missingValidators)
                {
                    ValidatorIndex? validatorIndex = FindValidatorIndexByPublicKey(state, validatorPublicKey);

                    if (validatorIndex.HasValue)
                    {
                        bool validatorActive = CheckIfValidatorActive(state, validatorIndex.Value);
                        if (validatorActive)
                        {
                            dutyDetailsList.Add(new DutyDetails(validatorPublicKey, validatorIndex.Value));
                        }
                        else
                        {
                            if (_logger.IsWarn())
                                Log.ValidatorNotActiveAtEpoch(_logger, epoch, validatorIndex.Value, validatorPublicKey,
                                    null);
                            dutiesForEpoch[validatorPublicKey] =
                                new ValidatorDuty(validatorPublicKey, Slot.None, CommitteeIndex.None, Slot.None);
                        }
                    }
                    else
                    {
                        if (_logger.IsWarn())
                            Log.ValidatorNotFoundAtEpoch(_logger, epoch, validatorPublicKey, null);
                        dutiesForEpoch[validatorPublicKey] =
                            new ValidatorDuty(validatorPublicKey, Slot.None, CommitteeIndex.None, Slot.None);
                    }
                }

                if (dutyDetailsList.Any())
                {
                    // Check starting state
                    UpdateDutyDetailsForState(dutyDetailsList, state);

                    // Check other slots in epoch, if needed
                    Slot endSlotExclusive = startSlot + new Slot(timeParameters.SlotsPerEpoch);
                    Slot slotToCheck = startSlot + Slot.One;
                    while (slotToCheck < endSlotExclusive)
                    {
                        _beaconStateTransition.ProcessSlots(state, slotToCheck);
                        UpdateDutyDetailsForState(dutyDetailsList, state);
                        slotToCheck += Slot.One;
                    }

                    // Active validators should always have attestation slots; warn if they don't
                    foreach (var dutyDetails in dutyDetailsList)
                    {
                        if (!dutyDetails.AttestationSlot.HasValue)
                        {
                            if (_logger.IsWarn())
                                Log.ValidatorDoesNotHaveAttestationSlot(_logger, epoch, dutyDetails.ValidatorPublicKey,
                                    null);
                        }
                    }

                    // Add to cached dictionary
                    foreach (var dutyDetails in dutyDetailsList)
                    {
                        ValidatorDuty validatorDuty =
                            new ValidatorDuty(dutyDetails.ValidatorPublicKey,
                                dutyDetails.AttestationSlot,
                                dutyDetails.AttestationCommitteeIndex,
                                dutyDetails.BlockProposalSlot);
                        dutiesForEpoch[dutyDetails.ValidatorPublicKey] = validatorDuty;
                    }
                }
            }

            return dutiesForEpoch
                .Where(x => validatorPublicKeys.Contains(x.Key))
                .Select(x => x.Value)
                .ToList();
        }

        public async Task<ValidatorDuty> GetValidatorDutyAsync(BlsPublicKey validatorPublicKey, Epoch? optionalEpoch)
        {
            // TODO: Obsolete this and remove it; only used in tests
            var validatorDuties = await GetValidatorDutiesAsync(new[] {validatorPublicKey}, optionalEpoch);
            return validatorDuties.FirstOrDefault();
        }

        public bool IsProposer(BeaconState state, ValidatorIndex validatorIndex)
        {
            ValidatorIndex stateProposerIndex = _beaconStateAccessor.GetBeaconProposerIndex(state);
            return stateProposerIndex.Equals(validatorIndex);
        }

        private class DutyDetails
        {
            public DutyDetails(BlsPublicKey validatorPublicKey, ValidatorIndex validatorIndex)
            {
                ValidatorPublicKey = validatorPublicKey;
                ValidatorIndex = validatorIndex;
            }

            public CommitteeIndex? AttestationCommitteeIndex { get; set; }
            public Slot? AttestationSlot { get; set; }
            public Slot? BlockProposalSlot { get; set; }
            public ValidatorIndex ValidatorIndex { get; }

            public BlsPublicKey ValidatorPublicKey { get; }
        }

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

        private void UpdateDutyDetailsForState(IList<DutyDetails> dutyDetailsList, BeaconState state)
        {
            // check attestation
            ulong committeeCount = _beaconStateAccessor.GetCommitteeCountAtSlot(state, state.Slot);
            for (CommitteeIndex index = CommitteeIndex.Zero;
                index < new CommitteeIndex(committeeCount);
                index += CommitteeIndex.One)
            {
                IReadOnlyList<ValidatorIndex> committee =
                    _beaconStateAccessor.GetBeaconCommittee(state, state.Slot, index);
                foreach (DutyDetails dutyDetails in dutyDetailsList)
                {
                    if (!dutyDetails.AttestationSlot.HasValue && committee.Contains(dutyDetails.ValidatorIndex))
                    {
                        dutyDetails.AttestationSlot = state.Slot;
                        dutyDetails.AttestationCommitteeIndex = index;
                    }
                }
            }

            // check proposer
            foreach (DutyDetails dutyDetails in dutyDetailsList)
            {
                if (!dutyDetails.BlockProposalSlot.HasValue)
                {
                    bool isProposer = IsProposer(state, dutyDetails.ValidatorIndex);
                    if (isProposer)
                    {
                        dutyDetails.BlockProposalSlot = state.Slot;
                    }
                }
            }
        }
    }
}