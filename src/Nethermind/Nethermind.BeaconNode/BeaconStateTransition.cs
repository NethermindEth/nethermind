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
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethermind.Core2;
using Nethermind.Core2.Configuration;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Nethermind.Logging.Microsoft;

namespace Nethermind.BeaconNode
{
    public class BeaconStateTransition
    {
        private readonly IBeaconChainUtility _beaconChainUtility;
        private readonly BeaconStateAccessor _beaconStateAccessor;
        private readonly BeaconStateMutator _beaconStateMutator;
        private readonly IDepositStore _depositStore;
        private readonly ChainConstants _chainConstants;
        private readonly ICryptographyService _cryptographyService;
        private readonly IOptionsMonitor<GweiValues> _gweiValueOptions;
        private readonly ILogger _logger;
        private readonly IOptionsMonitor<MaxOperationsPerBlock> _maxOperationsPerBlockOptions;
        private readonly IOptionsMonitor<SignatureDomains> _signatureDomainOptions;
        private readonly IOptionsMonitor<RewardsAndPenalties> _rewardsAndPenaltiesOptions;
        private readonly IOptionsMonitor<StateListLengths> _stateListLengthOptions;
        private readonly IOptionsMonitor<TimeParameters> _timeParameterOptions;

        public BeaconStateTransition(ILogger<BeaconStateTransition> logger,
            ChainConstants chainConstants,
            IOptionsMonitor<GweiValues> gweiValueOptions,
            IOptionsMonitor<TimeParameters> timeParameterOptions,
            IOptionsMonitor<StateListLengths> stateListLengthOptions,
            IOptionsMonitor<RewardsAndPenalties> rewardsAndPenaltiesOptions,
            IOptionsMonitor<MaxOperationsPerBlock> maxOperationsPerBlockOptions,
            IOptionsMonitor<SignatureDomains> signatureDomainOptions,
            ICryptographyService cryptographyService,
            IBeaconChainUtility beaconChainUtility,
            BeaconStateAccessor beaconStateAccessor,
            BeaconStateMutator beaconStateMutator,
            IDepositStore depositStore)
        {
            _logger = logger;
            _chainConstants = chainConstants;
            _gweiValueOptions = gweiValueOptions;
            _timeParameterOptions = timeParameterOptions;
            _stateListLengthOptions = stateListLengthOptions;
            _rewardsAndPenaltiesOptions = rewardsAndPenaltiesOptions;
            _maxOperationsPerBlockOptions = maxOperationsPerBlockOptions;
            _signatureDomainOptions = signatureDomainOptions;
            _cryptographyService = cryptographyService;
            _beaconChainUtility = beaconChainUtility;
            _beaconStateAccessor = beaconStateAccessor;
            _beaconStateMutator = beaconStateMutator;
            _depositStore = depositStore;
        }

        public (IList<Gwei> rewards, IList<Gwei> penalties) GetAttestationDeltas(BeaconState state)
        {
            RewardsAndPenalties rewardsAndPenalties = _rewardsAndPenaltiesOptions.CurrentValue;

            Epoch previousEpoch = _beaconStateAccessor.GetPreviousEpoch(state);
            Gwei totalBalance = _beaconStateAccessor.GetTotalActiveBalance(state);
            int validatorCount = state.Validators.Count;
            List<Gwei> rewards = Enumerable.Repeat(Gwei.Zero, validatorCount).ToList();
            List<Gwei> penalties = Enumerable.Repeat(Gwei.Zero, validatorCount).ToList();
            List<ValidatorIndex> eligibleValidatorIndices = new List<ValidatorIndex>();
            for (int index = 0; index < validatorCount; index++)
            {
                Validator validator = state.Validators[index];
                bool isActive = _beaconChainUtility.IsActiveValidator(validator, previousEpoch);
                if (isActive
                    || (validator.IsSlashed && previousEpoch + new Epoch(1) < validator.WithdrawableEpoch))
                {
                    eligibleValidatorIndices.Add(new ValidatorIndex((ulong)index));
                }
            }

            // Micro-incentives for matching FFG source, FFG target, and head
            IEnumerable<PendingAttestation> matchingSourceAttestations = GetMatchingSourceAttestations(state, previousEpoch);
            IEnumerable<PendingAttestation> matchingTargetAttestations = GetMatchingTargetAttestations(state, previousEpoch);
            IEnumerable<PendingAttestation> matchingHeadAttestations = GetMatchingHeadAttestations(state, previousEpoch);
            IEnumerable<PendingAttestation>[] attestationSets = new[] { matchingSourceAttestations, matchingTargetAttestations, matchingHeadAttestations };
            string[] setNames = new[] { "Source", "Target", "Head" };
            int setIndex = 0;
            foreach (IEnumerable<PendingAttestation> attestationSet in attestationSets)
            {
                IEnumerable<ValidatorIndex> unslashedAttestingIndices = GetUnslashedAttestingIndices(state, attestationSet);
                Gwei attestingBalance = _beaconStateAccessor.GetTotalBalance(state, unslashedAttestingIndices);
                foreach (ValidatorIndex index in eligibleValidatorIndices)
                {
                    if (unslashedAttestingIndices.Contains(index))
                    {
                        Gwei reward = GetBaseReward(state, index) * attestingBalance / totalBalance;
                        if(_logger.IsDebug()) LogDebug.RewardForValidator(_logger, index, "matching " + setNames[setIndex], reward, null);
                        rewards[(int)index] += reward;
                    }
                    else
                    {
                        Gwei penalty = GetBaseReward(state, index);
                        if(_logger.IsDebug()) LogDebug.PenaltyForValidator(_logger, index, "non-matching " + setNames[setIndex], penalty, null);
                        penalties[(int)index] += penalty;
                    }
                }
                setIndex++;
            }

            // Proposer and inclusion delay micro-rewards
            IEnumerable<ValidatorIndex> unslashedSourceAttestingIndices = GetUnslashedAttestingIndices(state, matchingSourceAttestations);
            foreach (ValidatorIndex index in unslashedSourceAttestingIndices)
            {
                PendingAttestation attestation = matchingSourceAttestations
                    .Where(x =>
                    {
                        IEnumerable<ValidatorIndex> attestingIndices = _beaconStateAccessor.GetAttestingIndices(state, x.Data, x.AggregationBits);
                        return attestingIndices.Contains(index);
                    })
                    .OrderBy(x => x.InclusionDelay)
                    .First();

                Gwei baseReward = GetBaseReward(state, index);
                Gwei proposerReward = baseReward / rewardsAndPenalties.ProposerRewardQuotient;
                if(_logger.IsDebug()) LogDebug.RewardForValidator(_logger, attestation.ProposerIndex, "proposer", proposerReward, null);
                rewards[(int)attestation.ProposerIndex] += proposerReward;

                Gwei maxAttesterReward = baseReward - proposerReward;
                Gwei attesterReward = maxAttesterReward / attestation.InclusionDelay;
                if(_logger.IsDebug()) LogDebug.RewardForValidator(_logger, attestation.ProposerIndex, "attester inclusion delay", proposerReward, null);
                rewards[(int)index] += attesterReward;
            }

            // Inactivity penalty
            Epoch finalityDelay = previousEpoch - state.FinalizedCheckpoint.Epoch;
            if (finalityDelay > _timeParameterOptions.CurrentValue.MinimumEpochsToInactivityPenalty)
            {
                IEnumerable<ValidatorIndex> matchingTargetAttestingIndices = GetUnslashedAttestingIndices(state, matchingTargetAttestations);
                foreach (ValidatorIndex index in eligibleValidatorIndices)
                {
                    Gwei delayPenalty = GetBaseReward(state, index) * _chainConstants.BaseRewardsPerEpoch;
                    if(_logger.IsDebug()) LogDebug.PenaltyForValidator(_logger, index, "finality delay", delayPenalty, null);
                    penalties[(int)index] += delayPenalty;

                    if (!matchingTargetAttestingIndices.Contains(index))
                    {
                        Gwei effectiveBalance = state.Validators[(int)index].EffectiveBalance;
                        Gwei additionalInactivityPenalty = (effectiveBalance * finalityDelay) / rewardsAndPenalties.InactivityPenaltyQuotient;
                        if(_logger.IsDebug()) LogDebug.PenaltyForValidator(_logger, index, "inactivity", additionalInactivityPenalty, null);
                        penalties[(int)index] += additionalInactivityPenalty;
                    }
                }
            }

            return (rewards, penalties);
        }

