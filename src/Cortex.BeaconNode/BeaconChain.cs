using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cortex.BeaconNode.Configuration;
using Cortex.BeaconNode.Ssz;
using Cortex.Containers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cortex.BeaconNode
{
    public class BeaconChain
    {
        private readonly BeaconChainUtility _beaconChainUtility;
        private readonly BeaconStateAccessor _beaconStateAccessor;
        private readonly BeaconStateTransition _beaconStateTransition;
        private readonly BeaconStateMutator _beaconStateMutator;
        private readonly ICryptographyService _cryptographyService;
        private readonly ChainConstants _chainConstants;
        private readonly IOptionsMonitor<GweiValues> _gweiValueOptions;
        private readonly IOptionsMonitor<InitialValues> _initialValueOptions;
        private readonly ILogger _logger;
        private readonly IOptionsMonitor<MaxOperationsPerBlock> _maxOperationsPerBlockOptions;
        private readonly IOptionsMonitor<MiscellaneousParameters> _miscellaneousParameterOptions;
        private readonly IOptionsMonitor<StateListLengths> _stateListLengthOptions;
        private readonly IOptionsMonitor<TimeParameters> _timeParameterOptions;

        public BeaconChain(ILogger<BeaconChain> logger,
            ChainConstants chainConstants,
            IOptionsMonitor<MiscellaneousParameters> miscellaneousParameterOptions,
            IOptionsMonitor<GweiValues> gweiValueOptions,
            IOptionsMonitor<InitialValues> initialValueOptions,
            IOptionsMonitor<TimeParameters> timeParameterOptions,
            IOptionsMonitor<StateListLengths> stateListLengthOptions,
            IOptionsMonitor<MaxOperationsPerBlock> maxOperationsPerBlockOptions,
            ICryptographyService blsSignatureService,
            BeaconChainUtility beaconChainUtility,
            BeaconStateAccessor beaconStateAccessor,
            BeaconStateMutator beaconStateMutator,
            BeaconStateTransition beaconStateTransition)
        {
            _logger = logger;
            _cryptographyService = blsSignatureService;
            _beaconChainUtility = beaconChainUtility;
            _beaconStateAccessor = beaconStateAccessor;
            _beaconStateTransition = beaconStateTransition;
            _beaconStateMutator = beaconStateMutator;
            _chainConstants = chainConstants;
            _miscellaneousParameterOptions = miscellaneousParameterOptions;
            _gweiValueOptions = gweiValueOptions;
            _initialValueOptions = initialValueOptions;
            _timeParameterOptions = timeParameterOptions;
            _stateListLengthOptions = stateListLengthOptions;
            _maxOperationsPerBlockOptions = maxOperationsPerBlockOptions;
        }

        public BeaconBlock? GenesisBlock { get; private set; }
        public BeaconState? State { get; private set; }

        public BeaconState InitializeBeaconStateFromEth1(Hash32 eth1BlockHash, ulong eth1Timestamp, IEnumerable<Deposit> deposits)
        {
            _logger.LogDebug(Event.InitializeBeaconState, "Initialise beacon state from ETH1 block {Eth1BlockHash}, time {Eth1Timestamp}, with {DepositCount} deposits.", eth1BlockHash, eth1Timestamp, deposits.Count());

            var gweiValues = _gweiValueOptions.CurrentValue;
            var initialValues = _initialValueOptions.CurrentValue;
            var timeParameters = _timeParameterOptions.CurrentValue;
            var stateListLengths = _stateListLengthOptions.CurrentValue;

            var genesisTime = eth1Timestamp - (eth1Timestamp % _chainConstants.SecondsPerDay)
                + (2 * _chainConstants.SecondsPerDay);
            var eth1Data = new Eth1Data((ulong)deposits.Count(), eth1BlockHash);
            var emptyBlockBody = new BeaconBlockBody();
            var latestBlockHeader = new BeaconBlockHeader(emptyBlockBody.HashTreeRoot(_miscellaneousParameterOptions.CurrentValue, _maxOperationsPerBlockOptions.CurrentValue));
            var state = new BeaconState(genesisTime, 0, eth1Data, latestBlockHeader, timeParameters.SlotsPerHistoricalRoot, stateListLengths.EpochsPerHistoricalVector, stateListLengths.EpochsPerSlashingsVector, _chainConstants.JustificationBitsLength);

            // Process deposits
            var depositDataList = new List<DepositData>();
            foreach (var deposit in deposits)
            {
                depositDataList.Add(deposit.Data);
                var depositRoot = depositDataList.HashTreeRoot(_chainConstants.MaximumDepositContracts);
                state.Eth1Data.SetDepositRoot(depositRoot);
                _beaconStateTransition.ProcessDeposit(state, deposit);
            }

            // Process activations
            for (var validatorIndex = 0; validatorIndex < state.Validators.Count; validatorIndex++)
            {
                var validator = state.Validators[validatorIndex];
                var balance = state.Balances[validatorIndex];
                var effectiveBalance = Gwei.Min(balance - (balance % gweiValues.EffectiveBalanceIncrement), gweiValues.MaximumEffectiveBalance);
                validator.SetEffectiveBalance(effectiveBalance);
                if (validator.EffectiveBalance == gweiValues.MaximumEffectiveBalance)
                {
                    validator.SetEligible(initialValues.GenesisEpoch);
                    validator.SetActive(initialValues.GenesisEpoch);
                }
            }

            return state;
        }

        public bool IsValidGenesisState(BeaconState state)
        {
            var miscellaneousParameters = _miscellaneousParameterOptions.CurrentValue;
            var initialValues = _initialValueOptions.CurrentValue;

            if (state.GenesisTime < miscellaneousParameters.MinimumGenesisTime)
            {
                return false;
            }
            var activeValidatorIndices = _beaconStateAccessor.GetActiveValidatorIndices(state, initialValues.GenesisEpoch);
            if (activeValidatorIndices.Count < miscellaneousParameters.MinimumGenesisActiveValidatorCount)
            {
                return false;
            }
            return true;
        }

        public async Task<bool> TryGenesisAsync(Hash32 eth1BlockHash, ulong eth1Timestamp, IList<Deposit> deposits)
        {
            return await Task.Run(() =>
            {
                _logger.LogDebug(Event.TryGenesis, "Try genesis with ETH1 block {Eth1BlockHash}, time {Eth1Timestamp}, with {DepositCount} deposits.", eth1BlockHash, eth1Timestamp, deposits.Count);

                var candidateState = InitializeBeaconStateFromEth1(eth1BlockHash, eth1Timestamp, deposits);
                if (IsValidGenesisState(candidateState))
                {
                    var genesisState = candidateState;
                    GenesisBlock = new BeaconBlock(genesisState.HashTreeRoot(_miscellaneousParameterOptions.CurrentValue,
                        _timeParameterOptions.CurrentValue, _stateListLengthOptions.CurrentValue, _maxOperationsPerBlockOptions.CurrentValue));
                    State = genesisState;
                    return true;
                }
                return false;
            });
        }

        // Update store via... (store processor ?)

        // on_tick

        // on_block(store, block)
        //          state_transition(pre_state, block)

        // on_attestation
    }
}
