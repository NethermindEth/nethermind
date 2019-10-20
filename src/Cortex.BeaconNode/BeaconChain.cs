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
        private readonly ICryptographyService _blsSignatureService;
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
            BeaconChainUtility beaconChainUtility)
        {
            _logger = logger;
            _blsSignatureService = blsSignatureService;
            _beaconChainUtility = beaconChainUtility;
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
            var eth1Data = new Eth1Data(eth1BlockHash, (ulong)deposits.Count());
            var emptyBlockBody = new BeaconBlockBody();
            var latestBlockHeader = new BeaconBlockHeader(emptyBlockBody.HashTreeRoot(_maxOperationsPerBlockOptions.CurrentValue));
            var state = new BeaconState(genesisTime, 0, eth1Data, latestBlockHeader, timeParameters.SlotsPerHistoricalRoot, stateListLengths.EpochsPerHistoricalVector);

            // Process deposits
            var depositDataList = new List<DepositData>();
            foreach (var deposit in deposits)
            {
                depositDataList.Add(deposit.Data);
                var depositRoot = depositDataList.HashTreeRoot(_chainConstants.MaximumDepositContracts);
                state.Eth1Data.SetDepositRoot(depositRoot);
                ProcessDeposit(state, deposit);
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
            var activeValidatorIndices = state.GetActiveValidatorIndices(initialValues.GenesisEpoch);
            if (activeValidatorIndices.Count < miscellaneousParameters.MinimumGenesisActiveValidatorCount)
            {
                return false;
            }
            return true;
        }

        public void ProcessDeposit(BeaconState state, Deposit deposit)
        {
            _logger.LogDebug(Event.ProcessDeposit, "Process deposit {Deposit} for state {BeaconState}.", deposit, state);

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
                if (!_blsSignatureService.BlsVerify(publicKey, deposit.Data.SigningRoot(), deposit.Data.Signature, domain))
                {
                    return;
                }

                var effectiveBalance = Gwei.Min(amount - (amount % gweiValues.EffectiveBalanceIncrement), gweiValues.MaximumEffectiveBalance);
                var newValidator = new Validator(
                    publicKey,
                    deposit.Data.WithdrawalCredentials,
                    _chainConstants.FarFutureEpoch,
                    _chainConstants.FarFutureEpoch,
                    _chainConstants.FarFutureEpoch,
                    _chainConstants.FarFutureEpoch,
                    effectiveBalance
                    );
                state.AddValidatorWithBalance(newValidator, amount);
            }
            else
            {
                var index = (ValidatorIndex)(ulong)validatorPublicKeys.IndexOf(publicKey);
                state.IncreaseBalanceForValidator(index, amount);
            }
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
                    GenesisBlock = new BeaconBlock(genesisState.HashTreeRoot(_stateListLengthOptions.CurrentValue));
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