        public Gwei GetAttestingBalance(BeaconState state, IEnumerable<PendingAttestation> attestations)
        {
            IEnumerable<ValidatorIndex> unslashed = GetUnslashedAttestingIndices(state, attestations);
            return _beaconStateAccessor.GetTotalBalance(state, unslashed);
        }

        public Gwei GetBaseReward(BeaconState state, ValidatorIndex index)
        {
            Gwei totalBalance = _beaconStateAccessor.GetTotalActiveBalance(state);
            Gwei effectiveBalance = state.Validators[(int)index].EffectiveBalance;
            Gwei squareRootBalance = totalBalance.IntegerSquareRoot();
            Gwei baseReward = effectiveBalance * _rewardsAndPenaltiesOptions.CurrentValue.BaseRewardFactor
                / squareRootBalance / _chainConstants.BaseRewardsPerEpoch;
            return baseReward;
        }

        public IReadOnlyList<PendingAttestation> GetMatchingHeadAttestations(BeaconState state, Epoch epoch)
        {
            IEnumerable<PendingAttestation> sourceAttestations = GetMatchingSourceAttestations(state, epoch);
            return sourceAttestations.Where(x =>
                {
                    Root blockRootAtSlot = _beaconStateAccessor.GetBlockRootAtSlot(state, x.Data.Slot);
                    return x.Data.BeaconBlockRoot.Equals(blockRootAtSlot);
                })
                .ToList()
                .AsReadOnly();
        }

        public IReadOnlyList<PendingAttestation> GetMatchingSourceAttestations(BeaconState state, Epoch epoch)
        {
            Epoch currentEpoch = _beaconStateAccessor.GetCurrentEpoch(state);
            Epoch previousEpoch = _beaconStateAccessor.GetPreviousEpoch(state);
            if (epoch != currentEpoch && epoch != previousEpoch)
            {
                throw new ArgumentOutOfRangeException(nameof(epoch), epoch, $"The epoch for attestations must be either the current epoch {currentEpoch} or previous epoch {previousEpoch}.");
            }

            if (epoch == currentEpoch)
            {
                return state.CurrentEpochAttestations;
            }
            return state.PreviousEpochAttestations;
        }

        public IReadOnlyList<PendingAttestation> GetMatchingTargetAttestations(BeaconState state, Epoch epoch)
        {
            Root blockRoot = _beaconStateAccessor.GetBlockRoot(state, epoch);
            IEnumerable<PendingAttestation> sourceAttestations = GetMatchingSourceAttestations(state, epoch);
            return sourceAttestations.Where(x => x.Data.Target.Root.Equals(blockRoot))
                .ToList()
                .AsReadOnly();
        }

        public IReadOnlyList<ValidatorIndex> GetUnslashedAttestingIndices(BeaconState state, IEnumerable<PendingAttestation> attestations)
        {
            IEnumerable<ValidatorIndex> output = new List<ValidatorIndex>();
            foreach (PendingAttestation attestation in attestations)
            {
                IEnumerable<ValidatorIndex> attestingIndices = _beaconStateAccessor.GetAttestingIndices(state, attestation.Data, attestation.AggregationBits);
                output = output.Union(attestingIndices);
            }
            return output.Where(x => !state.Validators[(int)x].IsSlashed)
                .ToList()
                .AsReadOnly();
        }

