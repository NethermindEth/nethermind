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
// 

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public abstract class VersionedContract<T> : IActivatedAtBlock where T : IVersionedContract
    {
        private readonly IDictionary<UInt256, T> _versions;
        
        private const int MaxCacheSize = 4096;
        
        private readonly IVersionedContract _versionSelectorContract;
        
        internal ICache<Keccak, UInt256> VersionsCache { get; } = new LruCacheWithRecycling<Keccak, UInt256>(MaxCacheSize, nameof(VersionedContract<T>));
        
        protected VersionedContract(IDictionary<UInt256, T> versions, long activation)
        {
            _versions = versions ?? throw new ArgumentNullException(nameof(versions));
            _versionSelectorContract = versions.Values.Last();
            Activation = activation;
        }

        public T ResolveVersion(BlockHeader blockHeader)
        {
            this.BlockActivationCheck(blockHeader);
            
            if (!VersionsCache.TryGet(blockHeader.Hash, out var versionNumber))
            {
                versionNumber = _versionSelectorContract.ContractVersion(blockHeader);
                VersionsCache.Set(blockHeader.Hash, versionNumber);
            }
            
            return ResolveVersion(versionNumber);
        }

        private T ResolveVersion(UInt256 versionNumber) => _versions.TryGetValue(versionNumber, out var contract) ? contract : default;
        
        public long Activation { get; }
    }
}
