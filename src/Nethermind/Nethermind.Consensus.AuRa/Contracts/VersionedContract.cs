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
    public abstract class VersionedContract<T> : IActivatedAtBlock
        where T : Contract, IVersionedContract
    {
        private readonly IDictionary<UInt256, T> _versionedContracts;
        
        public VersionedContract(IDictionary<UInt256, T> versions, long activation)
        {
            _versionedContracts = versions ?? throw new ArgumentNullException(nameof(versions));
            Current = versions.First(v => v.Value.SupportsContractVersion).Value;
            Activation = activation;
        }

        public void UpdateCurrent(BlockHeader blockHeader)
        {
            this.BlockActivationCheck(blockHeader);
            
            if (!VersionsCache.TryGet(blockHeader.Hash, out var version))
            {
                version = Current.ContractVersion(blockHeader);
                VersionsCache.Set(blockHeader.Hash, version);
            }
            
            Current = GetVersionedContract(version);
        }

        private const int MaxCacheSize = 4096;
        
        internal ICache<Keccak, UInt256> VersionsCache { get; } = new LruCacheWithRecycling<Keccak, UInt256>(MaxCacheSize, "TxPermissionsVersionedContracts");
        
        public T GetVersionedContract(UInt256 version) =>
            _versionedContracts.TryGetValue(version, out var contract) ? contract : null;

        public T Current { get; private set; }
        public long Activation { get; }
    }
}