        public void ProcessAttestation(BeaconState state, Attestation attestation)
        {
            if(_logger.IsDebug()) LogDebug.ProcessAttestation(_logger, attestation, null);

            TimeParameters timeParameters = _timeParameterOptions.CurrentValue;
            AttestationData data = attestation.Data;

            ulong committeeCount = _beaconStateAccessor.GetCommitteeCountAtSlot(state, data.Slot);
            if ((ulong)data.Index >= committeeCount)
            {
                throw new ArgumentOutOfRangeException("attestation.Data.Index", data.Index, $"Attestation data committee index must be less that the committee count {committeeCount}.");
            }
            
            Epoch previousEpoch = _beaconStateAccessor.GetPreviousEpoch(state);
            Epoch currentEpoch = _beaconStateAccessor.GetCurrentEpoch(state);
            if (data.Target.Epoch != previousEpoch && data.Target.Epoch != currentEpoch)
            {
                throw new ArgumentOutOfRangeException("attestation.Data.Target.Epoch", data.Target.Epoch, $"Attestation data target epoch must be either the previous epoch {previousEpoch} or the current epoch {currentEpoch}.");
            }

            Epoch dataSlotEpoch = _beaconChainUtility.ComputeEpochAtSlot(data.Slot);
            if (data.Target.Epoch != dataSlotEpoch)
            {
                throw new ArgumentOutOfRangeException("attestation.Data.Target.Epoch", data.Target.Epoch, $"Attestation data target epoch must match the attestation data slot {data.Slot} (epoch {dataSlotEpoch}).");
            }

            Slot minimumSlot = data.Slot + timeParameters.MinimumAttestationInclusionDelay;
            Slot maximumSlot = (Slot)(data.Slot + timeParameters.SlotsPerEpoch);
            if (state.Slot < minimumSlot)
            {
                throw new Exception($"Current state slot {state.Slot} must be equal or greater than the attestation slot {data.Slot} plus minimum delay {timeParameters.MinimumAttestationInclusionDelay}.");
            }
            if (state.Slot > maximumSlot)
            {
                throw new Exception($"Current state slot {state.Slot} must be equal or less than the attestation slot {data.Slot} plus slots per epoch {timeParameters.SlotsPerEpoch}.");
            }

            IReadOnlyList<ValidatorIndex> committee = _beaconStateAccessor.GetBeaconCommittee(state, data.Slot, data.Index);
            if (attestation.AggregationBits.Count != committee.Count)
            {
                throw new Exception($"The attestation aggregation bit (and custody bit) length {attestation.AggregationBits.Count} must be the same as the committee length {committee.Count}.");
            }

            Slot inclusionDelay = state.Slot - data.Slot;
            ValidatorIndex proposerIndex = _beaconStateAccessor.GetBeaconProposerIndex(state);
            PendingAttestation pendingAttestation = new PendingAttestation(attestation.AggregationBits, data, inclusionDelay, proposerIndex);

            if (data.Target.Epoch == currentEpoch)
            {
                if (!data.Source.Equals(state.CurrentJustifiedCheckpoint))
                {
                    throw new Exception($"For a current epoch target attestation the data source checkpoint {data.Source} must be the same as the current justified checkpoint {state.CurrentJustifiedCheckpoint}.");
                }
                state.AddCurrentAttestation(pendingAttestation);
            }
            else
            {
                if (!data.Source.Equals(state.PreviousJustifiedCheckpoint))
                {
                    throw new Exception($"For a previous epoch target attestation the data source checkpoint {data.Source} must be the same as the previous justified checkpoint {state.PreviousJustifiedCheckpoint}.");
                }
                state.AddPreviousAttestation(pendingAttestation);
            }

            // Verify signature
            IndexedAttestation indexedAttestation = _beaconStateAccessor.GetIndexedAttestation(state, attestation);
            Domain domain = _beaconStateAccessor.GetDomain(state, _signatureDomainOptions.CurrentValue.BeaconAttester, data.Target.Epoch);
            bool isValid = _beaconChainUtility.IsValidIndexedAttestation(state, indexedAttestation, domain);
            if (!isValid)
            {
                throw new Exception($"Indexed attestation {indexedAttestation} is not valid.");
            }
        }

        public void ProcessAttesterSlashing(BeaconState state, AttesterSlashing attesterSlashing)
        {
            if(_logger.IsDebug()) LogDebug.ProcessAttesterSlashing(_logger, attesterSlashing, null);
            IndexedAttestation attestation1 = attesterSlashing.Attestation1;
            IndexedAttestation attestation2 = attesterSlashing.Attestation2;

            bool isSlashableAttestationData = _beaconChainUtility.IsSlashableAttestationData(attestation1.Data, attestation2.Data);
            if (!isSlashableAttestationData)
            {
                throw new Exception("Attestation data must be slashable.");
            }

            SignatureDomains signatureDomains = _signatureDomainOptions.CurrentValue;

            Epoch epoch1 = attestation1.Data.Target.Epoch;
            Domain domain1 = _beaconStateAccessor.GetDomain(state, signatureDomains.BeaconAttester, epoch1);
            bool attestation1Valid = _beaconChainUtility.IsValidIndexedAttestation(state, attestation1, domain1);
            if (!attestation1Valid)
            {
                throw new Exception("Attestation 1 must be valid.");
            }

            Epoch epoch2 = attestation2.Data.Target.Epoch;
            Domain domain2 = _beaconStateAccessor.GetDomain(state, signatureDomains.BeaconAttester, epoch2);
            bool attestation2Valid = _beaconChainUtility.IsValidIndexedAttestation(state, attestation2, domain2);
            if (!attestation2Valid)
            {
                throw new Exception("Attestation 2 must be valid.");
            }

            bool slashedAny = false;
            IEnumerable<ValidatorIndex> intersection = attestation1.AttestingIndices.Intersect(attestation2.AttestingIndices);
            Epoch currentEpoch = _beaconStateAccessor.GetCurrentEpoch(state);
            foreach (ValidatorIndex index in intersection.OrderBy(x => x))
            {
                Validator validator = state.Validators[(int)index];
                bool isSlashableValidator = _beaconChainUtility.IsSlashableValidator(validator, currentEpoch);
                if (isSlashableValidator)
                {
                    _beaconStateMutator.SlashValidator(state, index, ValidatorIndex.None);
                    slashedAny = true;
                }
            }

            if (!slashedAny)
            {
                throw new Exception("Attester slashing should have slashed at least one validator.");
            }
        }

        public void ProcessBlock(BeaconState state, BeaconBlock block)
        {
            if(_logger.IsDebug()) LogDebug.ProcessBlock(_logger, block, state, null);
            ProcessBlockHeader(state, block);
            ProcessBlockRandao(state, block.Body);
            ProcessBlockEth1Data(state, block.Body);
            ProcessBlockOperations(state, block.Body);
        }

