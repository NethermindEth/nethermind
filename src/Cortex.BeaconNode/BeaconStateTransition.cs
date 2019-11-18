using System;
using System.Collections.Generic;
using System.Linq;
using Cortex.BeaconNode.Configuration;
using Cortex.BeaconNode.Ssz;
using Cortex.Containers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cortex.BeaconNode
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
        private readonly IOptionsMonitor<MaxOperationsPerBlock> _maxOperationsPerBlock;
        private readonly IOptionsMonitor<MiscellaneousParameters> _miscellaneousParameterOptions;
        private readonly IOptionsMonitor<StateListLengths> _stateListLengthOptions;
        private readonly IOptionsMonitor<TimeParameters> _timeParameterOptions;

        public BeaconStateTransition(ILogger<BeaconStateTransition> logger,
            ChainConstants chainConstants,
            IOptionsMonitor<MiscellaneousParameters> miscellaneousParameterOptions,
            IOptionsMonitor<GweiValues> gweiValueOptions,
            IOptionsMonitor<InitialValues> initialValueOptions,
            IOptionsMonitor<TimeParameters> timeParameterOptions,
            IOptionsMonitor<StateListLengths> stateListLengthOptions,
            IOptionsMonitor<MaxOperationsPerBlock> maxOperationsPerBlock,
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
            _maxOperationsPerBlock = maxOperationsPerBlock;
            _cryptographyService = cryptographyService;
            _beaconChainUtility = beaconChainUtility;
            _beaconStateAccessor = beaconStateAccessor;
            _beaconStateMutator = beaconStateMutator;
        }

        public Gwei GetAttestingBalance(BeaconState state, IEnumerable<PendingAttestation> attestations)
        {
            var unslashed = GetUnslashedAttestingIndices(state, attestations);
            return _beaconStateAccessor.GetTotalBalance(state, unslashed);
        }

        public IEnumerable<PendingAttestation> GetMatchingSourceAttestations(BeaconState state, Epoch epoch)
        {
            var currentEpoch = _beaconStateAccessor.GetCurrentEpoch(state);
            var previousEpoch = _beaconStateAccessor.GetPreviousEpoch(state);
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
            var blockRoot = _beaconStateAccessor.GetBlockRoot(state, epoch);
            var sourceAttestations = GetMatchingSourceAttestations(state, epoch);
            return sourceAttestations.Where(x => x.Data.Target.Root == blockRoot);
        }

        public IEnumerable<ValidatorIndex> GetUnslashedAttestingIndices(BeaconState state, IEnumerable<PendingAttestation> attestations)
        {
            IEnumerable<ValidatorIndex> output = new List<ValidatorIndex>();
            foreach (var attestation in attestations)
            {
                var attestingIndices = _beaconStateAccessor.GetAttestingIndices(state, attestation.Data, attestation.AggregationBits);
                output = output.Union(attestingIndices);
            }
            return output.Where(x => !state.Validators[(int)(ulong)x].IsSlashed);
        }

        public void ProcessAttestation(BeaconState state, Attestation attestation)
        {
            _logger.LogInformation(Event.ProcessAttestation, "Process block operation attestation {Attestation} for state {BeaconState}.", attestation, state);

            var timeParameters = _timeParameterOptions.CurrentValue;
            var data = attestation.Data;

            var committeeCount = _beaconStateAccessor.GetCommitteeCountAtSlot(state, data.Slot);
            if ((ulong)data.Index >= committeeCount)
            {
                throw new ArgumentOutOfRangeException("attestation.Data.Index", data.Index, $"Attestation data committee index must be less that the committee count {committeeCount}.");
            }
            var previousEpoch = _beaconStateAccessor.GetPreviousEpoch(state);
            var currentEpoch = _beaconStateAccessor.GetCurrentEpoch(state);
            if (data.Target.Epoch != previousEpoch && data.Target.Epoch != currentEpoch)
            {
                throw new ArgumentOutOfRangeException("attestation.Data.Target.Epoch", data.Target.Epoch, $"Attestation data target epoch must be either the previous epoch {previousEpoch} or the current epoch {currentEpoch}.");
            }
            var minimumSlot = data.Slot + timeParameters.MinimumAttestationInclusionDelay;
            var maximumSlot = data.Slot + timeParameters.SlotsPerEpoch;
            if (state.Slot < minimumSlot)
            {
                throw new Exception($"Current state slot {state.Slot} must be equal or greater than the attestion slot {data.Slot} plus minimum delay {timeParameters.MinimumAttestationInclusionDelay}.");
            }
            if (state.Slot > maximumSlot)
            {
                throw new Exception($"Current state slot {state.Slot} must be equal or less than the attestation slot {data.Slot} plus slots per epoch {timeParameters.SlotsPerEpoch}.");
            }

            var committee = _beaconStateAccessor.GetBeaconCommittee(state, data.Slot, data.Index);
            if (attestation.AggregationBits.Count != attestation.CustodyBits.Count)
            {
                throw new Exception($"The attestation aggregation bit length {attestation.AggregationBits.Count} must be the same as the custody bit length {attestation.CustodyBits.Count}.");
            }
            if (attestation.AggregationBits.Count != committee.Count)
            {
                throw new Exception($"The attestation aggregation bit (and custody bit) length {attestation.AggregationBits.Count} must be the same as the committee length {committee.Count}.");
            }

            var inclusionDelay = state.Slot - data.Slot;
            var proposerIndex = _beaconStateAccessor.GetBeaconProposerIndex(state);
            var pendingAttestation = new PendingAttestation(attestation.AggregationBits, data, inclusionDelay, proposerIndex);

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
            var indexedAttestation = _beaconStateAccessor.GetIndexedAttestation(state, attestation);
            var domain = _beaconStateAccessor.GetDomain(state, DomainType.BeaconAttester, data.Target.Epoch);
            var isValid = _beaconChainUtility.IsValidIndexedAttestation(state, indexedAttestation, domain);
            if (!isValid)
            {
                throw new Exception($"Indexed attestation {indexedAttestation} is not valid.");
            }
        }

        public void ProcessAttesterSlashing(BeaconState state, AttesterSlashing attesterSlashing)
        {
            _logger.LogInformation(Event.ProcessAttesterSlashing, "Process block operation attester slashing {AttesterSlashing}", attesterSlashing);
            var attestation1 = attesterSlashing.Attestation1;
            var attestation2 = attesterSlashing.Attestation2;

            var isSlashableAttestationData = _beaconChainUtility.IsSlashableAttestationData(attestation1.Data, attestation2.Data);
            if (!isSlashableAttestationData)
            {
                throw new Exception("Attestation data must be slashable.");
            }

            var epoch1 = attestation1.Data.Target.Epoch;
            var domain1 = _beaconStateAccessor.GetDomain(state, DomainType.BeaconAttester, epoch1);
            var attestation1Valid = _beaconChainUtility.IsValidIndexedAttestation(state, attestation1, domain1);
            if (!attestation1Valid)
            {
                throw new Exception("Attestation 1 must be valid.");
            }

            var epoch2 = attestation2.Data.Target.Epoch;
            var domain2 = _beaconStateAccessor.GetDomain(state, DomainType.BeaconAttester, epoch2);
            var attestation2Valid = _beaconChainUtility.IsValidIndexedAttestation(state, attestation2, domain2);
            if (!attestation2Valid)
            {
                throw new Exception("Attestation 2 must be valid.");
            }

            var slashedAny = false;
            var attestingIndices1 = attestation1.CustodyBit0Indices.Union(attestation1.CustodyBit1Indices);
            var attestingIndices2 = attestation2.CustodyBit0Indices.Union(attestation2.CustodyBit1Indices);

            var intersection = attestingIndices1.Intersect(attestingIndices2);
            var currentEpoch = _beaconStateAccessor.GetCurrentEpoch(state);
            foreach (var index in intersection.OrderBy(x => x))
            {
                var validator = state.Validators[(int)(ulong)index];
                var isSlashableValidator = _beaconChainUtility.IsSlashableValidator(validator, currentEpoch);
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
            _logger.LogInformation(Event.ProcessBlock, "Process block {BeaconBlock} for state {BeaconState}", block, state);
            ProcessBlockHeader(state, block);
            ProcessRandao(state, block.Body);
            ProcessEth1Data(state, block.Body);
            ProcessOperations(state, block.Body);
        }

        public void ProcessBlockHeader(BeaconState state, BeaconBlock block)
        {
            _logger.LogInformation(Event.ProcessBlock, "Process block header for block {BeaconBlock}", block);
            // Verify that the slots match
            if (block.Slot != state.Slot)
            {
                throw new ArgumentOutOfRangeException("block.Slot", block.Slot, $"Block slot must match state slot {state.Slot}.");
            }
            // Verify that the parent matches
            var latestBlockSigningRoot = state.LatestBlockHeader.SigningRoot();
            if (block.ParentRoot != latestBlockSigningRoot)
            {
                throw new ArgumentOutOfRangeException("block.ParentRoot", block.ParentRoot, $"Block parent root must match latest block header root {latestBlockSigningRoot}.");
            }

            // Save current block as the new latest block
            var bodyRoot = block.Body.HashTreeRoot(_miscellaneousParameterOptions.CurrentValue, _maxOperationsPerBlock.CurrentValue);
            var newBlockHeader = new BeaconBlockHeader(block.Slot,
                block.ParentRoot,
                Hash32.Zero, // `state_root` is zeroed and overwritten in the next `process_slot` call
                bodyRoot,
                new BlsSignature() //`signature` is zeroed
                );

            // Verify proposer is not slashed
            var beaconProposerIndex = _beaconStateAccessor.GetBeaconProposerIndex(state);
            var proposer = state.Validators[(int)(ulong)beaconProposerIndex];
            if (proposer.IsSlashed)
            {
                throw new Exception("Beacon proposer must not be slashed.");
            }

            // Verify proposer signature
            var signingRoot = block.SigningRoot(_miscellaneousParameterOptions.CurrentValue, _maxOperationsPerBlock.CurrentValue);
            var domain = _beaconStateAccessor.GetDomain(state, DomainType.BeaconProposer, Epoch.None);
            var validSignature = _cryptographyService.BlsVerify(proposer.PublicKey, signingRoot, block.Signature, domain);
            if (!validSignature)
            {
                throw new Exception($"Block signature must match proposer public key ${proposer.PublicKey}");
            }
        }

        public void ProcessDeposit(BeaconState state, Deposit deposit)
        {
            _logger.LogInformation(Event.ProcessDeposit, "Process block operation deposit {Deposit} for state {BeaconState}.", deposit, state);

            var gweiValues = _gweiValueOptions.CurrentValue;

            // Verify the Merkle branch
            var isValid = _beaconChainUtility.IsValidMerkleBranch(
                deposit.Data.HashTreeRoot(),
                deposit.Proof,
                _chainConstants.DepositContractTreeDepth + 1, // Add 1 for the 'List' length mix-in
                state.Eth1DepositIndex,
                state.Eth1Data.DepositRoot);
            if (!isValid)
            {
                throw new Exception($"Invalid Merle branch for deposit for validator poublic key {deposit.Data.PublicKey}");
            }

            // Deposits must be processed in order
            state.IncreaseEth1DepositIndex();

            var publicKey = deposit.Data.PublicKey;
            var amount = deposit.Data.Amount;
            var validatorPublicKeys = state.Validators.Select(x => x.PublicKey).ToList();

            if (!validatorPublicKeys.Contains(publicKey))
            {
                // Verify the deposit signature (proof of possession) for new validators
                // Note: The deposit contract does not check signatures.
                // Note: Deposits are valid across forks, thus the deposit domain is retrieved directly from 'computer_domain'.

                var domain = _beaconChainUtility.ComputeDomain(DomainType.Deposit);
                if (!_cryptographyService.BlsVerify(publicKey, deposit.Data.SigningRoot(), deposit.Data.Signature, domain))
                {
                    return;
                }

                var effectiveBalance = Gwei.Min(amount - (amount % gweiValues.EffectiveBalanceIncrement), gweiValues.MaximumEffectiveBalance);
                var newValidator = new Validator(
                    publicKey,
                    deposit.Data.WithdrawalCredentials,
                    effectiveBalance
,
                    _chainConstants.FarFutureEpoch,
                    _chainConstants.FarFutureEpoch,
                    _chainConstants.FarFutureEpoch,
                    _chainConstants.FarFutureEpoch);
                state.AddValidatorWithBalance(newValidator, amount);
            }
            else
            {
                var index = (ValidatorIndex)(ulong)validatorPublicKeys.IndexOf(publicKey);
                _beaconStateMutator.IncreaseBalance(state, index, amount);
            }
        }

        public void ProcessEpoch(BeaconState state)
        {
            _logger.LogInformation(Event.ProcessEpoch, "Process end of epoch for state {BeaconState}", state);
            ProcessJustificationAndFinalization(state);

            // Was removed from phase 0 spec
            //ProcessCrosslinks(state);

            ProcessRewardsAndPenalties(state);
            // ProcessRegistryUpdates(state);

            //# @process_reveal_deadlines
            //# @process_challenge_deadlines

            // ProcessSlashings(state);

            //# @update_period_committee

            // ProcessFinalUpdates(state);

            //# @after_process_final_updates

            // throw new NotImplementedException();
        }

        public void ProcessEth1Data(BeaconState state, BeaconBlockBody body)
        {
            _logger.LogInformation(Event.ProcessEth1Data, "Process block ETH1 data for block body {BeaconBlockBody}", body);

            state.AppendEth1DataVotes(body.Eth1Data);
            var eth1DataVoteCount = state.Eth1DataVotes.Count(x => x.Equals(body.Eth1Data));
            if (eth1DataVoteCount * 2 > (int)(ulong)_timeParameterOptions.CurrentValue.SlotsPerEth1VotingPeriod)
            {
                state.SetEth1Data(body.Eth1Data);
            }
        }

        public void ProcessJustificationAndFinalization(BeaconState state)
        {
            _logger.LogInformation(Event.ProcessJustificationAndFinalization, "Process epoch justification and finalization state {BeaconState}", state);
            var currentEpoch = _beaconStateAccessor.GetCurrentEpoch(state);
            if (currentEpoch <= _initialValueOptions.CurrentValue.GenesisEpoch + new Epoch(1))
            {
                return;
                //throw new ArgumentOutOfRangeException(nameof(state), currentEpoch, "Current epoch of state must be more than one away from genesis epoch.");
            }

            var previousEpoch = _beaconStateAccessor.GetPreviousEpoch(state);
            var oldPreviousJustifiedCheckpoint = state.PreviousJustifiedCheckpoint;
            var oldCurrentJustifiedCheckpoint = state.CurrentJustifiedCheckpoint;

            // Process justifications
            state.SetPreviousJustifiedCheckpoint(state.CurrentJustifiedCheckpoint);
            state.JustificationBitsShift();

            // Previous Epoch
            var matchingTargetAttestationsPreviousEpoch = GetMatchingTargetAttestations(state, previousEpoch);
            var attestingBalancePreviousEpoch = GetAttestingBalance(state, matchingTargetAttestationsPreviousEpoch);
            var totalActiveBalance = _beaconStateAccessor.GetTotalActiveBalance(state);
            if (attestingBalancePreviousEpoch * 3 >= totalActiveBalance * 2)
            {
                var blockRoot = _beaconStateAccessor.GetBlockRoot(state, previousEpoch);
                var checkpoint = new Checkpoint(previousEpoch, blockRoot);
                state.SetCurrentJustifiedCheckpoint(checkpoint);
                state.JustificationBits.Set(1, true);
            }

            // Current Epoch
            var matchingTargetAttestationsCurrentEpoch = GetMatchingTargetAttestations(state, currentEpoch);
            var attestingBalanceCurrentEpoch = GetAttestingBalance(state, matchingTargetAttestationsCurrentEpoch);
            if (attestingBalanceCurrentEpoch * 3 >= totalActiveBalance * 2)
            {
                var blockRoot = _beaconStateAccessor.GetBlockRoot(state, currentEpoch);
                var checkpoint = new Checkpoint(currentEpoch, blockRoot);
                state.SetCurrentJustifiedCheckpoint(checkpoint);
                state.JustificationBits.Set(0, true);
            }

            // Process finalizations
            var bits = state.JustificationBits;
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
            _logger.LogInformation(Event.ProcessOperations, "Process block operations for block body {BeaconBlockBody}", body);
            // Verify that outstanding deposits are processed up to the maximum number of deposits
            var outstandingDeposits = state.Eth1Data.DepositCount - state.Eth1DepositIndex;
            var expectedDeposits = Math.Min(_maxOperationsPerBlock.CurrentValue.MaximumDeposits, outstandingDeposits);
            if (body.Deposits.Count != (int)expectedDeposits)
            {
                throw new ArgumentOutOfRangeException("body.Deposits.Count", body.Deposits.Count, $"Block body does not have the expected number of outstanding deposits {expectedDeposits}.");
            }

            foreach (var proposerSlashing in body.ProposerSlashings)
            {
                ProcessProposerSlashing(state, proposerSlashing);
            }
            foreach (var attesterSlashing in body.AttesterSlashings)
            {
                ProcessAttesterSlashing(state, attesterSlashing);
            }
            foreach (var attestation in body.Attestations)
            {
                ProcessAttestation(state, attestation);
            }
            foreach (var deposit in body.Deposits)
            {
                ProcessDeposit(state, deposit);
            }
            foreach (var voluntaryExit in body.VoluntaryExits)
            {
                ProcessVoluntaryExit(state, voluntaryExit);
            }
            //ProcessShareReceiptProof();
        }

        public void ProcessProposerSlashing(BeaconState state, ProposerSlashing proposerSlashing)
        {
            _logger.LogInformation(Event.ProcessProposerSlashing, "Process block operation proposer slashing {ProposerSlashing}", proposerSlashing);
            var proposer = state.Validators[(int)(ulong)proposerSlashing.ProposerIndex];
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
            var currentEpoch = _beaconStateAccessor.GetCurrentEpoch(state);
            var isSlashable = _beaconChainUtility.IsSlashableValidator(proposer, currentEpoch);
            if (!isSlashable)
            {
                throw new Exception($"Proposer {proposerSlashing.ProposerIndex} is not slashable at epoch {currentEpoch}.");
            }
            // Signatures are valid
            var slashingEpoch = _beaconChainUtility.ComputeEpochAtSlot(proposerSlashing.Header1.Slot);
            var domain = _beaconStateAccessor.GetDomain(state, DomainType.BeaconProposer, slashingEpoch);

            var signingRoot1 = proposerSlashing.Header1.SigningRoot();
            var signature1 = proposerSlashing.Header1.Signature;
            var header1Valid = _cryptographyService.BlsVerify(proposer.PublicKey, signingRoot1, signature1, domain);
            if (!header1Valid)
            {
                throw new Exception("Proposer slashing header 1 signature is not valid.");
            }

            var signingRoot2 = proposerSlashing.Header2.SigningRoot();
            var signature2 = proposerSlashing.Header2.Signature;
            var header2Valid = _cryptographyService.BlsVerify(proposer.PublicKey, signingRoot2, signature2, domain);
            if (!header2Valid)
            {
                throw new Exception("Proposer slashing header 2 signature is not valid.");
            }

            _beaconStateMutator.SlashValidator(state, proposerSlashing.ProposerIndex, ValidatorIndex.None);
        }

        public void ProcessRandao(BeaconState state, BeaconBlockBody body)
        {
            _logger.LogInformation(Event.ProcessRandao, "Process block randao for block body {BeaconBlockBody}", body);
            var epoch = _beaconStateAccessor.GetCurrentEpoch(state);
            // Verify RANDAO reveal
            var beaconProposerIndex = _beaconStateAccessor.GetBeaconProposerIndex(state);
            var proposer = state.Validators[(int)(ulong)beaconProposerIndex];
            var epochRoot = epoch.HashTreeRoot();
            var domain = _beaconStateAccessor.GetDomain(state, DomainType.Randao, Epoch.None);
            var validRandaoReveal = _cryptographyService.BlsVerify(proposer.PublicKey, epochRoot, body.RandaoReveal, domain);
            if (!validRandaoReveal)
            {
                throw new Exception($"Randao reveal {body.RandaoReveal} must match proposer public key {proposer.PublicKey}");
            }
            // Mix in RANDAO reveal
            var randaoMix = _beaconStateAccessor.GetRandaoMix(state, epoch);
            var randaoHash = _cryptographyService.Hash(body.RandaoReveal.AsSpan());
            var mix = randaoMix.Xor(randaoHash);
            var randaoIndex = epoch % _stateListLengthOptions.CurrentValue.EpochsPerHistoricalVector;
            state.SetRandaoMix(randaoIndex, mix);
        }

        public void ProcessRewardsAndPenalties(BeaconState state)
        {
            _logger.LogInformation(Event.ProcessJustificationAndFinalization, "Process epoch rewards and penalties state {BeaconState}", state);

            var currentEpoch = _beaconStateAccessor.GetCurrentEpoch(state);
            if (currentEpoch == _initialValueOptions.CurrentValue.GenesisEpoch)
            {
                return;
            }

            throw new NotImplementedException();
        }

        public void ProcessSlot(BeaconState state)
        {
            _logger.LogInformation(Event.ProcessSlot, "Process current slot for state {BeaconState}", state);
            // Cache state root
            var previousStateRoot = state.HashTreeRoot(_miscellaneousParameterOptions.CurrentValue, _timeParameterOptions.CurrentValue,
                _stateListLengthOptions.CurrentValue, _maxOperationsPerBlock.CurrentValue);
            var previousRootIndex = state.Slot % _timeParameterOptions.CurrentValue.SlotsPerHistoricalRoot;
            state.SetStateRoot(previousRootIndex, previousStateRoot);
            // Cache latest block header state root
            if (state.LatestBlockHeader.StateRoot == Hash32.Zero)
            {
                state.LatestBlockHeader.SetStateRoot(previousStateRoot);
            }
            // Cache block root
            var previousBlockRoot = state.LatestBlockHeader.SigningRoot();
            state.SetBlockRoot(previousRootIndex, previousBlockRoot);
        }

        public void ProcessSlots(BeaconState state, Slot slot)
        {
            _logger.LogInformation(Event.ProcessSlots, "Process slots to {Slot} for state {BeaconState}", slot, state);
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
            _logger.LogInformation(Event.ProcessVoluntaryExit, "Process block operation voluntary exit {VoluntaryExit} for state {BeaconState}.", exit, state);

            var validator = state.Validators[(int)(ulong)exit.ValidatorIndex];

            //#Verify the validator is active
            var currentEpoch = _beaconStateAccessor.GetCurrentEpoch(state);
            var isActiveValidator = _beaconChainUtility.IsActiveValidator(validator, currentEpoch);
            if (!isActiveValidator)
            {
                throw new Exception($"Validator {exit.ValidatorIndex} must be active in order to exit.");
            }

            //# Verify the validator has not yet exited
            var hasExited = validator.ExitEpoch != _chainConstants.FarFutureEpoch;
            if (hasExited)
            {
                throw new Exception($"Validator {exit.ValidatorIndex} already has exit epoch {validator.ExitEpoch}.");
            }

            //# Exits must specify an epoch when they become valid; they are not valid before then
            var isCurrentAtOrAfterExit = currentEpoch >= exit.Epoch;
            if (!isCurrentAtOrAfterExit)
            {
                throw new Exception($"Validator {exit.ValidatorIndex} can not exit because the current epoch {currentEpoch} has not yet reached their exit epoch {validator.ExitEpoch}.");
            }

            //# Verify the validator has been active long enough
            var timeParameters = _timeParameterOptions.CurrentValue;
            var minimumActiveEpoch = validator.ActivationEpoch + timeParameters.PersistentCommitteePeriod;
            var isCurrentAtOrAfterMinimum = currentEpoch >= minimumActiveEpoch;
            if (!isCurrentAtOrAfterMinimum)
            {
                throw new Exception($"Validator {exit.ValidatorIndex} can not exit because the current epoch {currentEpoch} has not yet reached the minimum active epoch of {timeParameters.PersistentCommitteePeriod} after their activation epoch {validator.ActivationEpoch}.");
            }

            //# Verify signature
            var domain = _beaconStateAccessor.GetDomain(state, DomainType.VoluntaryExit, exit.Epoch);
            var signingRoot = exit.SigningRoot();
            var validSignature = _cryptographyService.BlsVerify(validator.PublicKey, signingRoot, exit.Signature, domain);
            if (!validSignature)
            {
                throw new Exception("Voluntary exit signature is not valid.");
            }

            //# Initiate exit
            _beaconStateMutator.InitiateValidatorExit(state, exit.ValidatorIndex);
        }

        public BeaconState StateTransition(BeaconState state, BeaconBlock block, bool validateStateRoot)
        {
            _logger.LogInformation(Event.ProcessSlots, "State transition for state {BeaconState} with block {BeaconBlock}; validating {ValidateStateRoot}.",
                state, block, validateStateRoot);

            // Process slots (including those with no blocks) since block
            ProcessSlots(state, block.Slot);
            // Process block
            ProcessBlock(state, block);
            // Validate state root (True in production)
            if (validateStateRoot)
            {
                var checkStateRoot = state.HashTreeRoot(_miscellaneousParameterOptions.CurrentValue, _timeParameterOptions.CurrentValue, _stateListLengthOptions.CurrentValue, _maxOperationsPerBlock.CurrentValue);
                if (block.StateRoot != checkStateRoot)
                {
                    throw new Exception("Mismatch between calculated state root and block state root.");
                }
            }
            return state;
        }
    }
}
