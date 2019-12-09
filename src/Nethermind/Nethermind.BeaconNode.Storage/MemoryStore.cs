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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethermind.BeaconNode.Configuration;
using Nethermind.BeaconNode.Containers;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Storage
{
    // Data Class
    public class MemoryStore : IStore
    {
        private readonly BeaconChainUtility _beaconChainUtility;
        private readonly Dictionary<Hash32, BeaconBlock> _blocks;
        private readonly Dictionary<Hash32, BeaconState> _blockStates;
        private readonly Dictionary<Checkpoint, BeaconState> _checkpointStates;
        private readonly Dictionary<ValidatorIndex, LatestMessage> _latestMessages;
        private readonly ILogger _logger;
        private readonly IOptionsMonitor<TimeParameters> _timeParameterOptions;

        public MemoryStore(ulong time,
            ulong genesisTime,
            Checkpoint justifiedCheckpoint,
            Checkpoint finalizedCheckpoint,
            Checkpoint bestJustifiedCheckpoint,
            IDictionary<Hash32, BeaconBlock> blocks,
            IDictionary<Hash32, BeaconState> blockStates,
            IDictionary<Checkpoint, BeaconState> checkpointStates,
            IDictionary<ValidatorIndex, LatestMessage> latestMessages,
            ILogger<MemoryStore> logger,
            IOptionsMonitor<TimeParameters> timeParameterOptions,
            BeaconChainUtility beaconChainUtility)
        {
            Time = time;
            GenesisTime = genesisTime;
            JustifiedCheckpoint = justifiedCheckpoint;
            FinalizedCheckpoint = finalizedCheckpoint;
            BestJustifiedCheckpoint = bestJustifiedCheckpoint;
            _blocks = new Dictionary<Hash32, BeaconBlock>(blocks);
            _blockStates = new Dictionary<Hash32, BeaconState>(blockStates);
            _checkpointStates = new Dictionary<Checkpoint, BeaconState>(checkpointStates);
            _latestMessages = new Dictionary<ValidatorIndex, LatestMessage>(latestMessages);
            _logger = logger;
            _timeParameterOptions = timeParameterOptions;
            _beaconChainUtility = beaconChainUtility;
        }

        public Checkpoint BestJustifiedCheckpoint { get; private set; }
        public IReadOnlyDictionary<Hash32, BeaconBlock> Blocks { get { return _blocks; } }
        public IReadOnlyDictionary<Hash32, BeaconState> BlockStates { get { return _blockStates; } }
        public IReadOnlyDictionary<Checkpoint, BeaconState> CheckpointStates { get { return _checkpointStates; } }
        public Checkpoint FinalizedCheckpoint { get; private set; }
        public ulong GenesisTime { get; }
        public Checkpoint JustifiedCheckpoint { get; private set; }
        public IReadOnlyDictionary<ValidatorIndex, LatestMessage> LatestMessages { get { return _latestMessages; } }
        public ulong Time { get; private set; }

        public void SetBestJustifiedCheckpoint(Checkpoint checkpoint) => BestJustifiedCheckpoint = checkpoint;

        public void SetBlock(Hash32 signingRoot, BeaconBlock block) => _blocks[signingRoot] = block;

        public void SetBlockState(Hash32 signingRoot, BeaconState state) => _blockStates[signingRoot] = state;

        public void SetCheckpointState(Checkpoint checkpoint, BeaconState state) => _checkpointStates[checkpoint] = state;

        public void SetFinalizedCheckpoint(Checkpoint checkpoint) => FinalizedCheckpoint = checkpoint;

        public void SetJustifiedCheckpoint(Checkpoint checkpoint) => JustifiedCheckpoint = checkpoint;

        public void SetLatestMessage(ValidatorIndex validatorIndex, LatestMessage latestMessage) => _latestMessages[validatorIndex] = latestMessage;

        public void SetTime(ulong time) => Time = time;

        public bool TryGetBlock(Hash32 signingRoot, out BeaconBlock? block) => _blocks.TryGetValue(signingRoot, out block);

        public bool TryGetBlockState(Hash32 signingRoot, out BeaconState? state) => _blockStates.TryGetValue(signingRoot, out state);

        public bool TryGetCheckpointState(Checkpoint checkpoint, out BeaconState? state) => _checkpointStates.TryGetValue(checkpoint, out state);

        public bool TryGetLatestMessage(ValidatorIndex validatorIndex, out LatestMessage? latestMessage) => _latestMessages.TryGetValue(validatorIndex, out latestMessage);
    }
}