        public void ProcessBlockHeader(BeaconState state, BeaconBlock block)
        {
            // Verify that the slots match
            if (block.Slot != state.Slot)
            {
                throw new ArgumentOutOfRangeException("block.Slot", block.Slot, $"Block slot must match state slot {state.Slot}.");
            }
            // Verify that the parent matches
            Root latestBlockHashTreeRoot = _cryptographyService.HashTreeRoot(state.LatestBlockHeader);
            if (!block.ParentRoot.Equals(latestBlockHashTreeRoot))
            {
                throw new ArgumentOutOfRangeException("block.ParentRoot", block.ParentRoot, $"Block parent root must match latest block header root {latestBlockHashTreeRoot}.");
            }

            // Cache current block as the new latest block
            Root bodyRoot = _cryptographyService.HashTreeRoot(block.Body);
            BeaconBlockHeader newBlockHeader = new BeaconBlockHeader(block.Slot,
                block.ParentRoot,
                Root.Zero, // `state_root` is zeroed and overwritten in the next `process_slot` call
                bodyRoot
                );
            if(_logger.IsDebug()) LogDebug.ProcessingBlockHeader(_logger, state.Slot, newBlockHeader, null);
            state.SetLatestBlockHeader(newBlockHeader);

            // Verify proposer is not slashed
            ValidatorIndex beaconProposerIndex = _beaconStateAccessor.GetBeaconProposerIndex(state);
            Validator proposer = state.Validators[(int)beaconProposerIndex];
            if (proposer.IsSlashed)
            {
                throw new Exception("Beacon proposer must not be slashed.");
            }
        }

        public void ProcessDeposit(BeaconState state, Deposit deposit)
        {
            if(_logger.IsDebug()) LogDebug.ProcessDeposit(_logger, deposit, state, null);

            GweiValues gweiValues = _gweiValueOptions.CurrentValue;

            bool isValid = _depositStore.Verify(deposit);
            if (!isValid)
            {
                throw new Exception($"Invalid Merkle branch for deposit for validator public key {deposit.Data.Item.PublicKey}");
            }

            // Deposits must be processed in order
            state.IncreaseEth1DepositIndex();

            DepositData depositData = deposit.Data.Item;
            BlsPublicKey publicKey = depositData.PublicKey;
            Gwei amount = depositData.Amount;

            ValidatorIndex? validatorIndex = null;
            for (int i = 0; i < state.Validators.Count; i++)
            {
                if (publicKey.Equals(state.Validators[i].PublicKey))
                {
                    validatorIndex = new ValidatorIndex((ulong)i);
                    break;
                }
            }
            
            if (validatorIndex is null)
            {
                // Verify the deposit signature (proof of possession) which is not checked by the deposit contract
                DepositMessage depositMessage = new DepositMessage(
                    depositData.PublicKey,
                    depositData.WithdrawalCredentials,
                    depositData.Amount);
                // Fork-agnostic domain since deposits are valid across forks
                Domain domain = _beaconChainUtility.ComputeDomain(_signatureDomainOptions.CurrentValue.Deposit);

                Root depositMessageRoot = _cryptographyService.HashTreeRoot(depositMessage);
                Root signingRoot = _beaconChainUtility.ComputeSigningRoot(depositMessageRoot, domain);

                if (!_cryptographyService.BlsVerify(publicKey, signingRoot, depositData.Signature))
                {
                    return;
                }

                Gwei effectiveBalance = Gwei.Min(amount - (amount % gweiValues.EffectiveBalanceIncrement), gweiValues.MaximumEffectiveBalance);
                Validator newValidator = new Validator(
                    publicKey,
                    depositData.WithdrawalCredentials,
                    effectiveBalance,
                    false,
                    _chainConstants.FarFutureEpoch,
                    _chainConstants.FarFutureEpoch,
                    _chainConstants.FarFutureEpoch,
                    _chainConstants.FarFutureEpoch);
                state.AddValidatorWithBalance(newValidator, amount);
            }
            else
            {
                _beaconStateMutator.IncreaseBalance(state, validatorIndex.Value, amount);
            }
        }

        public void ProcessEpoch(BeaconState state)
        {
            if (_logger.IsDebug()) LogDebug.ProcessEpoch(_logger, state.Slot, null);
            ProcessJustificationAndFinalization(state);
            ProcessRewardsAndPenalties(state);
            ProcessRegistryUpdates(state);

            //# @process_reveal_deadlines
            //# @process_challenge_deadlines

            ProcessSlashings(state);

            //# @update_period_committee

            ProcessFinalUpdates(state);

            //# @after_process_final_updates
        }

        public void ProcessBlockEth1Data(BeaconState state, BeaconBlockBody body)
        {
            if (_logger.IsDebug()) LogDebug.ProcessEth1Data(_logger, state.Slot, body.Eth1Data, null);

            state.AddEth1DataVote(body.Eth1Data);
            int eth1DataVoteCount = state.Eth1DataVotes.Count(x => x.Equals(body.Eth1Data));
            if (eth1DataVoteCount * 2 > (int)_timeParameterOptions.CurrentValue.SlotsPerEth1VotingPeriod)
            {
                state.SetEth1Data(body.Eth1Data);
            }
        }

