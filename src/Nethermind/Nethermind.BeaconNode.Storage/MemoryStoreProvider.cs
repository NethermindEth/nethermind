using System.Collections.Generic;
using Cortex.BeaconNode.Configuration;
using Cortex.Containers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cortex.BeaconNode.Storage
{
    public class MemoryStoreProvider : IStoreProvider
    {
        private readonly BeaconChainUtility _beaconChainUtility;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IOptionsMonitor<TimeParameters> _timeParameterOptions;

        private IStore? _store;

        public MemoryStoreProvider(ILoggerFactory loggerFactory, IOptionsMonitor<TimeParameters> timeParameterOptions, BeaconChainUtility beaconChainUtility)
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
            IDictionary<Checkpoint, BeaconState> checkpointStates,
            IDictionary<ValidatorIndex, LatestMessage> latestMessages)
        {
            _store = new MemoryStore(time, genesisTime, justifiedCheckpoint, finalizedCheckpoint, bestJustifiedCheckpoint, blocks, blockStates, checkpointStates, latestMessages,
                _loggerFactory.CreateLogger<MemoryStore>(),
                _timeParameterOptions,
                _beaconChainUtility);
            return _store;
        }

        public IStore? GetStore()
        {
            // NOTE: For MemoryStoreProvider, this needs ot have been initialised via CreateStore.
            return _store;
        }
    }
}
