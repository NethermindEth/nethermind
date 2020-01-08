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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethermind.Core2;
using Nethermind.Core2.Configuration;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Storage
{
    public class MemoryStoreProvider : IStoreProvider
    {
        private readonly ILoggerFactory _loggerFactory;
        private readonly IOptionsMonitor<TimeParameters> _timeParameterOptions;

        private IStore? _store;

        public MemoryStoreProvider(ILoggerFactory loggerFactory, IOptionsMonitor<TimeParameters> timeParameterOptions)
        {
            _loggerFactory = loggerFactory;
            _timeParameterOptions = timeParameterOptions;
        }

        public IStore CreateStore(ulong time,
            ulong genesisTime,
            Checkpoint justifiedCheckpoint,
            Checkpoint finalizedCheckpoint,
            Checkpoint bestJustifiedCheckpoint,
            IDictionary<Hash32, BeaconBlock> blocks,
            IDictionary<Hash32, BeaconState> blockStates,
            IDictionary<Checkpoint, BeaconState> checkpointStates,
            IDictionary<ValidatorIndex, LatestMessage> latestMessages)
        {
            _store = new MemoryStore(time, genesisTime, justifiedCheckpoint, finalizedCheckpoint, bestJustifiedCheckpoint, blocks, blockStates, checkpointStates, latestMessages,
                _loggerFactory.CreateLogger<MemoryStore>(),
                _timeParameterOptions);
            return _store;
        }

        public bool TryGetStore(out IStore? store)
        {
            // NOTE: For MemoryStoreProvider, this needs ot have been initialised via CreateStore (MemoryStore has no persistence).
            store = _store;
            
            if (store is null)
            {
                return false;
            }
            return true;
        }
    }
}