        public void ProcessFinalUpdates(BeaconState state)
        {
            if (_logger.IsDebug()) LogDebug.ProcessFinalUpdates(_logger, state.Slot, null);

            TimeParameters timeParameters = _timeParameterOptions.CurrentValue;
            GweiValues gweiValues = _gweiValueOptions.CurrentValue;
            StateListLengths stateListLengths = _stateListLengthOptions.CurrentValue;

            Epoch currentEpoch = _beaconStateAccessor.GetCurrentEpoch(state);
            Epoch nextEpoch = currentEpoch + new Epoch(1);

            // Reset eth1 data votes
            Slot nextSlot = state.Slot + Slot.One;
            if (nextSlot % timeParameters.SlotsPerEth1VotingPeriod == Slot.Zero)
            {
                state.ClearEth1DataVotes();
            }

            // Update effective balances with hysteresis
            Gwei halfIncrement = gweiValues.EffectiveBalanceIncrement / 2;
            for (int index = 0; index < state.Validators.Count; index++)
            {
                Validator validator = state.Validators[index];
                Gwei balance = state.Balances[index];
                if (balance < validator.EffectiveBalance || (validator.EffectiveBalance + (halfIncrement * 3)) < balance)
                {
                    Gwei roundedBalance = balance - (balance % gweiValues.EffectiveBalanceIncrement);
                    Gwei effectiveBalance = Gwei.Min(roundedBalance, gweiValues.MaximumEffectiveBalance);
                    validator.SetEffectiveBalance(effectiveBalance);
                }
            }

            // Reset slashings
            Epoch slashingsIndex = (Epoch)(nextEpoch % stateListLengths.EpochsPerSlashingsVector);
            state.SetSlashings(slashingsIndex, Gwei.Zero);

            // Set randao mix
            Epoch randaoIndex = (Epoch)(nextEpoch % stateListLengths.EpochsPerHistoricalVector);
            Bytes32 randaoMix = _beaconStateAccessor.GetRandaoMix(state, currentEpoch);
            state.SetRandaoMix(randaoIndex, randaoMix);

            // Set historical root accumulator
            uint divisor = timeParameters.SlotsPerHistoricalRoot / timeParameters.SlotsPerEpoch;
            if ((ulong)nextEpoch % divisor == 0)
            {
                HistoricalBatch historicalBatch = new HistoricalBatch(state.BlockRoots.ToArray(), state.StateRoots.ToArray());
                Root historicalRoot = _cryptographyService.HashTreeRoot(historicalBatch);
                state.AddHistoricalRoot(historicalRoot);
            }

            // Rotate current/previous epoch attestations
            state.SetPreviousEpochAttestations(state.CurrentEpochAttestations);
            state.SetCurrentEpochAttestations(new PendingAttestation[0]);
        }

        public void ProcessJustificationAndFinalization(BeaconState state)
        {
            if (_logger.IsDebug()) LogDebug.ProcessJustificationAndFinalization(_logger, state.Slot, null);
            Epoch currentEpoch = _beaconStateAccessor.GetCurrentEpoch(state);
            if (currentEpoch <= _chainConstants.GenesisEpoch + new Epoch(1))
            {
                return;
                //throw new ArgumentOutOfRangeException(nameof(state), currentEpoch, "Current epoch of state must be more than one away from genesis epoch.");
            }

            Epoch previousEpoch = _beaconStateAccessor.GetPreviousEpoch(state);
            Checkpoint oldPreviousJustifiedCheckpoint = state.PreviousJustifiedCheckpoint;
            Checkpoint oldCurrentJustifiedCheckpoint = state.CurrentJustifiedCheckpoint;

            // Process justifications
            state.SetPreviousJustifiedCheckpoint(state.CurrentJustifiedCheckpoint);
            state.JustificationBitsShift();

            // Previous Epoch
            IEnumerable<PendingAttestation> matchingTargetAttestationsPreviousEpoch = GetMatchingTargetAttestations(state, previousEpoch);
            Gwei attestingBalancePreviousEpoch = GetAttestingBalance(state, matchingTargetAttestationsPreviousEpoch);
            Gwei totalActiveBalance = _beaconStateAccessor.GetTotalActiveBalance(state);
            if (attestingBalancePreviousEpoch * 3 >= totalActiveBalance * 2)
            {
                Root blockRoot = _beaconStateAccessor.GetBlockRoot(state, previousEpoch);
                Checkpoint checkpoint = new Checkpoint(previousEpoch, blockRoot);
                state.SetCurrentJustifiedCheckpoint(checkpoint);
                state.JustificationBits.Set(1, true);
            }

            // Current Epoch
            IEnumerable<PendingAttestation> matchingTargetAttestationsCurrentEpoch = GetMatchingTargetAttestations(state, currentEpoch);
            Gwei attestingBalanceCurrentEpoch = GetAttestingBalance(state, matchingTargetAttestationsCurrentEpoch);
            if (attestingBalanceCurrentEpoch * 3 >= totalActiveBalance * 2)
            {
                Root blockRoot = _beaconStateAccessor.GetBlockRoot(state, currentEpoch);
                Checkpoint checkpoint = new Checkpoint(currentEpoch, blockRoot);
                state.SetCurrentJustifiedCheckpoint(checkpoint);
                state.JustificationBits.Set(0, true);
            }

            // Process finalizations
            BitArray bits = state.JustificationBits;
            // The 2nd/3rd/4th most recent epochs are justified, the 2nd using the 4th as source
            if ((oldPreviousJustifiedCheckpoint.Epoch + new Epoch(3) == currentEpoch)
                && bits.Cast<bool>().Skip(1).Take(3).All(x => x))
            {
                state.SetFinalizedCheckpoint(oldPreviousJustifiedCheckpoint);
            }
            // The 2nd/3rd most recent epochs are justified, the 2nd using the 3rd as source
            if ((oldPreviousJustifiedCheckpoint.Epoch + new Epoch(2) == currentEpoch)
                && bits.Cast<bool>().Skip(1).Take(2).All(x => x))
            {
                state.SetFinalizedCheckpoint(oldPreviousJustifiedCheckpoint);
            }

            // The 1st/2nd/3rd most recent epochs are justified, the 1st using the 3rd as source
            if ((oldCurrentJustifiedCheckpoint.Epoch + new Epoch(2) == currentEpoch)
                && bits.Cast<bool>().Take(3).All(x => x))
            {
                state.SetFinalizedCheckpoint(oldCurrentJustifiedCheckpoint);
            }
            // The 1st/2nd most recent epochs are justified, the 1st using the 2nd as source
            if ((oldCurrentJustifiedCheckpoint.Epoch + new Epoch(1) == currentEpoch)
                && bits.Cast<bool>().Take(2).All(x => x))
            {
                state.SetFinalizedCheckpoint(oldCurrentJustifiedCheckpoint);
            }
        }

