// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Abi;
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
        private readonly LruCache<ValueKeccak, UInt256> _versionsCache;
        private readonly ILogger _logger;

        protected VersionedContract(IDictionary<UInt256, T> versions, LruCache<ValueKeccak, UInt256> cache, long activation, ILogManager logManager)
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
                    if (_logger.IsDebug) _logger.Debug($"The contract version set to 1: {ex}");
                    versionNumber = UInt256.One;
                    _versionsCache.Set(blockHeader.Hash, versionNumber);
                }
            }

            return ResolveVersion(versionNumber);
        }

        private T? ResolveVersion(in UInt256 versionNumber) => _versions.TryGetValue(versionNumber, out var contract) ? contract : default;

        public long Activation { get; }
    }
}
