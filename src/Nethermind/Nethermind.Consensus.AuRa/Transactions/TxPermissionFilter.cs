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
using System.Diagnostics;
using Nethermind.Abi;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.AuRa.Transactions
{
    public class PermissionBasedTxFilter : ITxFilter
    {
        private readonly VersionedContract<ITransactionPermissionContract> _contract;
        private readonly Cache _cache;
        private readonly IReadOnlyStateProvider _stateProvider;
        private readonly ILogger _logger;

        public PermissionBasedTxFilter(
            VersionedContract<ITransactionPermissionContract> contract,
            Cache cache,
            IReadOnlyStateProvider stateProvider,
            ILogManager logManager)
        {
            _contract = contract ?? throw new ArgumentNullException(nameof(contract));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
            _logger = logManager?.GetClassLogger<PermissionBasedTxFilter>() ?? throw new ArgumentNullException(nameof(logManager));
        }
        
        public (bool Allowed, string Reason) IsAllowed(Transaction tx, BlockHeader parentHeader)
        {
            if (parentHeader.Number + 1 < _contract.Activation)
            {
                return (true, string.Empty);
            }

            var txType = GetTxType(tx);
            var txPermissions = GetPermissions(tx, parentHeader);
            if (_logger.IsTrace) _logger.Trace($"Given transaction: {tx.Hash} sender: {tx.SenderAddress} to: {tx.To} value: {tx.Value}, gas_price: {tx.GasPrice}. " +
                                               $"Permissions required: {txType}, got: {txPermissions}.");
            return (txPermissions & txType) == txType ? (true, string.Empty) : (false, "permission denied");
        }

        private ITransactionPermissionContract.TxPermissions GetPermissions(Transaction tx, BlockHeader parentHeader)
        {
            var key = (parentHeader.Hash, tx.SenderAddress);
            var txCachedPermissions = _cache.Permissions.Get(key);
            return txCachedPermissions ?? GetPermissionsFromContract(tx, parentHeader, key);
        }

        private ITransactionPermissionContract.TxPermissions GetPermissionsFromContract(
            Transaction tx,
            BlockHeader parentHeader,
            in (Keccak Hash, Address SenderAddress) key)
        {
            ITransactionPermissionContract.TxPermissions txPermissions = ITransactionPermissionContract.TxPermissions.None;
            bool shouldCache = true;
            
            var versionedContract = GetVersionedContract(parentHeader);
            if (versionedContract == null)
            {
                if (_logger.IsError) _logger.Error("Unknown version of tx permissions contract is used.");
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"Version of tx permission contract: {versionedContract.Version}.");
                
                try
                {
                    (txPermissions, shouldCache) = versionedContract.AllowedTxTypes(parentHeader, tx);
                }
                catch (AbiException e)
                {
                    if (_logger.IsError) _logger.Error($"Error calling tx permissions contract on {parentHeader.ToString(BlockHeader.Format.FullHashAndNumber)} {new StackTrace()}.", e);
                }
            }

            if (shouldCache)
            {
                _cache.Permissions.Set(key, txPermissions);
            }

            return txPermissions;
        }

        private ITransactionPermissionContract GetVersionedContract(BlockHeader blockHeader)
            => _contract.ResolveVersion(blockHeader);

        private ITransactionPermissionContract.TxPermissions GetTxType(Transaction tx) =>
            tx.IsContractCreation
                ? ITransactionPermissionContract.TxPermissions.Create
                : (_stateProvider.GetCode(tx.To) ?? Array.Empty<byte>()).Length != 0
                    ? ITransactionPermissionContract.TxPermissions.Call
                    : ITransactionPermissionContract.TxPermissions.Basic;
        
        public class Cache
        {
            public const int MaxCacheSize = 4096;
            
            internal ICache<(Keccak ParentHash, Address Sender), ITransactionPermissionContract.TxPermissions?> Permissions { get; } =
                new LruCache<(Keccak ParentHash, Address Sender), ITransactionPermissionContract.TxPermissions?>(MaxCacheSize, "TxPermissions");
        }
    }
}
