// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool.Comparison;

namespace Nethermind.TxPool.Collections
{
    public class TxDistinctSortedPool : DistinctValueSortedPool<ValueHash256, Transaction, AddressAsKey>
    {
        public delegate void UpdateGroupDelegate(in AccountStruct account, EnhancedSortedSet<Transaction> transactions, ref Transaction? lastElement, UpdateTransactionDelegate updateTx);
        public delegate void UpdateTransactionDelegate(EnhancedSortedSet<Transaction> bucket, Transaction tx, in UInt256? changedGasBottleneck, Transaction? lastElement);

        private readonly UpdateTransactionDelegate _updateTx;
        private readonly List<Transaction> _transactionsToRemove = [];
        private readonly Dictionary<AddressAsKey, int> _keyedNonceCounts = [];
        protected int _poolCapacity;

        public TxDistinctSortedPool(int capacity, IComparer<Transaction> comparer, ILogManager logManager)
            : base(capacity, comparer, CompetingTransactionEqualityComparer.Instance, logManager)
        {
            _poolCapacity = capacity;
            _updateTx = UpdateTransaction;
        }

        protected override IComparer<Transaction> GetUniqueComparer(IComparer<Transaction> comparer) => comparer.GetPoolUniqueTxComparer();
        protected override IComparer<Transaction> GetGroupComparer(IComparer<Transaction> comparer) => comparer.GetPoolUniqueTxComparerByNonce();
        protected override IComparer<Transaction> GetReplacementComparer(IComparer<Transaction> comparer) => comparer.GetReplacementComparer();

        protected override AddressAsKey MapToGroup(Transaction value) => value.MapTxToGroup() ?? throw new ArgumentException("MapTxToGroup() returned null!");
        protected override ValueHash256 GetKey(Transaction value) => value.Hash!;

        protected override bool InsertCore(ValueHash256 key, Transaction value, AddressAsKey groupKey)
        {
            if (!base.InsertCore(key, value, groupKey))
            {
                return false;
            }

            if (KeyedNonceManager.UsesKeyedNonce(value))
            {
                _keyedNonceCounts[groupKey] = _keyedNonceCounts.GetValueOrDefault(groupKey) + 1;
            }

            return true;
        }

        protected override bool Remove(ValueHash256 key, out Transaction? value)
        {
            if (!base.Remove(key, out value))
            {
                return false;
            }

            if (value is not null && KeyedNonceManager.UsesKeyedNonce(value))
            {
                AddressAsKey groupKey = MapToGroup(value);
                if (_keyedNonceCounts.TryGetValue(groupKey, out int keyedCount))
                {
                    if (keyedCount > 1)
                    {
                        _keyedNonceCounts[groupKey] = keyedCount - 1;
                    }
                    else
                    {
                        _keyedNonceCounts.Remove(groupKey);
                    }
                }
            }

            return true;
        }

        /// <summary>Number of a sender's pending transactions that consume its account nonce.</summary>
        /// <remarks>An EIP-8250 keyed transaction shares the sender's bucket but spends no account nonce, so an
        /// account-nonce caller sizing the sender's nonce window by the raw bucket count would widen it by entries
        /// that never fill it.</remarks>
        internal int GetAccountDomainBucketCount(AddressAsKey group)
        {
            using McsLock.Disposable lockRelease = Lock.Acquire();

            return _buckets.TryGetValue(group, out EnhancedSortedSet<Transaction>? bucket)
                ? bucket.Count - _keyedNonceCounts.GetValueOrDefault(group)
                : 0;
        }

        /// <summary>The nonce following a sender's pending transactions when the bucket count alone settles it,
        /// without walking the bucket.</summary>
        /// <remarks>Declines whenever the bucket holds an EIP-8250 keyed entry: its <c>Nonce</c> is a sequence in
        /// its own domain, so it neither advances the count nor bounds the bucket's highest account nonce.</remarks>
        internal bool TryGetContiguousPendingNonce(AddressAsKey group, ulong accountNonce, out ulong pendingNonce)
        {
            using McsLock.Disposable lockRelease = Lock.Acquire();

            if (!_keyedNonceCounts.ContainsKey(group)
                && _buckets.TryGetValue(group, out EnhancedSortedSet<Transaction>? bucket)
                && accountNonce + (ulong)bucket.Count - 1 == bucket.Max!.Nonce)
            {
                pendingNonce = bucket.Max.Nonce + 1;
                return true;
            }

            pendingNonce = accountNonce;
            return false;
        }

