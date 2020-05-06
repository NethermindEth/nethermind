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
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;

namespace Nethermind.Consensus.AuRa
{
    public class AuRaContractGasLimitOverride : IGasLimitOverride
    {
        private readonly IList<BlockGasLimitContract> _contracts;
        private readonly IGasLimitOverride.Cache _cache;
        private readonly ILogger _logger;
        
        public AuRaContractGasLimitOverride(IList<BlockGasLimitContract> contracts, IGasLimitOverride.Cache cache, ILogManager logManager)
        {
            _contracts = contracts ?? throw new ArgumentNullException(nameof(contracts));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _logger = logManager?.GetClassLogger<AuRaContractGasLimitOverride>() ?? throw new ArgumentNullException(nameof(logManager));
        }
        
        public long? GetGasLimit(BlockHeader header, long blockNumber)
        {
            if (_cache.GasLimitCache.TryGet(header.Hash, out var gasLimit))
            {
                return gasLimit;
            }
            else if (_contracts.TryGetForBlock(blockNumber, out var contract))
            {
                var contractLimit = GetContractGasLimit(header, contract);
                gasLimit = contractLimit.HasValue ? (long) contractLimit.Value : (long?) null;
                _cache.GasLimitCache.Set(header.Hash, gasLimit);
                if (gasLimit.HasValue)
                {
                    if (gasLimit.Value != header.GasLimit)
                    {
                        if (_logger.IsInfo) _logger.Info($"Block gas limit was changed from {header.GasLimit} to {gasLimit.Value}.");
                    }
                }
                else
                {
                    if (_logger.IsDebug) _logger.Debug("Contract call returned nothing. Not changing the block gas limit.");
                }
                
                return gasLimit;
            }
            else
            {
                return null;
            }
        }

        private UInt256? GetContractGasLimit(BlockHeader parent, BlockGasLimitContract contract)
        {
            try
            {
                return contract.BlockGasLimit(parent);
            }
            catch (AuRaException e)
            {
                if (_logger.IsError) _logger.Error("Contract call failed. Not changing the block gas limit.", e);
                return null;
            }
        }
    }
}