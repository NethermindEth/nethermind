//  Copyright (c) 2021 Demerzel Solutions Limited
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
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public abstract class VersionedContract<T> : IActivatedAtBlock where T : IVersionedContract
    {
        private readonly IDictionary<UInt256, T> _versions;

        private readonly IVersionedContract _versionSelectorContract;
        private readonly ICache<Keccak, UInt256> _versionsCache;
        private readonly ILogger _logger;

        protected VersionedContract(IDictionary<UInt256, T> versions, ICache<Keccak, UInt256> cache, long activation, ILogManager logManager)
        {
            _versions = versions ?? throw new ArgumentNullException(nameof(versions));
            _versionSelectorContract = versions.Values.Last();
            Activation = activation;
            _versionsCache = cache ?? throw new ArgumentNullException(nameof(cache));
            _logger = logManager.GetClassLogger();
        }

        public T? ResolveVersion(BlockHeader blockHeader)
        {
            this.BlockActivationCheck(blockHeader);

            if (!_versionsCache.TryGet(blockHeader.Hash, out var versionNumber))
            {
                try
                {
                    versionNumber = _versionSelectorContract.ContractVersion(blockHeader);
                    _versionsCache.Set(blockHeader.Hash, versionNumber);
                }
                catch (AbiException ex)
                {
                    if (_logger.IsWarn) _logger.Warn($"The contract version set to 1: {ex}");
                    versionNumber = UInt256.One;
                    _versionsCache.Set(blockHeader.Hash, versionNumber);
                }
            }

            return ResolveVersion(versionNumber);
        }

        private T? ResolveVersion(UInt256 versionNumber) => _versions.TryGetValue(versionNumber, out var contract) ? contract : default;

        public long Activation { get; }
    }
}