        protected override void UpdateGroup(AddressAsKey groupKey, EnhancedSortedSet<Transaction> bucket, Func<AddressAsKey, IReadOnlySortedSet<Transaction>, IEnumerable<(Transaction Tx, Action<Transaction>? Change)>> changingElements)
        {
            _transactionsToRemove.Clear();
            Transaction? lastElement = bucket.Max;

            foreach ((Transaction tx, Action<Transaction>? change) in changingElements(groupKey, bucket))
            {
                if (change is null)
                {
                    _transactionsToRemove.Add(tx);
                }
                else if (Equals(lastElement, tx))
                {
                    bool reAdd = _worstSortedValues.Remove(tx);
                    change(tx);
                    if (reAdd)
                    {
                        _worstSortedValues.Add(tx, tx.Hash!);
                    }

                    UpdateWorstValue();
                }
                else
                {
                    change(tx);
                }
            }

            for (int i = 0; i < _transactionsToRemove.Count; i++)
            {
                TryRemove(_transactionsToRemove[i].Hash!);
            }
        }

        public void UpdatePool(IAccountStateProvider accounts, UpdateGroupDelegate updateElements)
        {
            using McsLock.Disposable lockRelease = Lock.Acquire();

            UpdatePoolNonLocked(accounts, updateElements);
        }

        /// <summary>
        /// Updates every account bucket during a fork revalidation pass.
        /// </summary>
        /// <remarks>
        /// A fork revalidation may evict many transactions at once. Persistent pools override this method to
        /// coalesce their storage deletions into a single write batch.
        /// </remarks>
        internal virtual void UpdatePoolForRevalidation(IAccountStateProvider accounts, UpdateGroupDelegate updateElements) =>
            UpdatePool(accounts, updateElements);

        private protected void UpdatePoolNonLocked(IAccountStateProvider accounts, UpdateGroupDelegate updateElements)
        {
            EnsureCapacity();
            foreach ((AddressAsKey address, EnhancedSortedSet<Transaction> bucket) in _buckets)
            {
                Debug.Assert(bucket.Count > 0);

                accounts.TryGetAccount(address, out AccountStruct account);
                UpdateGroupNonLocked(account, bucket, updateElements);
            }
        }

        private void UpdateGroupNonLocked(AccountStruct groupValue, EnhancedSortedSet<Transaction> bucket, UpdateGroupDelegate updateElements)
        {
            _transactionsToRemove.Clear();
            Transaction? lastElement = bucket.Max;

            updateElements(groupValue, bucket, ref lastElement, _updateTx);

            ReadOnlySpan<Transaction> txs = CollectionsMarshal.AsSpan(_transactionsToRemove);
            for (int i = 0; i < txs.Length; i++)
            {
                TryRemoveNonLocked(txs[i].Hash!, evicted: false, out _, out _);
            }
        }

        private void UpdateTransaction(EnhancedSortedSet<Transaction> bucket, Transaction tx, in UInt256? changedGasBottleneck, Transaction? lastElement)
        {
            if (changedGasBottleneck is null)
            {
                _transactionsToRemove.Add(tx);
            }
            else if (Equals(lastElement, tx))
            {
                bool reAdd = _worstSortedValues.Remove(tx);
                tx.GasBottleneck = changedGasBottleneck;
                if (reAdd)
                {
                    _worstSortedValues.Add(tx, tx.Hash!);
                }

                UpdateWorstValue();
            }
            else
            {
                tx.GasBottleneck = changedGasBottleneck;
            }
        }

        public void UpdateGroup(Address groupKey, AccountStruct groupValue, UpdateGroupDelegate updateElements)
        {
            using McsLock.Disposable lockRelease = Lock.Acquire();

            ArgumentNullException.ThrowIfNull(groupKey);
            if (_buckets.TryGetValue(groupKey, out EnhancedSortedSet<Transaction>? bucket))
            {
                Debug.Assert(bucket.Count > 0);

                UpdateGroupNonLocked(groupValue, bucket, updateElements);
            }
        }

        protected override string ShortPoolName => "TxPool";
    }
}