        public void ProcessBlockOperations(BeaconState state, BeaconBlockBody body)
        {
            if (_logger.IsDebug()) LogDebug.ProcessOperations(_logger, state.Slot, body, null);
            // Verify that outstanding deposits are processed up to the maximum number of deposits
            ulong outstandingDeposits = state.Eth1Data.DepositCount - state.Eth1DepositIndex;
            ulong expectedDeposits = Math.Min(_maxOperationsPerBlockOptions.CurrentValue.MaximumDeposits, outstandingDeposits);
            if (body.Deposits.Count != (int)expectedDeposits)
            {
                throw new ArgumentOutOfRangeException("body.Deposits.Count", body.Deposits.Count, $"Block body does not have the expected number of outstanding deposits {expectedDeposits}.");
            }

            foreach (ProposerSlashing proposerSlashing in body.ProposerSlashings)
            {
                ProcessProposerSlashing(state, proposerSlashing);
            }
            foreach (AttesterSlashing attesterSlashing in body.AttesterSlashings)
            {
                ProcessAttesterSlashing(state, attesterSlashing);
            }
            foreach (Attestation attestation in body.Attestations)
            {
                ProcessAttestation(state, attestation);
            }
            foreach (Deposit deposit in body.Deposits)
            {
                ProcessDeposit(state, deposit);
            }
            foreach (SignedVoluntaryExit signedVoluntaryExit in body.VoluntaryExits)
            {
                ProcessVoluntaryExit(state, signedVoluntaryExit);
            }
            //ProcessShareReceiptProof();
        }

        public void ProcessProposerSlashing(BeaconState state, ProposerSlashing proposerSlashing)
        {
            if (_logger.IsDebug()) LogDebug.ProcessProposerSlashing(_logger, proposerSlashing, null);
            // Verify header slots match
            if (proposerSlashing.SignedHeader1.Message.Slot != proposerSlashing.SignedHeader2.Message.Slot)
            {
                throw new Exception($"Proposer slashing header 1 slot {proposerSlashing.SignedHeader1.Message.Slot} must match header 2 slot {proposerSlashing.SignedHeader2.Message.Slot}.");
            }
            // But the headers are different
            if (proposerSlashing.SignedHeader1.Equals(proposerSlashing.SignedHeader2))
            {
                throw new Exception("Proposer slashing must be for two different headers.");
            }
            // Verify the proposer is slashable
            Validator proposer = state.Validators[(int)proposerSlashing.ProposerIndex];
            Epoch currentEpoch = _beaconStateAccessor.GetCurrentEpoch(state);
            bool isSlashable = _beaconChainUtility.IsSlashableValidator(proposer, currentEpoch);
            if (!isSlashable)
            {
                throw new Exception($"Proposer {proposerSlashing.ProposerIndex} is not slashable at epoch {currentEpoch}.");
            }
            // Verify signatures
            Epoch slashingEpoch = _beaconChainUtility.ComputeEpochAtSlot(proposerSlashing.SignedHeader1.Message.Slot);
            Domain domain = _beaconStateAccessor.GetDomain(state, _signatureDomainOptions.CurrentValue.BeaconProposer, slashingEpoch);

            Root headerRoot1 = _cryptographyService.HashTreeRoot(proposerSlashing.SignedHeader1.Message);
            Root signingRoot1 = _beaconChainUtility.ComputeSigningRoot(headerRoot1, domain);
            BlsSignature signature1 = proposerSlashing.SignedHeader1.Signature;
            bool header1Valid = _cryptographyService.BlsVerify(proposer.PublicKey, signingRoot1, signature1);
            if (!header1Valid)
            {
                throw new Exception("Proposer slashing header 1 signature is not valid.");
            }

            Root headerRoot2 = _cryptographyService.HashTreeRoot(proposerSlashing.SignedHeader2.Message);
            Root signingRoot2 = _beaconChainUtility.ComputeSigningRoot(headerRoot2, domain);
            BlsSignature signature2 = proposerSlashing.SignedHeader2.Signature;
            bool header2Valid = _cryptographyService.BlsVerify(proposer.PublicKey, signingRoot2, signature2);
            if (!header2Valid)
            {
                throw new Exception("Proposer slashing header 2 signature is not valid.");
            }

            _beaconStateMutator.SlashValidator(state, proposerSlashing.ProposerIndex, ValidatorIndex.None);
        }

        public void ProcessBlockRandao(BeaconState state, BeaconBlockBody body)
        {
            if (_logger.IsDebug()) LogDebug.ProcessRandao(_logger, state.Slot, body.RandaoReveal, null);
            Epoch epoch = _beaconStateAccessor.GetCurrentEpoch(state);
            // Verify RANDAO reveal
            ValidatorIndex beaconProposerIndex = _beaconStateAccessor.GetBeaconProposerIndex(state);
            Validator proposer = state.Validators[(int)beaconProposerIndex];
            Root epochRoot = _cryptographyService.HashTreeRoot(epoch);
            Domain domain = _beaconStateAccessor.GetDomain(state, _signatureDomainOptions.CurrentValue.Randao, Epoch.None);
            Root signingRoot = _beaconChainUtility.ComputeSigningRoot(epochRoot, domain);
            bool validRandaoReveal = _cryptographyService.BlsVerify(proposer.PublicKey, signingRoot, body.RandaoReveal);
            if (!validRandaoReveal)
            {
                throw new Exception($"Randao reveal {body.RandaoReveal} must match proposer public key {proposer.PublicKey}");
            }
            // Mix in RANDAO reveal
            Bytes32 randaoMix = _beaconStateAccessor.GetRandaoMix(state, epoch);
            Bytes32 randaoHash = _cryptographyService.Hash(body.RandaoReveal.AsSpan());
            Bytes32 mix = randaoMix.Xor(randaoHash);
            Epoch randaoIndex = (Epoch)(epoch % _stateListLengthOptions.CurrentValue.EpochsPerHistoricalVector);
            state.SetRandaoMix(randaoIndex, mix);
        }

