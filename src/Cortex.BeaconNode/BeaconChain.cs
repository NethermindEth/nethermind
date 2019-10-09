using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Cortex.BeaconNode.Ssz;
using Cortex.Containers;
using Cortex.SimpleSerialize;
using Microsoft.Extensions.Logging;

namespace Cortex.BeaconNode
{
    public class BeaconChain
    {
        private readonly ILogger _logger;
        private readonly BeaconChainParameters _beaconChainParameters;
        private readonly InitialValues _initialValues;
        private readonly TimeParameters _timeParameters;
        private readonly MaxOperationsPerBlock _maxOperationsPerBlock;

        public BeaconChain(ILogger<BeaconChain> logger, 
            BeaconChainParameters beaconChainParameters,
            InitialValues initialValues,
            TimeParameters timeParameters,
            MaxOperationsPerBlock maxOperationsPerBlock)
        {
            _logger = logger;
            _beaconChainParameters = beaconChainParameters;
            _initialValues = initialValues;
            _timeParameters = timeParameters;
            _maxOperationsPerBlock = maxOperationsPerBlock;
        }

        public BeaconState State { get; }

        public async Task<bool> TryGenesisAsync(byte[] eth1BlockHash, ulong eth1Timestamp, IList<Deposit> deposits)
        {
            var candidateState = InitializeBeaconStateFromEth1(eth1BlockHash, eth1Timestamp, deposits);

            // if is_valid_genesis_state(candidate_state) then genesis_state = candidate_state
            // store = get_genesis_store(genesis_state)
            //         genesis_block = BeaconBlock(state_root=hash_tree_root(genesis_state))
            return false;
        }

        public BeaconState InitializeBeaconStateFromEth1(ReadOnlySpan<byte> eth1BlockHash, ulong eth1Timestamp, IList<Deposit> deposits)
        {
            var genesisTime = eth1Timestamp - (eth1Timestamp % _timeParameters.SecondsPerDay) + (2 * _timeParameters.SecondsPerDay);
            var eth1Data = new Eth1Data(eth1BlockHash, (ulong)deposits.Count);
            var latestBlockHeader = new BeaconBlockHeader(HashTreeRoot(new BeaconBlockBody()));
            var state = new BeaconState(genesisTime, eth1Data, latestBlockHeader);

            // Process deposits
            // TODO:

            // Process activations
            // TODO:

            return state;
        }

        public bool IsValidGenesisState(BeaconState state)
        {
            if (state.GenesisTime < _beaconChainParameters.MinGenesisTime)
            {
                return false;
            }
            var activeValidatorIndices = state.GetActiveValidatorIndices(_initialValues.GenesisEpoch);
            if (activeValidatorIndices.Count < _beaconChainParameters.MinGenesisActiveValidatorCount)
            {
                return false;
            }
            return true;
        }

        private ReadOnlySpan<byte> HashTreeRoot(BeaconBlockBody beaconBlockBody)
        {
            var tree = new SszTree(beaconBlockBody.ToSszContainer(_maxOperationsPerBlock));
            return tree.HashTreeRoot();
        }

        // Update store via... (store processor ?)

        // on_tick

        // on_block(store, block)
        //          state_transition(pre_state, block)

        // on_attestation
    }
}
