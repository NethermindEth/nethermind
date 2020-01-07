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
using Nethermind.Core2.Configuration;
using Nethermind.BeaconNode.Services;
using Nethermind.BeaconNode.Ssz;
using Nethermind.Core2;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Nethermind.Logging.Microsoft;

namespace Nethermind.BeaconNode
{
    public class BeaconStateTransition
    {
        private readonly BeaconChainUtility _beaconChainUtility;
        private readonly BeaconStateAccessor _beaconStateAccessor;
        private readonly BeaconStateMutator _beaconStateMutator;
        private readonly ChainConstants _chainConstants;
        private readonly ICryptographyService _cryptographyService;
        private readonly IOptionsMonitor<GweiValues> _gweiValueOptions;
        private readonly IOptionsMonitor<InitialValues> _initialValueOptions;
        private readonly ILogger _logger;
        private readonly IOptionsMonitor<MaxOperationsPerBlock> _maxOperationsPerBlockOptions;
        private readonly IOptionsMonitor<SignatureDomains> _signatureDomainOptions;
        private readonly IOptionsMonitor<MiscellaneousParameters> _miscellaneousParameterOptions;
        private readonly IOptionsMonitor<RewardsAndPenalties> _rewardsAndPenaltiesOptions;
        private readonly IOptionsMonitor<StateListLengths> _stateListLengthOptions;
        private readonly IOptionsMonitor<TimeParameters> _timeParameterOptions;