        public void ProcessRegistryUpdates(BeaconState state)
        {
            if (_logger.IsDebug()) LogDebug.ProcessRegistryUpdates(_logger, state.Slot, null);

            GweiValues gweiValues = _gweiValueOptions.CurrentValue;

            // Process activation eligibility and ejections
            Epoch currentEpoch = _beaconStateAccessor.GetCurrentEpoch(state);
            Epoch nextEpoch = currentEpoch + Epoch.One;
            for (int index = 0; index < state.Validators.Count; index++)
            {
                Validator validator = state.Validators[index];
                if (_beaconChainUtility.IsEligibleForActivationQueue(validator))
                {
                    validator.SetEligible(nextEpoch);
                }

                bool isActive = _beaconChainUtility.IsActiveValidator(validator, currentEpoch);
                if (isActive && validator.EffectiveBalance <= gweiValues.EjectionBalance)
                {
                    _beaconStateMutator.InitiateValidatorExit(state, new ValidatorIndex((ulong)index));
                }
            }

            // Queue validators eligible for activation and not yet dequeued for activation
            IEnumerable<Validator> activationQueue = state.Validators
                .Select((validator, index) => new { index, validator })
                .Where(x => _beaconChainUtility.IsEligibleForActivation(state, x.validator))
                // # Order by the sequence of activation_eligibility_epoch setting and then index
                .OrderBy(x => x.validator.ActivationEligibilityEpoch)
                .ThenBy(x => x.index)
                .Select(x => x.validator);

            // Dequeued validators for activation up to churn limit
            ulong validatorChurnLimit = _beaconStateAccessor.GetValidatorChurnLimit(state);
            Epoch activationEpoch = _beaconChainUtility.ComputeActivationExitEpoch(currentEpoch);
            foreach (Validator validator in activationQueue.Take((int)validatorChurnLimit))
            {
                validator.SetActive(activationEpoch);
            }
        }

        public void ProcessRewardsAndPenalties(BeaconState state)
        {
            if (_logger.IsDebug()) LogDebug.ProcessRewardsAndPenalties(_logger, state.Slot, null);

            Epoch currentEpoch = _beaconStateAccessor.GetCurrentEpoch(state);
            if (currentEpoch == _chainConstants.GenesisEpoch)
            {
                return;
            }

            (IList<Gwei> rewards, IList<Gwei> penalties) = GetAttestationDeltas(state);
            for (int index = 0; index < state.Validators.Count; index++)
            {
                ValidatorIndex validatorIndex = new ValidatorIndex((ulong)index);
                _beaconStateMutator.IncreaseBalance(state, validatorIndex, rewards[index]);
                _beaconStateMutator.DecreaseBalance(state, validatorIndex, penalties[index]);
            }
        }

        public void ProcessSlashings(BeaconState state)
        {
            if (_logger.IsDebug()) LogDebug.ProcessSlashings(_logger, state.Slot, null);

            Epoch currentEpoch = _beaconStateAccessor.GetCurrentEpoch(state);
            Gwei totalBalance = _beaconStateAccessor.GetTotalActiveBalance(state);

            Epoch targetEpoch = currentEpoch + new Epoch(_stateListLengthOptions.CurrentValue.EpochsPerSlashingsVector / 2);

            Gwei totalSlashings = state.Slashings.Aggregate(Gwei.Zero, (accumulator, x) => accumulator + x);
            Gwei minimumFactor = Gwei.Min(totalSlashings * 3, totalBalance);

            for (int index = 0; index < state.Validators.Count; index++)
            {
                Validator validator = state.Validators[index];
                if (validator.IsSlashed && validator.WithdrawableEpoch == targetEpoch)
                {
                    ulong increment = _gweiValueOptions.CurrentValue.EffectiveBalanceIncrement; // # Factored out from penalty numerator to avoid uint64 overflow
                    Gwei penaltyNumerator = validator.EffectiveBalance / increment * minimumFactor;
                    Gwei penalty = penaltyNumerator / totalBalance * increment;
                    ValidatorIndex validatorIndex = new ValidatorIndex((ulong)index);
                    _beaconStateMutator.DecreaseBalance(state, validatorIndex, penalty);
                }
            }
        }

        public void ProcessSlot(BeaconState state)
        {
            if(_logger.IsDebug()) LogDebug.ProcessSlot(_logger, state.Slot, state, null);
            // Cache state root
            Root previousStateRoot = _cryptographyService.HashTreeRoot(state);
            Slot previousRootIndex = (Slot)(state.Slot % _timeParameterOptions.CurrentValue.SlotsPerHistoricalRoot);
            state.SetStateRoot(previousRootIndex, previousStateRoot);
            // Cache latest block header state root
            if (state.LatestBlockHeader.StateRoot.Equals(Root.Zero))
            {
                // TODO: Validate if the below is correct.
                // NOTE: For slots that have a block, when the block is added, LatestBlockHeader.StateRoot = Root.Zero
                // the state HashTreeRoot is then calculated (with the zero value) and included in the block.
                // The value is set here as part of ProcessSlot and used during processing (during this time HashTreeRoot of
                // state would be different), but is *reset* to Root.Zero which the block is added. 
                // That means HashTreeRoot(state) for slots with blocks always have LatestBlockHeader.StateRoot = Root.Zero.
                // But where slots are *skipped* this LatestBlockHeader.StateRoot is not cleared.
                // i.e.
                // State { slot 5, LatestBlockHeader { slot 5, parent = 0xa4a4, state = 0x0000, body = 0xb5b5 }} => state root 0xc5c5 => Block { slot 5, parent = 0xa4a4, state = 0xc5c5, body = 0xb5b5 } => block root 0xa5a5 
                // State { slot 6, LatestBlockHeader { slot 6, parent = 0xa5a5, state = 0x0000, body = 0xb6b6 }} => state root 0xc6c6 => Block { slot 5, parent = 0xa5a5, state = 0xc6c6, body = 0xb6b6 } => block root 0xa5a5 
                // State { slot 7, LatestBlockHeader { slot 6, parent = 0xa5a5, state = 0xc6c6, body = 0xb6b6 }} => state root 0x1234 => skip block (would have been 0xc7c7 if state root = 0x0000; the calculated state root goes into the history as well) 
                // State { slot 8, LatestBlockHeader { slot 8, parent = 0xa5a5, state = 0x0000, body = 0xb8b8 }} => state root 0xc8c8 => Block { slot 8, parent = 0xa5a5, state = 0xc8c8, body = 0x85b8 } => block root 0xa8a8 
                state.LatestBlockHeader.SetStateRoot(previousStateRoot);
            }
            // Cache block root
            Root previousBlockRoot = _cryptographyService.HashTreeRoot(state.LatestBlockHeader);
            state.SetBlockRoot(previousRootIndex, previousBlockRoot);
        }

