using System.Collections.Generic;
using Nethermind.BeaconNode.Containers;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Storage
{
    public interface IStore
    {
        Checkpoint BestJustifiedCheckpoint { get; }
        IReadOnlyDictionary<Hash32, BeaconBlock> Blocks { get; }
        IReadOnlyDictionary<Hash32, BeaconState> BlockStates { get; }
        IReadOnlyDictionary<Checkpoint, BeaconState> CheckpointStates { get; }
        Checkpoint FinalizedCheckpoint { get; }
        ulong GenesisTime { get; }
        Checkpoint JustifiedCheckpoint { get; }
        IReadOnlyDictionary<ValidatorIndex, LatestMessage> LatestMessages { get; }
        ulong Time { get; }

        void SetBestJustifiedCheckpoint(Checkpoint checkpoint);

        void SetBlock(Hash32 signingRoot, BeaconBlock block);

        void SetBlockState(Hash32 signingRoot, BeaconState state);

        void SetCheckpointState(Checkpoint checkpoint, BeaconState state);

        void SetFinalizedCheckpoint(Checkpoint checkpoint);

        void SetJustifiedCheckpoint(Checkpoint checkpoint);

        void SetLatestMessage(ValidatorIndex validatorIndex, LatestMessage latestMessage);

        void SetTime(ulong time);

        bool TryGetBlock(Hash32 signingRoot, out BeaconBlock? block);

        bool TryGetBlockState(Hash32 signingRoot, out BeaconState? state);

        bool TryGetCheckpointState(Checkpoint checkpoint, out BeaconState? state);

        bool TryGetLatestMessage(ValidatorIndex validatorIndex, out LatestMessage? latestMessage);
    }
}
