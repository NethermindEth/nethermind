// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Store;
using Nethermind.Core2.Types;

namespace Nethermind.Core2
{
    public interface IStore
    {
        Checkpoint BestJustifiedCheckpoint { get; }
        Checkpoint FinalizedCheckpoint { get; }
        ulong GenesisTime { get; }
        bool IsInitialized { get; }
        Checkpoint JustifiedCheckpoint { get; }
        ulong Time { get; }

        Task<Root> GetAncestorAsync(Root root, Slot slot);
        ValueTask<BeaconState> GetBlockStateAsync(Root blockRoot);
        ValueTask<BeaconState?> GetCheckpointStateAsync(Checkpoint checkpoint, bool throwIfMissing);
        IAsyncEnumerable<Root> GetChildKeysAsync(Root parent);
        Task<Root> GetHeadAsync();
        ValueTask<LatestMessage?> GetLatestMessageAsync(ValidatorIndex validatorIndex, bool throwIfMissing);
        ValueTask<SignedBeaconBlock> GetSignedBlockAsync(Root blockRoot);

        Task InitializeForkChoiceStoreAsync(ulong time, ulong genesisTime, Checkpoint justifiedCheckpoint,
            Checkpoint finalizedCheckpoint, Checkpoint bestJustifiedCheckpoint,
            IDictionary<Root, SignedBeaconBlock> signedBlocks,
            IDictionary<Root, BeaconState> states, IDictionary<Checkpoint, BeaconState> checkpointStates);

        Task SetBestJustifiedCheckpointAsync(Checkpoint checkpoint);
        Task SetBlockStateAsync(Root blockHashTreeRoot, BeaconState beaconState);
        Task SetCheckpointStateAsync(Checkpoint checkpoint, BeaconState state);
        Task SetFinalizedCheckpointAsync(Checkpoint checkpoint);
        Task SetJustifiedCheckpointAsync(Checkpoint checkpoint);
        Task SetLatestMessageAsync(ValidatorIndex validatorIndex, LatestMessage latestMessage);
        Task SetSignedBlockAsync(Root blockHashTreeRoot, SignedBeaconBlock signedBeaconBlock);
        Task SetTimeAsync(ulong time);
    }
}