        public void ProcessSlots(BeaconState state, Slot slot)
        {
            if(_logger.IsDebug()) LogDebug.ProcessSlots(_logger, state, slot, null);
            if (state.Slot > slot)
            {
                throw new ArgumentOutOfRangeException(nameof(slot), slot, $"Slot to process should be greater than current state slot {state.Slot}");
            }

            while (state.Slot < slot)
            {
                ProcessSlot(state);
                // Process epoch on the start slot of the next epoch
                if ((state.Slot + new Slot(1)) % _timeParameterOptions.CurrentValue.SlotsPerEpoch == Slot.Zero)
                {
                    ProcessEpoch(state);
                }
                state.IncreaseSlot();
            }
        }

        public void ProcessVoluntaryExit(BeaconState state, SignedVoluntaryExit signedVoluntaryExit)
        {
            VoluntaryExit voluntaryExit = signedVoluntaryExit.Message;
            
            if (_logger.IsDebug()) LogDebug.ProcessVoluntaryExit(_logger, voluntaryExit, null);

            Validator validator = state.Validators[(int)voluntaryExit.ValidatorIndex];

            //#Verify the validator is active
            Epoch currentEpoch = _beaconStateAccessor.GetCurrentEpoch(state);
            bool isActiveValidator = _beaconChainUtility.IsActiveValidator(validator, currentEpoch);
            if (!isActiveValidator)
            {
                throw new Exception($"Validator {voluntaryExit.ValidatorIndex} must be active in order to exit.");
            }

            //# Verify exit has not been initiated
            bool hasExited = validator.ExitEpoch != _chainConstants.FarFutureEpoch;
            if (hasExited)
            {
                throw new Exception($"Validator {voluntaryExit.ValidatorIndex} already has exit epoch {validator.ExitEpoch}.");
            }

            //# Exits must specify an epoch when they become valid; they are not valid before then
            bool isCurrentAtOrAfterExit = currentEpoch >= voluntaryExit.Epoch;
            if (!isCurrentAtOrAfterExit)
            {
                throw new Exception($"Validator {voluntaryExit.ValidatorIndex} can not exit because the current epoch {currentEpoch} has not yet reached their exit epoch {validator.ExitEpoch}.");
            }

            //# Verify the validator has been active long enough
            TimeParameters timeParameters = _timeParameterOptions.CurrentValue;
            Epoch minimumActiveEpoch = validator.ActivationEpoch + timeParameters.PersistentCommitteePeriod;
            bool isCurrentAtOrAfterMinimum = currentEpoch >= minimumActiveEpoch;
            if (!isCurrentAtOrAfterMinimum)
            {
                throw new Exception($"Validator {voluntaryExit.ValidatorIndex} can not exit because the current epoch {currentEpoch} has not yet reached the minimum active epoch of {timeParameters.PersistentCommitteePeriod} after their activation epoch {validator.ActivationEpoch}.");
            }

            //# Verify signature
            Domain domain = _beaconStateAccessor.GetDomain(state, _signatureDomainOptions.CurrentValue.VoluntaryExit, voluntaryExit.Epoch);
            Root voluntaryExitRoot = _cryptographyService.HashTreeRoot(voluntaryExit);
            Root signingRoot = _beaconChainUtility.ComputeSigningRoot(voluntaryExitRoot, domain);
            bool validSignature = _cryptographyService.BlsVerify(validator.PublicKey, signingRoot, signedVoluntaryExit.Signature);
            if (!validSignature)
            {
                throw new Exception("Voluntary exit signature is not valid.");
            }

            //# Initiate exit
            _beaconStateMutator.InitiateValidatorExit(state, voluntaryExit.ValidatorIndex);
        }

        public BeaconState StateTransition(BeaconState state, SignedBeaconBlock signedBlock, bool validateResult)
        {
            BeaconBlock block = signedBlock.Message;

            if (_logger.IsDebug()) LogDebug.StateTransition(_logger, validateResult, state, block, null);

            // Process slots (including those with no blocks) since block
            ProcessSlots(state, block.Slot);
            
            // Verify signature
            if (validateResult)
            {
                bool isValidBlockSignature = VerifyBlockSignature(state, signedBlock);
                if (!isValidBlockSignature)
                {
                    throw new Exception($"Block {block} signature must be valid (when processing state transition for {state}).");
                }
            }
            
            // Process block
            ProcessBlock(state, block);
            
            // Validate state root (True in production)
            if (validateResult)
            {
                //var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
                //options.AddCortexContainerConverters();
                //var debugState = System.Text.Json.JsonSerializer.Serialize(state, options);

                Root checkStateRoot = _cryptographyService.HashTreeRoot(state);
                if (!block.StateRoot.Equals(checkStateRoot))
                {
                    throw new Exception($"Mismatch between calculated state root {checkStateRoot} and block state root {block.StateRoot}.");
                }

                if (_logger.IsInfo())
                {
                    Root blockRoot = _cryptographyService.HashTreeRoot(block);
                    Log.ValidatedStateTransition(_logger, checkStateRoot, state, blockRoot, block, null);
                }
            }
            return state;
        }

        private bool VerifyBlockSignature(BeaconState state, SignedBeaconBlock signedBlock)
        {
            ValidatorIndex beaconProposerIndex = _beaconStateAccessor.GetBeaconProposerIndex(state);
            Validator proposer = state.Validators[(int)beaconProposerIndex];
            Root blockRoot = _cryptographyService.HashTreeRoot(signedBlock.Message);
            Domain domain = _beaconStateAccessor.GetDomain(state, _signatureDomainOptions.CurrentValue.BeaconProposer,
                Epoch.None);
            Root signingRoot = _beaconChainUtility.ComputeSigningRoot(blockRoot, domain);
            bool isValid = _cryptographyService.BlsVerify(proposer.PublicKey, signingRoot, signedBlock.Signature);
            if (_logger.IsDebug()) LogDebug.VerifiedBlockSignature(_logger, blockRoot, signedBlock.Message, signingRoot, beaconProposerIndex, isValid, null);
            return isValid;
        }
    }
}
