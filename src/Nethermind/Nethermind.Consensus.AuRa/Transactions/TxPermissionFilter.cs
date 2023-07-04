// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
using Nethermind.TxPool;

namespace Nethermind.Consensus.AuRa.Transactions
{
    public class PermissionBasedTxFilter : ITxFilter
    {
        private readonly VersionedContract<ITransactionPermissionContract> _contract;
        private readonly Cache _cache;
        private readonly ILogger _logger;

        public PermissionBasedTxFilter(
            VersionedContract<ITransactionPermissionContract> contract,
            Cache cache,
            ILogManager logManager)
        {
            _contract = contract ?? throw new ArgumentNullException(nameof(contract));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _logger = logManager?.GetClassLogger<PermissionBasedTxFilter>() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public AcceptTxResult IsAllowed(Transaction tx, BlockHeader parentHeader)
        {
            if (parentHeader.Number + 1 < _contract.Activation)
            {
                return AcceptTxResult.Accepted;
            }

            var txPermissions = GetPermissions(tx, parentHeader);
            var txType = GetTxType(tx, txPermissions.ContractExists);
            if (_logger.IsTrace) _logger.Trace($"Given transaction: {tx.Hash} sender: {tx.SenderAddress} to: {tx.To} value: {tx.Value}, gas_price: {tx.GasPrice}. " +
                                               $"Permissions required: {txType}, got: {txPermissions}.");
            return (txPermissions.Permissions & txType) == txType ? AcceptTxResult.Accepted : AcceptTxResultAuRa.PermissionDenied.WithMessage($"permission denied for tx type: {txType}, actual permissions: {txPermissions.Permissions}");
        }

        private (ITransactionPermissionContract.TxPermissions Permissions, bool ContractExists) GetPermissions(Transaction tx, BlockHeader parentHeader)
        {
            var key = (parentHeader.Hash, tx.SenderAddress);
            return _cache.Permissions.TryGet(key, out var txCachedPermissions)
                ? txCachedPermissions
                : GetPermissionsFromContract(tx, parentHeader, key);
        }

        private (ITransactionPermissionContract.TxPermissions Permissions, bool ContractExists) GetPermissionsFromContract(
            Transaction tx,
            BlockHeader parentHeader,
            in (Keccak Hash, Address SenderAddress) key)
        {
            ITransactionPermissionContract.TxPermissions txPermissions = ITransactionPermissionContract.TxPermissions.None;
            bool shouldCache = true;
            bool contractExists = false;

            ITransactionPermissionContract versionedContract = GetVersionedContract(parentHeader);
            if (versionedContract is null)
            {
                if (_logger.IsError) _logger.Error("Unknown version of tx permissions contract is used.");
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"Version of tx permission contract: {versionedContract.Version}.");

                try
                {
                    (txPermissions, shouldCache, contractExists) = versionedContract.AllowedTxTypes(parentHeader, tx);
                }
                catch (AbiException e)
                {
                    if (_logger.IsError) _logger.Error($"Error calling tx permissions contract on {parentHeader.ToString(BlockHeader.Format.FullHashAndNumber)} for tx {tx.ToShortString()}.", e);
                }
            }

            var result = (txPermissions, contractExists);

            if (shouldCache)
            {
                _cache.Permissions.Set(key, result);
            }

            return result;
        }

        private ITransactionPermissionContract? GetVersionedContract(BlockHeader blockHeader)
            => _contract.ResolveVersion(blockHeader);

        private ITransactionPermissionContract.TxPermissions GetTxType(Transaction tx, bool contractExists) =>
            tx.IsContractCreation
                ? ITransactionPermissionContract.TxPermissions.Create
                : contractExists
                    ? ITransactionPermissionContract.TxPermissions.Call
                    : ITransactionPermissionContract.TxPermissions.Basic;

        public class Cache
        {
            public const int MaxCacheSize = 4096;

            internal LruCache<(Keccak ParentHash, Address Sender), (ITransactionPermissionContract.TxPermissions Permissions, bool ContractExists)> Permissions { get; } =
                new LruCache<(Keccak ParentHash, Address Sender), (ITransactionPermissionContract.TxPermissions Permissions, bool ContractExists)>(MaxCacheSize, "TxPermissions");
        }
    }
}