        public BeaconStateTransition(ILogger<BeaconStateTransition> logger,
            ChainConstants chainConstants,
            IOptionsMonitor<MiscellaneousParameters> miscellaneousParameterOptions,
            IOptionsMonitor<GweiValues> gweiValueOptions,
            IOptionsMonitor<InitialValues> initialValueOptions,
            IOptionsMonitor<TimeParameters> timeParameterOptions,
            IOptionsMonitor<StateListLengths> stateListLengthOptions,
            IOptionsMonitor<RewardsAndPenalties> rewardsAndPenaltiesOptions,
            IOptionsMonitor<MaxOperationsPerBlock> maxOperationsPerBlockOptions,
            IOptionsMonitor<SignatureDomains> signatureDomainOptions,
            ICryptographyService cryptographyService,
            BeaconChainUtility beaconChainUtility,
            BeaconStateAccessor beaconStateAccessor,
            BeaconStateMutator beaconStateMutator)
        {
            _logger = logger;
            _chainConstants = chainConstants;
            _miscellaneousParameterOptions = miscellaneousParameterOptions;
            _gweiValueOptions = gweiValueOptions;
            _initialValueOptions = initialValueOptions;
            _timeParameterOptions = timeParameterOptions;
            _stateListLengthOptions = stateListLengthOptions;
            _rewardsAndPenaltiesOptions = rewardsAndPenaltiesOptions;
            _maxOperationsPerBlockOptions = maxOperationsPerBlockOptions;
            _signatureDomainOptions = signatureDomainOptions;
            _cryptographyService = cryptographyService;
            _beaconChainUtility = beaconChainUtility;
            _beaconStateAccessor = beaconStateAccessor;
            _beaconStateMutator = beaconStateMutator;
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
                Gwei attesterReward = maxAttesterReward / (ulong)attestation.InclusionDelay;
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
                        Gwei additionalInactivityPenalty = (effectiveBalance * (ulong)finalityDelay) / rewardsAndPenalties.InactivityPenaltyQuotient;
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

        public IEnumerable<PendingAttestation> GetMatchingHeadAttestations(BeaconState state, Epoch epoch)
        {
            IEnumerable<PendingAttestation> sourceAttestations = GetMatchingSourceAttestations(state, epoch);
            return sourceAttestations.Where(x =>
            {
                Hash32 blockRootAtSlot = _beaconStateAccessor.GetBlockRootAtSlot(state, x.Data.Slot);
                return x.Data.BeaconBlockRoot == blockRootAtSlot;
            });
        }

        public IEnumerable<PendingAttestation> GetMatchingSourceAttestations(BeaconState state, Epoch epoch)
        {
            Epoch currentEpoch = _beaconStateAccessor.GetCurrentEpoch(state);
            Epoch previousEpoch = _beaconStateAccessor.GetPreviousEpoch(state);
            if (epoch != currentEpoch && epoch != previousEpoch)
            {
                throw new ArgumentOutOfRangeException(nameof(epoch), epoch, $"The epoch for attestions must be either the current epoch {currentEpoch} or previous epoch {previousEpoch}.");
            }

            if (epoch == currentEpoch)
            {
                return state.CurrentEpochAttestations;
            }
            return state.PreviousEpochAttestations;
        }

        public IEnumerable<PendingAttestation> GetMatchingTargetAttestations(BeaconState state, Epoch epoch)
        {
            Hash32 blockRoot = _beaconStateAccessor.GetBlockRoot(state, epoch);
            IEnumerable<PendingAttestation> sourceAttestations = GetMatchingSourceAttestations(state, epoch);
            return sourceAttestations.Where(x => x.Data.Target.Root == blockRoot);
        }

        public IEnumerable<ValidatorIndex> GetUnslashedAttestingIndices(BeaconState state, IEnumerable<PendingAttestation> attestations)
        {
            IEnumerable<ValidatorIndex> output = new List<ValidatorIndex>();
            foreach (PendingAttestation attestation in attestations)
            {
                IEnumerable<ValidatorIndex> attestingIndices = _beaconStateAccessor.GetAttestingIndices(state, attestation.Data, attestation.AggregationBits);
                output = output.Union(attestingIndices);
            }
            return output.Where(x => !state.Validators[(int)x].IsSlashed);
        }

        public void ProcessAttestation(BeaconState state, Attestation attestation)
        {
            if(_logger.IsDebug()) LogDebug.ProcessAttestation(_logger, attestation, state, null);

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
            Slot minimumSlot = data.Slot + timeParameters.MinimumAttestationInclusionDelay;
            Slot maximumSlot = (Slot)(data.Slot + timeParameters.SlotsPerEpoch);
            if (state.Slot < minimumSlot)
            {
                throw new Exception($"Current state slot {state.Slot} must be equal or greater than the attestion slot {data.Slot} plus minimum delay {timeParameters.MinimumAttestationInclusionDelay}.");
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

            // Check signature
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

        public void ProcessBlock(BeaconState state, BeaconBlock block, bool validateStateRoot)
        {
            if(_logger.IsDebug()) LogDebug.ProcessBlock(_logger, validateStateRoot, block, state, null);
            ProcessBlockHeader(state, block, validateStateRoot);
            ProcessRandao(state, block.Body);
            ProcessEth1Data(state, block.Body);
            ProcessOperations(state, block.Body);
        }

        public void ProcessBlockHeader(BeaconState state, BeaconBlock block, bool validateStateRoot)
        {
            if(_logger.IsDebug()) LogDebug.ProcessBlockHeader(_logger, block, null);
            // Verify that the slots match
            if (block.Slot != state.Slot)
            {
                throw new ArgumentOutOfRangeException("block.Slot", block.Slot, $"Block slot must match state slot {state.Slot}.");
            }
            // Verify that the parent matches
            Hash32 latestBlockSigningRoot = state.LatestBlockHeader.SigningRoot();
            if (block.ParentRoot != latestBlockSigningRoot)
            {
                throw new ArgumentOutOfRangeException("block.ParentRoot", block.ParentRoot, $"Block parent root must match latest block header root {latestBlockSigningRoot}.");
            }

            // Save current block as the new latest block
            Hash32 bodyRoot = _cryptographyService.HashTreeRoot(block.Body);
            BeaconBlockHeader newBlockHeader = new BeaconBlockHeader(block.Slot,
                block.ParentRoot,
                Hash32.Zero, // `state_root` is zeroed and overwritten in the next `process_slot` call
                bodyRoot,
                BlsSignature.Empty //`signature` is zeroed
                );
            state.SetLatestBlockHeader(newBlockHeader);

            // Verify proposer is not slashed
            ValidatorIndex beaconProposerIndex = _beaconStateAccessor.GetBeaconProposerIndex(state);
            Validator proposer = state.Validators[(int)beaconProposerIndex];
            if (proposer.IsSlashed)
            {
                throw new Exception("Beacon proposer must not be slashed.");
            }

            // Verify proposer signature
            if (validateStateRoot)
            {
                Hash32 signingRoot = _cryptographyService.SigningRoot(block);
                Domain domain = _beaconStateAccessor.GetDomain(state,
                    _signatureDomainOptions.CurrentValue.BeaconProposer, Epoch.None);
                bool validSignature =
                    _cryptographyService.BlsVerify(proposer.PublicKey, signingRoot, block.Signature, domain);
                if (!validSignature)
                {
                    throw new Exception($"Block signature must match proposer public key {proposer.PublicKey}");
                }
            }
        }

        public void ProcessDeposit(BeaconState state, Deposit deposit)
        {
            if(_logger.IsDebug()) LogDebug.ProcessDeposit(_logger, deposit, state, null);

            GweiValues gweiValues = _gweiValueOptions.CurrentValue;

            // Verify the Merkle branch
            bool isValid = _beaconChainUtility.IsValidMerkleBranch(
                deposit.Data.HashTreeRoot(),
                deposit.Proof,
                _chainConstants.DepositContractTreeDepth + 1, // Add 1 for the 'List' length mix-in
                state.Eth1DepositIndex,
                state.Eth1Data.DepositRoot);
            if (!isValid)
            {
                throw new Exception($"Invalid Merkle branch for deposit for validator poublic key {deposit.Data.PublicKey}");
            }

            // Deposits must be processed in order
            state.IncreaseEth1DepositIndex();

            BlsPublicKey publicKey = deposit.Data.PublicKey;
            Gwei amount = deposit.Data.Amount;
            List<BlsPublicKey> validatorPublicKeys = state.Validators.Select(x => x.PublicKey).ToList();

            if (!validatorPublicKeys.Contains(publicKey))
            {
                // Verify the deposit signature (proof of possession) for new validators
                // Note: The deposit contract does not check signatures.
                // Note: Deposits are valid across forks, thus the deposit domain is retrieved directly from 'computer_domain'.

                Hash32 signingRoot = _cryptographyService.SigningRoot(deposit.Data);
                Domain domain = _beaconChainUtility.ComputeDomain(_signatureDomainOptions.CurrentValue.Deposit);
                if (!_cryptographyService.BlsVerify(publicKey, signingRoot, deposit.Data.Signature, domain))
                {
                    return;
                }

                Gwei effectiveBalance = Gwei.Min(amount - (amount % gweiValues.EffectiveBalanceIncrement), gweiValues.MaximumEffectiveBalance);
                Validator newValidator = new Validator(
                    publicKey,
                    deposit.Data.WithdrawalCredentials,
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
                ValidatorIndex index = (ValidatorIndex)(ulong)validatorPublicKeys.IndexOf(publicKey);
                _beaconStateMutator.IncreaseBalance(state, index, amount);
            }
        }

        public void ProcessEpoch(BeaconState state)
        {
            if (_logger.IsDebug()) LogDebug.ProcessEpoch(_logger, state, null);
            ProcessJustificationAndFinalization(state);

            // Was removed from phase 0 spec
            //ProcessCrosslinks(state);

            ProcessRewardsAndPenalties(state);
            ProcessRegistryUpdates(state);

            //# @process_reveal_deadlines
            //# @process_challenge_deadlines

            ProcessSlashings(state);

            //# @update_period_committee

            ProcessFinalUpdates(state);

            //# @after_process_final_updates
        }

        public void ProcessEth1Data(BeaconState state, BeaconBlockBody body)
        {
            if (_logger.IsDebug()) LogDebug.ProcessEth1Data(_logger, body, null);

            state.AddEth1DataVote(body.Eth1Data);
            int eth1DataVoteCount = state.Eth1DataVotes.Count(x => x.Equals(body.Eth1Data));
            if (eth1DataVoteCount * 2 > (int)_timeParameterOptions.CurrentValue.SlotsPerEth1VotingPeriod)
            {
                state.SetEth1Data(body.Eth1Data);
            }
        }

        public void ProcessFinalUpdates(BeaconState state)
        {
            if (_logger.IsDebug()) LogDebug.ProcessFinalUpdates(_logger, state, null);

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
            Hash32 randaoMix = _beaconStateAccessor.GetRandaoMix(state, currentEpoch);
            state.SetRandaoMix(randaoIndex, randaoMix);

            // Set historical root accumulator
            uint divisor = timeParameters.SlotsPerHistoricalRoot / timeParameters.SlotsPerEpoch;
            if ((ulong)nextEpoch % divisor == 0)
            {
                HistoricalBatch historicalBatch = new HistoricalBatch(state.BlockRoots.ToArray(), state.StateRoots.ToArray());
                Hash32 historicalRoot = historicalBatch.HashTreeRoot();
                state.AddHistoricalRoot(historicalRoot);
            }

            // Rotate current/previous epoch attestations
            state.SetPreviousEpochAttestations(state.CurrentEpochAttestations);
            state.SetCurrentEpochAttestations(new PendingAttestation[0]);
        }

        public void ProcessJustificationAndFinalization(BeaconState state)
        {
            if (_logger.IsDebug()) LogDebug.ProcessJustificationAndFinalization(_logger, state, null);
            Epoch currentEpoch = _beaconStateAccessor.GetCurrentEpoch(state);
            if (currentEpoch <= _initialValueOptions.CurrentValue.GenesisEpoch + new Epoch(1))
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
                Hash32 blockRoot = _beaconStateAccessor.GetBlockRoot(state, previousEpoch);
                Checkpoint checkpoint = new Checkpoint(previousEpoch, blockRoot);
                state.SetCurrentJustifiedCheckpoint(checkpoint);
                state.JustificationBits.Set(1, true);
            }

            // Current Epoch
            IEnumerable<PendingAttestation> matchingTargetAttestationsCurrentEpoch = GetMatchingTargetAttestations(state, currentEpoch);
            Gwei attestingBalanceCurrentEpoch = GetAttestingBalance(state, matchingTargetAttestationsCurrentEpoch);
            if (attestingBalanceCurrentEpoch * 3 >= totalActiveBalance * 2)
            {
                Hash32 blockRoot = _beaconStateAccessor.GetBlockRoot(state, currentEpoch);
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

        public void ProcessOperations(BeaconState state, BeaconBlockBody body)
        {
            if (_logger.IsDebug()) LogDebug.ProcessOperations(_logger, body, null);
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
            foreach (VoluntaryExit voluntaryExit in body.VoluntaryExits)
            {
                ProcessVoluntaryExit(state, voluntaryExit);
            }
            //ProcessShareReceiptProof();
        }

        public void ProcessProposerSlashing(BeaconState state, ProposerSlashing proposerSlashing)
        {
            if (_logger.IsDebug()) LogDebug.ProcessProposerSlashing(_logger, proposerSlashing, null);
            Validator proposer = state.Validators[(int)proposerSlashing.ProposerIndex];
            // Verify slots match
            if (proposerSlashing.Header1.Slot != proposerSlashing.Header2.Slot)
            {
                throw new Exception($"Proposer slashing header 1 slot {proposerSlashing.Header1.Slot} must match header 2 slot {proposerSlashing.Header2.Slot}.");
            }
            // But the headers are different
            if (proposerSlashing.Header1.Equals(proposerSlashing.Header2))
            {
                throw new Exception($"Proposer slashing must be for two different headers.");
            }
            // Check proposer is slashable
            Epoch currentEpoch = _beaconStateAccessor.GetCurrentEpoch(state);
            bool isSlashable = _beaconChainUtility.IsSlashableValidator(proposer, currentEpoch);
            if (!isSlashable)
            {
                throw new Exception($"Proposer {proposerSlashing.ProposerIndex} is not slashable at epoch {currentEpoch}.");
            }
            // Signatures are valid
            Epoch slashingEpoch = _beaconChainUtility.ComputeEpochAtSlot(proposerSlashing.Header1.Slot);
            Domain domain = _beaconStateAccessor.GetDomain(state, _signatureDomainOptions.CurrentValue.BeaconProposer, slashingEpoch);

            Hash32 signingRoot1 = _cryptographyService.SigningRoot(proposerSlashing.Header1);
            BlsSignature signature1 = proposerSlashing.Header1.Signature;
            bool header1Valid = _cryptographyService.BlsVerify(proposer.PublicKey, signingRoot1, signature1, domain);
            if (!header1Valid)
            {
                throw new Exception("Proposer slashing header 1 signature is not valid.");
            }

            Hash32 signingRoot2 = _cryptographyService.SigningRoot(proposerSlashing.Header2);
            BlsSignature signature2 = proposerSlashing.Header2.Signature;
            bool header2Valid = _cryptographyService.BlsVerify(proposer.PublicKey, signingRoot2, signature2, domain);
            if (!header2Valid)
            {
                throw new Exception("Proposer slashing header 2 signature is not valid.");
            }

            _beaconStateMutator.SlashValidator(state, proposerSlashing.ProposerIndex, ValidatorIndex.None);
        }

        public void ProcessRandao(BeaconState state, BeaconBlockBody body)
        {
            if (_logger.IsDebug()) LogDebug.ProcessRandao(_logger, body, null);
            Epoch epoch = _beaconStateAccessor.GetCurrentEpoch(state);
            // Verify RANDAO reveal
            ValidatorIndex beaconProposerIndex = _beaconStateAccessor.GetBeaconProposerIndex(state);
            Validator proposer = state.Validators[(int)beaconProposerIndex];
            Hash32 epochRoot = _cryptographyService.HashTreeRoot(epoch);
            Domain domain = _beaconStateAccessor.GetDomain(state, _signatureDomainOptions.CurrentValue.Randao, Epoch.None);
            bool validRandaoReveal = _cryptographyService.BlsVerify(proposer.PublicKey, epochRoot, body.RandaoReveal, domain);
            if (!validRandaoReveal)
            {
                throw new Exception($"Randao reveal {body.RandaoReveal} must match proposer public key {proposer.PublicKey}");
            }
            // Mix in RANDAO reveal
            Hash32 randaoMix = _beaconStateAccessor.GetRandaoMix(state, epoch);
            Hash32 randaoHash = _cryptographyService.Hash(body.RandaoReveal.AsSpan());
            Hash32 mix = randaoMix.Xor(randaoHash);
            Epoch randaoIndex = (Epoch)(epoch % _stateListLengthOptions.CurrentValue.EpochsPerHistoricalVector);
            state.SetRandaoMix(randaoIndex, mix);
        }

        public void ProcessRegistryUpdates(BeaconState state)
        {
            if (_logger.IsDebug()) LogDebug.ProcessRegistryUpdates(_logger, state, null);

            GweiValues gweiValues = _gweiValueOptions.CurrentValue;

            // Process activation eligibility and ejections
            Epoch currentEpoch = _beaconStateAccessor.GetCurrentEpoch(state);
            for (int index = 0; index < state.Validators.Count; index++)
            {
                Validator validator = state.Validators[index];
                if (validator.ActivationEligibilityEpoch == _chainConstants.FarFutureEpoch
                    && validator.EffectiveBalance == gweiValues.MaximumEffectiveBalance)
                {
                    validator.SetEligible(currentEpoch);
                }

                bool isActive = _beaconChainUtility.IsActiveValidator(validator, currentEpoch);
                if (isActive && validator.EffectiveBalance <= gweiValues.EjectionBalance)
                {
                    _beaconStateMutator.InitiateValidatorExit(state, new ValidatorIndex((ulong)index));
                }
            }

            // Queue validators eligible for activation and not dequeued for activation prior to finalized epoch
            Epoch activationExitEpoch = _beaconChainUtility.ComputeActivationExitEpoch(state.FinalizedCheckpoint.Epoch);

            IEnumerable<int> activationQueue = state.Validators
                .Select((validator, index) => new { index, validator })
                .Where(x => x.validator.ActivationEligibilityEpoch != _chainConstants.FarFutureEpoch
                    && x.validator.ActivationEpoch >= activationExitEpoch)
                .OrderBy(x => x.validator.ActivationEligibilityEpoch)
                .Select(x => x.index);

            // Dequeued validators for activation up to churn limit (without resetting activation epoch)
            ulong validatorChurnLimit = _beaconStateAccessor.GetValidatorChurnLimit(state);
            Epoch activationEpoch = _beaconChainUtility.ComputeActivationExitEpoch(currentEpoch);
            foreach (int index in activationQueue.Take((int)validatorChurnLimit))
            {
                Validator validator = state.Validators[index];
                if (validator.ActivationEpoch == _chainConstants.FarFutureEpoch)
                {
                    validator.SetActive(activationEpoch);
                }
            }
        }

        public void ProcessRewardsAndPenalties(BeaconState state)
        {
            if (_logger.IsDebug()) LogDebug.ProcessRewardsAndPenalties(_logger, state, null);

            Epoch currentEpoch = _beaconStateAccessor.GetCurrentEpoch(state);
            if (currentEpoch == _initialValueOptions.CurrentValue.GenesisEpoch)
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
            if (_logger.IsDebug()) LogDebug.ProcessSlashings(_logger, state, null);

            Epoch currentEpoch = _beaconStateAccessor.GetCurrentEpoch(state);
            Gwei totalBalance = _beaconStateAccessor.GetTotalActiveBalance(state);

            Epoch targetEpoch = currentEpoch + new Epoch((ulong)_stateListLengthOptions.CurrentValue.EpochsPerSlashingsVector / 2);

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
            Hash32 previousStateRoot = _cryptographyService.HashTreeRoot(state);
            Slot previousRootIndex = (Slot)(state.Slot % _timeParameterOptions.CurrentValue.SlotsPerHistoricalRoot);
            state.SetStateRoot(previousRootIndex, previousStateRoot);
            // Cache latest block header state root
            if (state.LatestBlockHeader.StateRoot == Hash32.Zero)
            {
                state.LatestBlockHeader.SetStateRoot(previousStateRoot);
            }
            // Cache block root
            Hash32 previousBlockRoot = state.LatestBlockHeader.SigningRoot();
            state.SetBlockRoot(previousRootIndex, previousBlockRoot);
        }

        public void ProcessSlots(BeaconState state, Slot slot)
        {
            if(_logger.IsDebug()) LogDebug.ProcessSlots(_logger, slot, state, null);
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

        public void ProcessVoluntaryExit(BeaconState state, VoluntaryExit exit)
        {
            if (_logger.IsDebug()) LogDebug.ProcessVoluntaryExit(_logger, exit, state, null);

            Validator validator = state.Validators[(int)exit.ValidatorIndex];

            //#Verify the validator is active
            Epoch currentEpoch = _beaconStateAccessor.GetCurrentEpoch(state);
            bool isActiveValidator = _beaconChainUtility.IsActiveValidator(validator, currentEpoch);
            if (!isActiveValidator)
            {
                throw new Exception($"Validator {exit.ValidatorIndex} must be active in order to exit.");
            }

            //# Verify the validator has not yet exited
            bool hasExited = validator.ExitEpoch != _chainConstants.FarFutureEpoch;
            if (hasExited)
            {
                throw new Exception($"Validator {exit.ValidatorIndex} already has exit epoch {validator.ExitEpoch}.");
            }

            //# Exits must specify an epoch when they become valid; they are not valid before then
            bool isCurrentAtOrAfterExit = currentEpoch >= exit.Epoch;
            if (!isCurrentAtOrAfterExit)
            {
                throw new Exception($"Validator {exit.ValidatorIndex} can not exit because the current epoch {currentEpoch} has not yet reached their exit epoch {validator.ExitEpoch}.");
            }

            //# Verify the validator has been active long enough
            TimeParameters timeParameters = _timeParameterOptions.CurrentValue;
            Epoch minimumActiveEpoch = validator.ActivationEpoch + timeParameters.PersistentCommitteePeriod;
            bool isCurrentAtOrAfterMinimum = currentEpoch >= minimumActiveEpoch;
            if (!isCurrentAtOrAfterMinimum)
            {
                throw new Exception($"Validator {exit.ValidatorIndex} can not exit because the current epoch {currentEpoch} has not yet reached the minimum active epoch of {timeParameters.PersistentCommitteePeriod} after their activation epoch {validator.ActivationEpoch}.");
            }

            //# Verify signature
            Domain domain = _beaconStateAccessor.GetDomain(state, _signatureDomainOptions.CurrentValue.VoluntaryExit, exit.Epoch);
            Hash32 signingRoot = _cryptographyService.SigningRoot(exit);
            bool validSignature = _cryptographyService.BlsVerify(validator.PublicKey, signingRoot, exit.Signature, domain);
            if (!validSignature)
            {
                throw new Exception("Voluntary exit signature is not valid.");
            }

            //# Initiate exit
            _beaconStateMutator.InitiateValidatorExit(state, exit.ValidatorIndex);
        }

        public BeaconState StateTransition(BeaconState state, BeaconBlock block, bool validateStateRoot)
        {
            if (_logger.IsDebug()) LogDebug.StateTransition(_logger, validateStateRoot, state, block, null);

            // Process slots (including those with no blocks) since block
            ProcessSlots(state, block.Slot);
            // Process block
            ProcessBlock(state, block, validateStateRoot);
            // Validate state root (True in production)
            if (validateStateRoot)
            {
                //var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
                //options.AddCortexContainerConverters();
                //var debugState = System.Text.Json.JsonSerializer.Serialize(state, options);

                Hash32 checkStateRoot = _cryptographyService.HashTreeRoot(state);
                if (block.StateRoot != checkStateRoot)
                {
                    throw new Exception($"Mismatch between calculated state root {checkStateRoot} and block state root {block.StateRoot}.");
                }

                if (_logger.IsInfo())
                {
                    Hash32 blockSigningRoot = _cryptographyService.SigningRoot(block);
                    Log.ValidatedStateTransition(_logger, checkStateRoot, state, blockSigningRoot, block, null);
                }
            }
            return state;
        }
    }
}
