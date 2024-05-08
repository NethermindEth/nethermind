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
        private readonly List<Transaction> _transactionsToRemove = new();
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
