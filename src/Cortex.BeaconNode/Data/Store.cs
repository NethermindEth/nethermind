using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cortex.BeaconNode.Configuration;
using Cortex.Containers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cortex.BeaconNode.Data
{
    // Data Class
    public class Store : IStore
    {
        private readonly BeaconChainUtility _beaconChainUtility;
        private readonly Dictionary<Hash32, BeaconBlock> _blocks;
        private readonly Dictionary<Hash32, BeaconState> _blockStates;
        private readonly Dictionary<Checkpoint, BeaconState> _checkpointStates;
        private readonly ILogger _logger;
        private readonly IOptionsMonitor<TimeParameters> _timeParameterOptions;

        public Store(ulong time,
            ulong genesisTime,
            Checkpoint justifiedCheckpoint,
            Checkpoint finalizedCheckpoint,
            Checkpoint bestJustifiedCheckpoint,
            IDictionary<Hash32, BeaconBlock> blocks,
            IDictionary<Hash32, BeaconState> blockStates,
            IDictionary<Checkpoint, BeaconState> checkpointStates,
            ILogger<Store> logger,
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
        public IDictionary<ValidatorIndex, LatestMessage> LatestMessages { get; } = new Dictionary<ValidatorIndex, LatestMessage>();
        public ulong Time { get; private set; }

        public void AddBlock(Hash32 signingRoot, BeaconBlock block) => _blocks[signingRoot] = block;

        public void AddBlockState(Hash32 signingRoot, BeaconState state) => _blockStates[signingRoot] = state;

        public async Task<Hash32> GetHeadAsync()
        {
            return await Task.Run(() =>
            {
                var head = JustifiedCheckpoint.Root;
                var justifiedSlot = _beaconChainUtility.ComputeStartSlotOfEpoch(JustifiedCheckpoint.Epoch);
                while (true)
                {
                    var children = Blocks
                        .Where(kvp =>
                            kvp.Value.ParentRoot.Equals(head)
                            && kvp.Value.Slot > justifiedSlot)
                        .Select(kvp => kvp.Key);
                    if (children.Count() == 0)
                    {
                        return head;
                    }
                    head = children
                        .OrderBy(x => GetLatestAttestingBalance(x))
                        .ThenBy(x => x)
                        .First();
                }
            });
        }

        public void SetBestJustifiedCheckpoint(Checkpoint checkpoint) => BestJustifiedCheckpoint = checkpoint;

        public void SetFinalizedCheckpoint(Checkpoint checkpoint) => FinalizedCheckpoint = checkpoint;

        public void SetJustifiedCheckpoint(Checkpoint checkpoint) => JustifiedCheckpoint = checkpoint;

        public void SetTime(ulong time) => Time = time;

        public bool TryGetBlock(Hash32 signingRoot, out BeaconBlock block) => _blocks.TryGetValue(signingRoot, out block);

        public bool TryGetBlockState(Hash32 signingRoot, out BeaconState state) => _blockStates.TryGetValue(signingRoot, out state);

        private Gwei GetLatestAttestingBalance(Hash32 root)
        {
            var state = CheckpointStates[JustifiedCheckpoint];
            //var currentEpoch = GetCurrentEpoch(state);
            //var activeIndexes = GetActiveValidatorIndices(state, currentEpoch);
            var rootSlot = Blocks[root].Slot;
            var balance = new Gwei(0);
            /*
            foreach (var index in activeIndexes)
            {
                if (LatestMessages.Contains(index))
                {
                    var latestMessage = LatestMessages[index];
                    var ancestor = GetAncestor(latestMessage.Root, rootSlot);
                    if (ancestor == root)
                    {
                        balance += state.Validators[index].EffectiveBalance;
                    }
                }
            }
            */
            return balance;
        }
    }
}
