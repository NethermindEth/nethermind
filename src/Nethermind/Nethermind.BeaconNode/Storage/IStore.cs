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
