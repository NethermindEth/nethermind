using System.Collections.Generic;
using System.Threading.Tasks;
using Cortex.Containers;

namespace Cortex.BeaconNode.Data
{
    public interface IStore
    {
        Checkpoint BestJustifiedCheckpoint { get; }
        IDictionary<Hash32, BeaconBlock> Blocks { get; }
        IDictionary<Hash32, BeaconState> BlockStates { get; }
        IDictionary<Checkpoint, BeaconState> CheckpointStates { get; }
        public ulong GenesisTime { get; }
        Checkpoint JustifiedCheckpoint { get; }
        IDictionary<ValidatorIndex, LatestMessage> LatestMessages { get; }
        ulong Time { get; }

        Task<Hash32> GetHeadAsync();
        void SetTime(ulong time);
        void SetJustifiedCheckpoint(Checkpoint bestJustifiedCheckpoint);
    }
}
