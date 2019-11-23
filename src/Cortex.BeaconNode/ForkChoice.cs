using System.Collections.Generic;
using Cortex.BeaconNode.Configuration;
using Cortex.BeaconNode.Data;
using Cortex.BeaconNode.Ssz;
using Cortex.Containers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cortex.BeaconNode
{
    public class ForkChoice
    {
        private readonly IOptionsMonitor<InitialValues> _initialValueOptions;
        private readonly ILogger _logger;
        private readonly IOptionsMonitor<MaxOperationsPerBlock> _maxOperationsPerBlockOptions;
        private readonly IOptionsMonitor<MiscellaneousParameters> _miscellaneousParameterOptions;
        private readonly IOptionsMonitor<StateListLengths> _stateListLengthOptions;
        private readonly StoreProvider _storeProvider;
        private readonly IOptionsMonitor<TimeParameters> _timeParameterOptions;

        public ForkChoice(
            ILogger<ForkChoice> logger,
            IOptionsMonitor<MiscellaneousParameters> miscellaneousParameterOptions,
            IOptionsMonitor<InitialValues> initialValueOptions,
            IOptionsMonitor<TimeParameters> timeParameterOptions,
            IOptionsMonitor<StateListLengths> stateListLengthOptions,
            IOptionsMonitor<MaxOperationsPerBlock> maxOperationsPerBlockOptions,
            StoreProvider storeProvider)
        {
            _logger = logger;
            _miscellaneousParameterOptions = miscellaneousParameterOptions;
            _initialValueOptions = initialValueOptions;
            _timeParameterOptions = timeParameterOptions;
            _stateListLengthOptions = stateListLengthOptions;
            _maxOperationsPerBlockOptions = maxOperationsPerBlockOptions;
            _storeProvider = storeProvider;
        }

        public Slot GetCurrentSlot(IStore store)
        {
            var slotValue = (store.Time - store.GenesisTime) / _timeParameterOptions.CurrentValue.SecondsPerSlot;
            return new Slot(slotValue);
        }

        public IStore GetGenesisStore(BeaconState genesisState)
        {
            var miscellaneousParameters = _miscellaneousParameterOptions.CurrentValue;
            var maxOperationsPerBlock = _maxOperationsPerBlockOptions.CurrentValue;

            var stateRoot = genesisState.HashTreeRoot(miscellaneousParameters, _timeParameterOptions.CurrentValue, _stateListLengthOptions.CurrentValue, maxOperationsPerBlock);
            var genesisBlock = new BeaconBlock(stateRoot);
            var root = genesisBlock.SigningRoot(miscellaneousParameters, maxOperationsPerBlock);
            var justifiedCheckpoint = new Checkpoint(_initialValueOptions.CurrentValue.GenesisEpoch, root);
            var finalizedCheckpoint = new Checkpoint(_initialValueOptions.CurrentValue.GenesisEpoch, root);

            var blocks = new Dictionary<Hash32, BeaconBlock>
            {
                [root] = genesisBlock
            };
            var blockStates = new Dictionary<Hash32, BeaconState>
            {
                [root] = BeaconState.Clone(genesisState)
            };
            var checkpointStates = new Dictionary<Checkpoint, BeaconState>
            {
                [justifiedCheckpoint] = BeaconState.Clone(genesisState)
            };

            var store = _storeProvider.CreateStore(
                genesisState.GenesisTime,
                genesisState.GenesisTime,
                justifiedCheckpoint,
                finalizedCheckpoint,
                justifiedCheckpoint,
                blocks,
                blockStates,
                checkpointStates
                );
            return store;
        }

        public void OnTick(IStore store, ulong time)
        {
            var previousSlot = GetCurrentSlot(store);

            store.SetTime(time);
        }
    }
}
