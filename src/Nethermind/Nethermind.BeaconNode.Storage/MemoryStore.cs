using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cortex.BeaconNode.Configuration;
using Cortex.Containers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cortex.BeaconNode.Storage
{
    // Data Class
    public class MemoryStore : IStore
    {
        private readonly BeaconChainUtility _beaconChainUtility;
        private readonly Dictionary<Hash32, BeaconBlock> _blocks;
        private readonly Dictionary<Hash32, BeaconState> _blockStates;
        private readonly Dictionary<Checkpoint, BeaconState> _checkpointStates;
        private readonly Dictionary<ValidatorIndex, LatestMessage> _latestMessages;
        private readonly ILogger _logger;
        private readonly IOptionsMonitor<TimeParameters> _timeParameterOptions;

        public MemoryStore(ulong time,
            ulong genesisTime,
            Checkpoint justifiedCheckpoint,
            Checkpoint finalizedCheckpoint,
            Checkpoint bestJustifiedCheckpoint,
            IDictionary<Hash32, BeaconBlock> blocks,
            IDictionary<Hash32, BeaconState> blockStates,
            IDictionary<Checkpoint, BeaconState> checkpointStates,
            IDictionary<ValidatorIndex, LatestMessage> latestMessages,
            ILogger<MemoryStore> logger,
            IOptionsMonitor<TimeParameters> timeParameterOptions,
            BeaconChainUtility beaconChainUtility)
        {
            Time = time;
            GenesisTime = genesisTime;
            JustifiedCheckpoint = justifiedCheckpoint;
            FinalizedCheckpoint = finalizedCheckpoint;
            BestJustifiedCheckpoint = bestJustifiedCheckpoint;
            _blocks = new Dictionary<Hash32, BeaconBlock>(blocks);
            _blockStates = new Dictionary<Hash32, BeaconState>(blockStates);
            _checkpointStates = new Dictionary<Checkpoint, BeaconState>(checkpointStates);
            _latestMessages = new Dictionary<ValidatorIndex, LatestMessage>(latestMessages);
            _logger = logger;
            _timeParameterOptions = timeParameterOptions;
            _beaconChainUtility = beaconChainUtility;
        }

        public Checkpoint BestJustifiedCheckpoint { get; private set; }
        public IReadOnlyDictionary<Hash32, BeaconBlock> Blocks { get { return _blocks; } }
        public IReadOnlyDictionary<Hash32, BeaconState> BlockStates { get { return _blockStates; } }
        public IReadOnlyDictionary<Checkpoint, BeaconState> CheckpointStates { get { return _checkpointStates; } }
        public Checkpoint FinalizedCheckpoint { get; private set; }
        public ulong GenesisTime { get; }
        public Checkpoint JustifiedCheckpoint { get; private set; }
        public IReadOnlyDictionary<ValidatorIndex, LatestMessage> LatestMessages { get { return _latestMessages; } }
        public ulong Time { get; private set; }

        public void SetBestJustifiedCheckpoint(Checkpoint checkpoint) => BestJustifiedCheckpoint = checkpoint;

        public void SetBlock(Hash32 signingRoot, BeaconBlock block) => _blocks[signingRoot] = block;

        public void SetBlockState(Hash32 signingRoot, BeaconState state) => _blockStates[signingRoot] = state;

        public void SetCheckpointState(Checkpoint checkpoint, BeaconState state) => _checkpointStates[checkpoint] = state;

        public void SetFinalizedCheckpoint(Checkpoint checkpoint) => FinalizedCheckpoint = checkpoint;

        public void SetJustifiedCheckpoint(Checkpoint checkpoint) => JustifiedCheckpoint = checkpoint;

        public void SetLatestMessage(ValidatorIndex validatorIndex, LatestMessage latestMessage) => _latestMessages[validatorIndex] = latestMessage;

        public void SetTime(ulong time) => Time = time;

        public bool TryGetBlock(Hash32 signingRoot, out BeaconBlock block) => _blocks.TryGetValue(signingRoot, out block);

        public bool TryGetBlockState(Hash32 signingRoot, out BeaconState state) => _blockStates.TryGetValue(signingRoot, out state);

        public bool TryGetCheckpointState(Checkpoint checkpoint, out BeaconState state) => _checkpointStates.TryGetValue(checkpoint, out state);

        public bool TryGetLatestMessage(ValidatorIndex validatorIndex, out LatestMessage latestMessage) => _latestMessages.TryGetValue(validatorIndex, out latestMessage);
    }
}
