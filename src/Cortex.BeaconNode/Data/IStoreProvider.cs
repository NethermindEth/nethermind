using System.Collections.Generic;
using Cortex.Containers;

namespace Cortex.BeaconNode.Data
{
    public interface IStoreProvider
    {
        IStore CreateStore(ulong time, ulong genesisTime, Checkpoint justifiedCheckpoint, Checkpoint finalizedCheckpoint, Checkpoint bestJustifiedCheckpoint, IDictionary<Hash32, BeaconBlock> blocks, IDictionary<Hash32, BeaconState> blockStates, IDictionary<Checkpoint, BeaconState> checkpointStates);
    }
}