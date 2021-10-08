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

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethermind.Core2;
using Nethermind.Core2.Configuration;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Nethermind.Logging.Microsoft;
using Nethermind.Merkleization;

namespace Nethermind.BeaconNode
{
    public class GenesisChainStart : IEth1Genesis
    {
        private readonly BeaconStateAccessor _beaconStateAccessor;
        private readonly BeaconStateTransition _beaconStateTransition;
        private readonly ChainConstants _chainConstants;
        private readonly ICryptographyService _cryptographyService;
        private readonly IForkChoice _forkChoice;
        private readonly IDepositStore _depositStore;
        private readonly IOptionsMonitor<GweiValues> _gweiValueOptions;
        private readonly IOptionsMonitor<InitialValues> _initialValueOptions;
        private readonly ILogger _logger;
        private readonly IOptionsMonitor<MiscellaneousParameters> _miscellaneousParameterOptions;
        private readonly IOptionsMonitor<StateListLengths> _stateListLengthOptions;
        private readonly IStore _store;
        private readonly IOptionsMonitor<TimeParameters> _timeParameterOptions;

        public GenesisChainStart(ILogger<GenesisChainStart> logger,
            ChainConstants chainConstants,
            IOptionsMonitor<MiscellaneousParameters> miscellaneousParameterOptions,
            IOptionsMonitor<GweiValues> gweiValueOptions,
            IOptionsMonitor<InitialValues> initialValueOptions,
            IOptionsMonitor<TimeParameters> timeParameterOptions,
            IOptionsMonitor<StateListLengths> stateListLengthOptions,
            ICryptographyService cryptographyService,
            IStore store,
            BeaconStateAccessor beaconStateAccessor,
            BeaconStateTransition beaconStateTransition,
            IForkChoice forkChoice,
            IDepositStore depositStore)
        {
            _logger = logger;
            _beaconStateAccessor = beaconStateAccessor;
            _beaconStateTransition = beaconStateTransition;
            _forkChoice = forkChoice;
            _depositStore = depositStore;
            _chainConstants = chainConstants;
            _miscellaneousParameterOptions = miscellaneousParameterOptions;
            _gweiValueOptions = gweiValueOptions;
            _initialValueOptions = initialValueOptions;
            _timeParameterOptions = timeParameterOptions;
            _stateListLengthOptions = stateListLengthOptions;
            _cryptographyService = cryptographyService;
            _store = store;
        }

        public BeaconState InitializeBeaconStateFromEth1(Bytes32 eth1BlockHash, ulong eth1Timestamp)
        {
            if (_logger.IsInfo())
                Log.InitializeBeaconState(_logger, eth1BlockHash, eth1Timestamp, (uint)_depositStore.Deposits.Count, null);

            InitialValues initialValues = _initialValueOptions.CurrentValue;
            GweiValues gweiValues = _gweiValueOptions.CurrentValue;
            TimeParameters timeParameters = _timeParameterOptions.CurrentValue;
            StateListLengths stateListLengths = _stateListLengthOptions.CurrentValue;

            Fork fork = new Fork(
                initialValues.GenesisForkVersion,
                initialValues.GenesisForkVersion,
                _chainConstants.GenesisEpoch);

            ulong genesisTime = eth1Timestamp - (eth1Timestamp % timeParameters.MinimumGenesisDelay)
                                + (2 * timeParameters.MinimumGenesisDelay);
            Eth1Data eth1Data = new Eth1Data(Root.Zero, (uint)_depositStore.Deposits.Count, eth1BlockHash);

            Root emptyBlockBodyRoot = _cryptographyService.HashTreeRoot(BeaconBlockBody.Zero);
            BeaconBlockHeader latestBlockHeader = new BeaconBlockHeader(emptyBlockBodyRoot);

            Bytes32[] randaoMixes = Enumerable.Repeat(eth1BlockHash, (int) stateListLengths.EpochsPerHistoricalVector)
                .ToArray();

            BeaconState state = new BeaconState(genesisTime, fork, eth1Data, latestBlockHeader, randaoMixes,
                timeParameters.SlotsPerHistoricalRoot, stateListLengths.EpochsPerHistoricalVector,
                stateListLengths.EpochsPerSlashingsVector, _chainConstants.JustificationBitsLength);
            
            state.Eth1Data.SetDepositRoot(_depositStore.DepositData.Root);
            foreach (Deposit deposit in _depositStore.Deposits)
            {
                _beaconStateTransition.ProcessDeposit(state, deposit);
            }

            // Process activations
            for (int validatorIndex = 0; validatorIndex < state.Validators.Count; validatorIndex++)
            {
                Validator validator = state.Validators[validatorIndex];
                Gwei balance = state.Balances[validatorIndex];
                Gwei effectiveBalance = Gwei.Min(balance - (balance % gweiValues.EffectiveBalanceIncrement),
                    gweiValues.MaximumEffectiveBalance);
                validator.SetEffectiveBalance(effectiveBalance);
                if (validator.EffectiveBalance == gweiValues.MaximumEffectiveBalance)
                {
                    validator.SetEligible(_chainConstants.GenesisEpoch);
                    validator.SetActive(_chainConstants.GenesisEpoch);
                }
            }

            return state;
        }

        public bool IsValidGenesisState(BeaconState state)
        {
            MiscellaneousParameters miscellaneousParameters = _miscellaneousParameterOptions.CurrentValue;

            if (state.GenesisTime < miscellaneousParameters.MinimumGenesisTime)
            {
                return false;
            }

            IList<ValidatorIndex> activeValidatorIndices =
                _beaconStateAccessor.GetActiveValidatorIndices(state, _chainConstants.GenesisEpoch);
            if (activeValidatorIndices.Count < miscellaneousParameters.MinimumGenesisActiveValidatorCount)
            {
                return false;
            }

            return true;
        }

        /// <param name="eth1BlockHash"></param>
        /// <param name="eth1Timestamp"></param>
        /// <param name="deposits"></param>
        /// <returns></returns>
        public async Task<bool> TryGenesisAsync(Bytes32 eth1BlockHash, ulong eth1Timestamp)
        {
            if (_logger.IsDebug()) LogDebug.TryGenesis(_logger, eth1BlockHash, eth1Timestamp, (uint)_depositStore.Deposits.Count, null);

            BeaconState candidateState = InitializeBeaconStateFromEth1(eth1BlockHash, eth1Timestamp);
            if (IsValidGenesisState(candidateState))
            {
                BeaconState genesisState = candidateState;
                await _forkChoice.InitializeForkChoiceStoreAsync(_store, genesisState);
                return true;
            }

            return false;
        }
    }
}