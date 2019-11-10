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
        private readonly ICryptographyService _cryptographyService;
        private readonly IOptionsMonitor<InitialValues> _initialValueOptions;
        private readonly ILogger _logger;
        private readonly IOptionsMonitor<MaxOperationsPerBlock> _maxOperationsPerBlock;
        private readonly IOptionsMonitor<MiscellaneousParameters> _miscellaneousParameterOptions;
        private readonly IOptionsMonitor<StateListLengths> _stateListLengthOptions;
        private readonly IOptionsMonitor<TimeParameters> _timeParameterOptions;

        public BeaconStateTransition(ILogger<BeaconStateTransition> logger,
            IOptionsMonitor<MiscellaneousParameters> miscellaneousParameterOptions,
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
            _miscellaneousParameterOptions = miscellaneousParameterOptions;
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

        public void ProcessAttesterSlashing(BeaconState state, AttesterSlashing attesterSlashing)
        {
            _logger.LogInformation(Event.ProcessAttesterSlashing, "Process attester slashing {AttesterSlashing}", attesterSlashing);
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
            _logger.LogInformation(Event.ProcessBlock, "Process block {BeaconBlock}", block);
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

        public void ProcessEpoch(BeaconState state)
        {
            _logger.LogInformation(Event.ProcessEpoch, "Process end of epoch for state {BeaconState}", state);
            ProcessJustificationAndFinalization(state);

            // Did this change ???
            //ProcessCrosslinks(state);

            // TODO:
            // ProcessRewardsAndPenalties(state);
            // ProcessRegistryUpdates(state);
            // ProcessSlashings(state);
            // ProcessFinalUpdates(state);

            // throw new NotImplementedException();
        }

        public void ProcessEth1Data(BeaconState state, BeaconBlockBody body)
        {
            _logger.LogInformation(Event.ProcessEth1Data, "Process ETH1 data for block body {BeaconBlockBody}", body);

            state.AppendEth1DataVotes(body.Eth1Data);
            var eth1DataVoteCount = state.Eth1DataVotes.Count(x => x.Equals(body.Eth1Data));
            if (eth1DataVoteCount * 2 > (int)(ulong)_timeParameterOptions.CurrentValue.SlotsPerEth1VotingPeriod)
            {
                state.SetEth1Data(body.Eth1Data);
            }
        }

        public void ProcessJustificationAndFinalization(BeaconState state)
        {
            _logger.LogInformation(Event.ProcessJustificationAndFinalization, "Process justification and finalization state {BeaconState}", state);
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
            _logger.LogInformation(Event.ProcessOperations, "Process operations for block body {BeaconBlockBody}", body);
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
            //ProcessAttestation();
            //ProcessDeposit();
            //ProcessVoluntaryExit();
            //ProcessShareReceiptProof();
        }

        public void ProcessProposerSlashing(BeaconState state, ProposerSlashing proposerSlashing)
        {
            _logger.LogInformation(Event.ProcessProposerSlashing, "Process proposer slashing {ProposerSlashing}", proposerSlashing);
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
            _logger.LogInformation(Event.ProcessRandao, "Process randao for block body {BeaconBlockBody}", body);
            var epoch = _beaconStateAccessor.GetCurrentEpoch(state);
            // Verify RANDAO reveal
            var beaconProposerIndex = _beaconStateAccessor.GetBeaconProposerIndex(state);
            var proposer = state.Validators[(int)(ulong)beaconProposerIndex];
            var epochRoot = epoch.HashTreeRoot();
            var domain = _beaconStateAccessor.GetDomain(state, DomainType.Randao, Epoch.None);
            var validRandaoReveal = _cryptographyService.BlsVerify(proposer.PublicKey, epochRoot, body.RandaoReveal, domain);
            if (!validRandaoReveal)
            {
                throw new Exception($"Randao reveal must match proposer public key ${proposer.PublicKey}");
            }
            // Mix in RANDAO reveal
            var randaoMix = _beaconStateAccessor.GetRandaoMix(state, epoch);
            var randaoHash = _cryptographyService.Hash(body.RandaoReveal.AsSpan());
            var mix = randaoMix.Xor(randaoHash);
            var randaoIndex = epoch % _stateListLengthOptions.CurrentValue.EpochsPerHistoricalVector;
            state.SetRandaoMix(randaoIndex, mix);
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

        public BeaconState StateTransition(BeaconState state, BeaconBlock block, bool validateStateRoot)
        {
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
