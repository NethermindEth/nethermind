using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cortex.BeaconNode.Configuration;
using Cortex.BeaconNode.Ssz;
using Cortex.Containers;
using Microsoft.Extensions.Logging;

namespace Cortex.BeaconNode
{
    public class BeaconChain
    {
        // Constants
        //private const ulong DEPOSIT_CONTRACT_LIMIT = (ulong)1 << DEPOSIT_CONTRACT_TREE_DEPTH;
        //private const int DEPOSIT_CONTRACT_TREE_DEPTH = 1 << 5; // 2 ** 5
        //private static readonly Epoch FAR_FUTURE_EPOCH = new Epoch((ulong)1 << 64 - 1);

        //private static readonly Gwei EFFECTIVE_BALANCE_INCREMENT = new Gwei(1000 * 1000 * 1000); // (2 ** 0) * (10 ** 9)
        //private static readonly Gwei MAX_EFFECTIVE_BALANCE = new Gwei(((ulong)1 << 5) * 1000 * 1000 * 1000); // (2 ** 5) * (10 ** 9)

        private readonly MiscellaneousParameters _miscellaneousParameters;
        private readonly GweiValues _gweiValues;
        private readonly BeaconChainUtility _beaconChainUtility;
        private readonly ChainConstants _chainConstants;
        private readonly ICryptographyService _blsSignatureService;
        private readonly InitialValues _initialValues;

        // 1,000,000,000
        private readonly ILogger _logger;

        private readonly MaxOperationsPerBlock _maxOperationsPerBlock;
        private readonly TimeParameters _timeParameters;
        private readonly StateListLengths _stateListLengths;

        public BeaconChain(ILogger<BeaconChain> logger,
            ICryptographyService blsSignatureService,
            BeaconChainUtility beaconChainUtility,
            ChainConstants chainConstants,
            MiscellaneousParameters miscellaneousParameters,
            GweiValues gweiValues,
            InitialValues initialValues,
            TimeParameters timeParameters,
            StateListLengths stateListLengths,
            MaxOperationsPerBlock maxOperationsPerBlock)
        {
            _logger = logger;
            _blsSignatureService = blsSignatureService;
            _beaconChainUtility = beaconChainUtility;
            _chainConstants = chainConstants;
            _miscellaneousParameters = miscellaneousParameters;
            _gweiValues = gweiValues;
            _initialValues = initialValues;
            _timeParameters = timeParameters;
            _stateListLengths = stateListLengths;
            _maxOperationsPerBlock = maxOperationsPerBlock;
        }

        public BeaconBlock? GenesisBlock { get; private set; }
        public BeaconState? State { get; private set; }

        public BeaconState InitializeBeaconStateFromEth1(Hash32 eth1BlockHash, ulong eth1Timestamp, IEnumerable<Deposit> deposits)
        {
            var genesisTime = eth1Timestamp - (eth1Timestamp % _chainConstants.SecondsPerDay) + (2 * _chainConstants.SecondsPerDay);
            var eth1Data = new Eth1Data(eth1BlockHash, (ulong)deposits.Count());
            var emptyBlockBody = new BeaconBlockBody();
            var latestBlockHeader = new BeaconBlockHeader(emptyBlockBody.HashTreeRoot(_maxOperationsPerBlock));
            var state = new BeaconState(genesisTime, eth1Data, latestBlockHeader);

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
                var effectiveBalance = Gwei.Min(balance - (balance % _gweiValues.EffectiveBalanceIncrement), _gweiValues.MaximumEffectiveBalance);
                validator.SetEffectiveBalance(effectiveBalance);
                if (validator.EffectiveBalance == _gweiValues.MaximumEffectiveBalance)
                {
                    validator.SetEligible(_initialValues.GenesisEpoch);
                    validator.SetActive(_initialValues.GenesisEpoch);
                }
            }

            return state;
        }

        public bool IsValidGenesisState(BeaconState state)
        {
            if (state.GenesisTime < _miscellaneousParameters.MinimumGenesisTime)
            {
                return false;
            }
            var activeValidatorIndices = state.GetActiveValidatorIndices(_initialValues.GenesisEpoch);
            if (activeValidatorIndices.Count < _miscellaneousParameters.MinimumGenesisActiveValidatorCount)
            {
                return false;
            }
            return true;
        }

        public void ProcessDeposit(BeaconState state, Deposit deposit)
        {
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

                var effectiveBalance = Gwei.Min(amount - (amount % _gweiValues.EffectiveBalanceIncrement), _gweiValues.MaximumEffectiveBalance);
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
                var candidateState = InitializeBeaconStateFromEth1(eth1BlockHash, eth1Timestamp, deposits);
                if (IsValidGenesisState(candidateState))
                {
                    var genesisState = candidateState;
                    GenesisBlock = new BeaconBlock(genesisState.HashTreeRoot(_stateListLengths));
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
