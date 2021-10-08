//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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