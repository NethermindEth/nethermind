// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Nethermind.Abi;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Consensus.AuRa
{
    public class AuRaContractGasLimitOverride : IGasLimitCalculator
    {
        private static UInt256? _minimalContractGasLimit;

        private static UInt256 MinimalContractGasLimit
        {
            get
            {
                _minimalContractGasLimit ??= 2_000_000L;
                return _minimalContractGasLimit.Value;
            }
        }
        private readonly IList<IBlockGasLimitContract> _contracts;
        private readonly Cache _cache;
        private readonly bool _minimum2MlnGasPerBlockWhenUsingBlockGasLimitContract;
        private readonly IGasLimitCalculator _innerCalculator;
        private readonly ILogger _logger;

        public AuRaContractGasLimitOverride(
            IList<IBlockGasLimitContract> contracts,
            Cache cache,
            bool minimum2MlnGasPerBlockWhenUsingBlockGasLimitContract,
            IGasLimitCalculator innerCalculator,
            ILogManager logManager)
        {
            _contracts = contracts ?? throw new ArgumentNullException(nameof(contracts));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _minimum2MlnGasPerBlockWhenUsingBlockGasLimitContract = minimum2MlnGasPerBlockWhenUsingBlockGasLimitContract;
            _innerCalculator = innerCalculator ?? throw new ArgumentNullException(nameof(innerCalculator));
            _logger = logManager?.GetClassLogger<AuRaContractGasLimitOverride>() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public long GetGasLimit(BlockHeader parentHeader) => GetGasLimitFromContract(parentHeader) ?? _innerCalculator.GetGasLimit(parentHeader);

        private long? GetGasLimitFromContract(BlockHeader parentHeader)
        {
            if (_cache.GasLimitCache.TryGet(parentHeader.Hash, out long? gasLimit))
            {
                return gasLimit;
            }

            if (_contracts.TryGetForBlock(parentHeader.Number + 1, out IBlockGasLimitContract contract))
            {
                UInt256? contractLimit = GetContractGasLimit(parentHeader, contract);
                gasLimit = contractLimit.HasValue ? (long)contractLimit.Value : (long?)null;
                _cache.GasLimitCache.Set(parentHeader.Hash, gasLimit);
                if (gasLimit.HasValue)
                {
                    if (gasLimit.Value != parentHeader.GasLimit)
                    {
                        if (_logger.IsInfo)
                            _logger.Info($"Block gas limit was changed from {parentHeader.GasLimit} to {gasLimit.Value}.");
                    }
                }
                else
                {
                    if (_logger.IsTrace)
                        _logger.Trace("Contract call returned nothing. Not changing the block gas limit.");
                }
            }

            return gasLimit;
        }

        private UInt256? GetContractGasLimit(BlockHeader parent, IBlockGasLimitContract contract)
        {
            try
            {
                var contractGasLimit = contract.BlockGasLimit(parent);
                return contractGasLimit.HasValue && _minimum2MlnGasPerBlockWhenUsingBlockGasLimitContract && contractGasLimit < MinimalContractGasLimit
                    ? MinimalContractGasLimit
                    : contractGasLimit;
            }
            catch (AbiException e)
            {
                if (_logger.IsError) _logger.Error($"Contract call failed. Not changing the block gas limit on block {parent.ToString(BlockHeader.Format.FullHashAndNumber)} {new StackTrace()}.", e);
                return null;
            }
        }

        public class Cache
        {
            private const int MaxCacheSize = 10;

            internal LruCache<KeccakKey, long?> GasLimitCache { get; } = new(MaxCacheSize, "BlockGasLimit");
        }

        public bool IsGasLimitValid(BlockHeader parentHeader, in long gasLimit, out long? expectedGasLimit)
        {
            expectedGasLimit = GetGasLimitFromContract(parentHeader);
            return expectedGasLimit is null || expectedGasLimit == gasLimit;
        }
    }
}
