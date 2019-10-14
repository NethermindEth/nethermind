using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Cortex.BeaconNode.Configuration;
using Cortex.Containers;
using Microsoft.Extensions.Options;

namespace Cortex.BeaconNode
{
    // Data Class
    public class Store
    {
        private readonly IOptionsMonitor<TimeParameters> _timeParameterOptions;

        public ulong Time { get; }
        public Checkpoint JustifiedCheckpoint { get; } = new Checkpoint();
        //public Checkpoint FinalizedCheckpoint { get; }
        public IDictionary<Hash32, BeaconBlock> Blocks { get; } = new Dictionary<Hash32, BeaconBlock>();
        public IDictionary<Hash32, BeaconState> BlockStates { get; } = new Dictionary<Hash32, BeaconState>();
        public IDictionary<Checkpoint, BeaconState> CheckpointStates { get; } = new Dictionary<Checkpoint, BeaconState>();
        public IDictionary<ValidatorIndex, LatestMessage> LatestMessages { get; } = new Dictionary<ValidatorIndex, LatestMessage>();

        public Store (IOptionsMonitor<TimeParameters> timeParameterOptions)
        {
            _timeParameterOptions = timeParameterOptions;
        }

        public async Task<Hash32> GetHeadAsync()
        {
            return await Task.Run(() =>
            {
                var head = JustifiedCheckpoint.Root;
                var justifiedSlot = ComputeStartSlotOfEpoch(JustifiedCheckpoint.Epoch);
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

        /// <summary>
        /// Return the start slot of ``epoch``.
        /// </summary>
        private Slot ComputeStartSlotOfEpoch(Epoch epoch)
        {
            return new Slot((ulong)epoch * _timeParameterOptions.CurrentValue.SlotsPerEpoch);
        }

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
