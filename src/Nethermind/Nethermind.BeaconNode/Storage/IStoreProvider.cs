using System.Collections.Generic;
using Nethermind.BeaconNode.Containers;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Storage
{
    public interface IStoreProvider
    {
        IStore CreateStore(ulong time, ulong genesisTime, Checkpoint justifiedCheckpoint, Checkpoint finalizedCheckpoint, Checkpoint bestJustifiedCheckpoint, IDictionary<Hash32, BeaconBlock> blocks, IDictionary<Hash32, BeaconState> blockStates, IDictionary<Checkpoint, BeaconState> checkpointStates, IDictionary<ValidatorIndex, LatestMessage> latestMessages);
        bool TryGetStore(out IStore? store);
    }
}
