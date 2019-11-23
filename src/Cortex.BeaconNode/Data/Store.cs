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
            Blocks = blocks;
            BlockStates = blockStates;
            CheckpointStates = checkpointStates;
            _logger = logger;
            _timeParameterOptions = timeParameterOptions;
            _beaconChainUtility = beaconChainUtility;
        }

        public Checkpoint BestJustifiedCheckpoint { get; }

        //public Checkpoint FinalizedCheckpoint { get; }
        public IDictionary<Hash32, BeaconBlock> Blocks { get; }

        public IDictionary<Hash32, BeaconState> BlockStates { get; }
        public IDictionary<Checkpoint, BeaconState> CheckpointStates { get; }
        public Checkpoint FinalizedCheckpoint { get; }
        public ulong GenesisTime { get; }
        public Checkpoint JustifiedCheckpoint { get; }
        public IDictionary<ValidatorIndex, LatestMessage> LatestMessages { get; } = new Dictionary<ValidatorIndex, LatestMessage>();
        public ulong Time { get; private set; }

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

        public void SetTime(ulong time) => Time = time;

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
