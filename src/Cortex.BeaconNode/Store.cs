using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Cortex.Containers;
using Microsoft.Extensions.Options;
using Epoch = System.UInt64;
using Gwei = System.UInt64;
using Hash = System.Byte; // Byte32
using Slot = System.UInt64;

namespace Cortex.BeaconNode
{
    public class Store
    {
        private readonly IOptionsMonitor<TimeParameters> _timeParameterOptions;

        public ulong Time { get; }
        public Checkpoint JustifiedCheckpoint { get; } = new Checkpoint();
        //public Checkpoint FinalizedCheckpoint { get; }
        public IDictionary<Hash[], BeaconBlock> Blocks { get; }
        public IDictionary<Hash[], BeaconState> BlockStates { get; }
        //public IDictionary<Checkpoint, BeaconState> CheckpointStates { get; }
        //public IDictionary<ValidatorIndex, LatestMessage> LatestMessages { get; }

        public Store (IOptionsMonitor<TimeParameters> timeParameterOptions)
        {
            _timeParameterOptions = timeParameterOptions;
        }

        public async Task<Hash[]> GetHeadAsync()
        {
            return await Task.Run(() =>
            {
                var head = JustifiedCheckpoint.Root;
                var justifiedSlot = ComputeStartSlotOfEpoch(JustifiedCheckpoint.Epoch);
                while (true)
                {
                    var children = Blocks
                        .Where(kvp => ByteArrayCompare(kvp.Value.ParentRoot, head) && kvp.Value.Slot > justifiedSlot)
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
            return epoch * _timeParameterOptions.CurrentValue.SlotsPerEpoch;
        }

        private Gwei GetLatestAttestingBalance(Hash[] root)
        {
            return 0;
        }

        private static bool ByteArrayCompare(ReadOnlySpan<byte> a1, ReadOnlySpan<byte> a2)
        {
            return a1.SequenceEqual(a2);
        }
    }
}
