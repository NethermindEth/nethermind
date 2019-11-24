using System.Collections.Generic;
using System.Threading.Tasks;
using Cortex.Containers;

namespace Cortex.BeaconNode.Data
{
    public interface IStore
    {
        Checkpoint BestJustifiedCheckpoint { get; }
        IReadOnlyDictionary<Hash32, BeaconBlock> Blocks { get; }
        IReadOnlyDictionary<Hash32, BeaconState> BlockStates { get; }
        IReadOnlyDictionary<Checkpoint, BeaconState> CheckpointStates { get; }
        Checkpoint FinalizedCheckpoint { get; }
        public ulong GenesisTime { get; }
        Checkpoint JustifiedCheckpoint { get; }
        IDictionary<ValidatorIndex, LatestMessage> LatestMessages { get; }
        ulong Time { get; }

        void AddBlock(Hash32 signingRoot, BeaconBlock block);

        void AddBlockState(Hash32 signingRoot, BeaconState state);

        Task<Hash32> GetHeadAsync();

        void SetBestJustifiedCheckpoint(Checkpoint checkpoint);

        void SetFinalizedCheckpoint(Checkpoint checkpoint);

        void SetJustifiedCheckpoint(Checkpoint checkpoint);

        void SetTime(ulong time);

        bool TryGetBlock(Hash32 signingRoot, out BeaconBlock block);

        bool TryGetBlockState(Hash32 signingRoot, out BeaconState state);
    }
}
