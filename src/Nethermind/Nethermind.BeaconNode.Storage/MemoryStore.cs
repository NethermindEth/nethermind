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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethermind.Core2.Configuration;
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
        public Checkpoint FinalizedCheckpoint { get; private set; }
        public ulong GenesisTime { get; }
        public Checkpoint JustifiedCheckpoint { get; private set; }
        public ulong Time { get; private set; }

        public Task SetBestJustifiedCheckpointAsync(Checkpoint checkpoint)
        {
            BestJustifiedCheckpoint = checkpoint;
            return Task.CompletedTask;
        }

        public Task SetBlockAsync(Hash32 signingRoot, BeaconBlock block)
        {
            _blocks[signingRoot] = block;
            return Task.CompletedTask;
        }

        public Task SetBlockStateAsync(Hash32 signingRoot, BeaconState state)
        {
            _blockStates[signingRoot] = state;
            return Task.CompletedTask;
        }

        public Task SetCheckpointStateAsync(Checkpoint checkpoint, BeaconState state)
        {
            _checkpointStates[checkpoint] = state;
            return Task.CompletedTask;
        }

        public Task SetFinalizedCheckpointAsync(Checkpoint checkpoint)
        {
            FinalizedCheckpoint = checkpoint;
            return Task.CompletedTask;
        }

        public Task SetJustifiedCheckpointAsync(Checkpoint checkpoint)
        {
            JustifiedCheckpoint = checkpoint;
            return Task.CompletedTask;
        }

        public Task SetLatestMessageAsync(ValidatorIndex validatorIndex, LatestMessage latestMessage)
        {
            _latestMessages[validatorIndex] = latestMessage;
            return Task.CompletedTask;
        }

        public Task SetTimeAsync(ulong time)
        {
            Time = time;
            return Task.CompletedTask;
        }

        public ValueTask<BeaconBlock> GetBlockAsync(Hash32 signingRoot)
        {
            if (!_blocks.TryGetValue(signingRoot, out BeaconBlock? block))
            {
                throw new ArgumentOutOfRangeException(nameof(signingRoot), signingRoot, "Block not found in store.");
            }
            return new ValueTask<BeaconBlock>(block!);
        }

        public ValueTask<BeaconState> GetBlockStateAsync(Hash32 signingRoot)
        {
            if (!_blockStates.TryGetValue(signingRoot, out BeaconState? state))
            {
                throw new ArgumentOutOfRangeException(nameof(signingRoot), signingRoot, "State not found in store.");
            }
            return new ValueTask<BeaconState>(state!);
        }

        public ValueTask<BeaconState?> GetCheckpointStateAsync(Checkpoint checkpoint, bool throwIfMissing)
        {
            if (!_checkpointStates.TryGetValue(checkpoint, out BeaconState? state))
            {
                if (throwIfMissing)
                {
                    throw new ArgumentOutOfRangeException(nameof(checkpoint), checkpoint,
                        "Checkpoint state not found in store."); 
                }
            }
            return new ValueTask<BeaconState?>(state);
        }

        public async IAsyncEnumerable<Hash32> GetChildKeysAfterSlotAsync(Hash32 parent, Slot slot)
        {
            await Task.CompletedTask;
            IEnumerable<Hash32> childKeys = _blocks
                .Where(kvp =>
                    kvp.Value.ParentRoot.Equals(parent)
                    && kvp.Value.Slot > slot)
                .Select(kvp => kvp.Key);
            foreach (Hash32 childKey in childKeys)
            {
                yield return childKey;
            }
        }

        public ValueTask<LatestMessage?> GetLatestMessageAsync(ValidatorIndex validatorIndex, bool throwIfMissing)
        {
            if (!_latestMessages.TryGetValue(validatorIndex, out LatestMessage? latestMessage))
            {
                if (throwIfMissing)
                {
                    throw new ArgumentOutOfRangeException(nameof(validatorIndex), validatorIndex,
                        "Latest message not found in store.");
                }
            }
            return new ValueTask<LatestMessage?>(latestMessage);
        }
    }
}
