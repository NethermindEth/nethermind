using System.Collections.Generic;
using Cortex.BeaconNode.Configuration;
using Cortex.Containers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cortex.BeaconNode.Data
{
    public class StoreProvider : IStoreProvider
    {
        private readonly BeaconChainUtility _beaconChainUtility;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IOptionsMonitor<TimeParameters> _timeParameterOptions;

        public StoreProvider(ILoggerFactory loggerFactory, IOptionsMonitor<TimeParameters> timeParameterOptions, BeaconChainUtility beaconChainUtility)
        {
            _loggerFactory = loggerFactory;
            _timeParameterOptions = timeParameterOptions;
            _beaconChainUtility = beaconChainUtility;
        }

        public IStore CreateStore(ulong time,
            ulong genesisTime,
            Checkpoint justifiedCheckpoint,
            Checkpoint finalizedCheckpoint,
            Checkpoint bestJustifiedCheckpoint,
            IDictionary<Hash32, BeaconBlock> blocks,
            IDictionary<Hash32, BeaconState> blockStates,
            IDictionary<Checkpoint, BeaconState> checkpointStates)
        {
            var store = new Store(time, genesisTime, justifiedCheckpoint, finalizedCheckpoint, bestJustifiedCheckpoint, blocks, blockStates, checkpointStates,
                _loggerFactory.CreateLogger<Store>(),
                _timeParameterOptions,
                _beaconChainUtility);
            return store;
        }
    }
}
