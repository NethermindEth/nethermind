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
using Nethermind.BeaconNode.Containers;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Storage
{
    public interface IStore
    {
//        IReadOnlyDictionary<Hash32, BeaconBlock> Blocks { get; }
//        IReadOnlyDictionary<Hash32, BeaconState> BlockStates { get; }
//        IReadOnlyDictionary<Checkpoint, BeaconState> CheckpointStates { get; }
//        IReadOnlyDictionary<ValidatorIndex, LatestMessage> LatestMessages { get; }

        Checkpoint BestJustifiedCheckpoint { get; }
        Checkpoint FinalizedCheckpoint { get; }
        ulong GenesisTime { get; }
        Checkpoint JustifiedCheckpoint { get; }
        ulong Time { get; }

        Task SetBestJustifiedCheckpointAsync(Checkpoint checkpoint);

        Task SetBlockAsync(Hash32 signingRoot, BeaconBlock block);

        Task SetBlockStateAsync(Hash32 signingRoot, BeaconState state);

        Task SetCheckpointStateAsync(Checkpoint checkpoint, BeaconState state);

        Task SetFinalizedCheckpointAsync(Checkpoint checkpoint);

        Task SetJustifiedCheckpointAsync(Checkpoint checkpoint);

        Task SetLatestMessageAsync(ValidatorIndex validatorIndex, LatestMessage latestMessage);

        Task SetTimeAsync(ulong time);

        ValueTask<BeaconBlock> GetBlockAsync(Hash32 signingRoot);

        ValueTask<BeaconState> GetBlockStateAsync(Hash32 signingRoot);

        ValueTask<BeaconState?> GetCheckpointStateAsync(Checkpoint checkpoint, bool throwIfMissing);

        IAsyncEnumerable<Hash32> GetChildKeysAfterSlotAsync(Hash32 parent, Slot slot);

        ValueTask<LatestMessage?> GetLatestMessageAsync(ValidatorIndex validatorIndex, bool throwIfMissing);
    }
}
