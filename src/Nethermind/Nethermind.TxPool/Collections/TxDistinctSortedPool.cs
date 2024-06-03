// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool.Comparison;

namespace Nethermind.TxPool.Collections
{
    public class TxDistinctSortedPool : DistinctValueSortedPool<ValueHash256, Transaction, AddressAsKey>
    {
        public delegate void UpdateGroupDelegate(in AccountStruct account, SortedSet<Transaction> transactions, ref Transaction? lastElement, UpdateTransactionDelegate updateTx);
        public delegate void UpdateTransactionDelegate(SortedSet<Transaction> bucket, Transaction tx, in UInt256? changedGasBottleneck, Transaction? lastElement);

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

        public virtual bool TryInsert(ValueHash256 key, Transaction tx, ref TxFilteringState state, UpdateGroupDelegate updateElements, out Transaction? removed)
        {
            using var lockRelease = Lock.Acquire();

            TryGetBucketsWorstValueNotLocked(tx.SenderAddress!, out Transaction? worstTx);
            if (worstTx is not null && tx.GasBottleneck > worstTx.GasBottleneck)
            {
                tx.GasBottleneck = worstTx.GasBottleneck;
            }

            bool inserted = TryInsertNotLocked(key, tx, out removed);
            if (inserted && tx.Hash != removed?.Hash)
            {
                UpdateGroupNotLocked(tx.SenderAddress!, state.SenderAccount, updateElements);
            }

            return inserted;
        }

        protected override void UpdateGroupNotLocked(AddressAsKey groupKey, SortedSet<Transaction> bucket, Func<AddressAsKey, SortedSet<Transaction>, IEnumerable<(Transaction Tx, Action<Transaction>? Change)>> changingElements)
        {
            _transactionsToRemove.Clear();
            Transaction? lastElement = bucket.Max;

            foreach ((Transaction tx, Action<Transaction>? change) in changingElements(groupKey, bucket))
            {
                if (change is null)
                {
                    _transactionsToRemove.Add(tx);
                }
                else
                {
                    WorstValuesRemove(tx);
                    change(tx);
                    WorstValuesAdd(tx);
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
            foreach ((AddressAsKey address, SortedSet<Transaction> bucket) in _buckets)
            {
                Debug.Assert(bucket.Count > 0);

                accounts.TryGetAccount(address, out AccountStruct account);
                UpdateGroupNonLocked(account, bucket, updateElements);
            }
        }

        private void UpdateGroupNonLocked(AccountStruct groupValue, SortedSet<Transaction> bucket, UpdateGroupDelegate updateElements)
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

        private void UpdateTransaction(SortedSet<Transaction> bucket, Transaction tx, in UInt256? changedGasBottleneck, Transaction? lastElement)
        {
            if (changedGasBottleneck is null)
            {
                _transactionsToRemove.Add(tx);
            }
            else
            {
                WorstValuesRemove(tx);
                tx.GasBottleneck = changedGasBottleneck;
                WorstValuesAdd(tx);
            }
        }

        protected void UpdateGroupNotLocked(Address groupKey, in AccountStruct groupValue, UpdateGroupDelegate updateElements)
        {
            ArgumentNullException.ThrowIfNull(groupKey);
            if (_buckets.TryGetValue(groupKey, out SortedSet<Transaction>? bucket))
            {
                Debug.Assert(bucket.Count > 0);

                UpdateGroupNonLocked(groupValue, bucket, updateElements);
            }
        }

        protected override string ShortPoolName => "TxPool";
    }
}
