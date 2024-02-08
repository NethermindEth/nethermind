// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool.Comparison;

namespace Nethermind.TxPool.Collections
{
    public class TxDistinctSortedPool : DistinctValueSortedPool<ValueHash256, Transaction, Address>
    {
        private readonly List<Transaction> _transactionsToRemove = new();
        protected int _poolCapacity;
        private readonly ILogger _logger;


        public TxDistinctSortedPool(int capacity, IComparer<Transaction> comparer, ILogManager logManager)
            : base(capacity, comparer, CompetingTransactionEqualityComparer.Instance, logManager)
        {
            _poolCapacity = capacity;
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        protected override IComparer<Transaction> GetUniqueComparer(IComparer<Transaction> comparer) => comparer.GetPoolUniqueTxComparer();
        protected override IComparer<Transaction> GetGroupComparer(IComparer<Transaction> comparer) => comparer.GetPoolUniqueTxComparerByNonce();
        protected override IComparer<Transaction> GetReplacementComparer(IComparer<Transaction> comparer) => comparer.GetReplacementComparer();

        protected override Address MapToGroup(Transaction value) => value.MapTxToGroup() ?? throw new ArgumentException("MapTxToGroup() returned null!");
        protected override ValueHash256 GetKey(Transaction value) => value.Hash!;

        protected override void UpdateGroup(Address groupKey, EnhancedSortedSet<Transaction> bucket, Func<Address, IReadOnlySortedSet<Transaction>, IEnumerable<(Transaction Tx, Action<Transaction>? Change)>> changingElements)
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

        public void UpdatePool(IAccountStateProvider accounts, Func<Address, AccountStruct, EnhancedSortedSet<Transaction>, IEnumerable<(Transaction Tx, UInt256? changedGasBottleneck)>> changingElements)
        {
            using var lockRelease = Lock.Acquire();

            VerifyCapacity();
            foreach ((Address address, EnhancedSortedSet<Transaction> bucket) in _buckets)
            {
                Debug.Assert(bucket.Count > 0);

                AccountStruct account = accounts.GetAccount(address);
                UpdateGroupNonLocked(address, account, bucket, changingElements);
            }
        }

        private void UpdateGroupNonLocked(Address groupKey, AccountStruct groupValue, EnhancedSortedSet<Transaction> bucket, Func<Address, AccountStruct, EnhancedSortedSet<Transaction>, IEnumerable<(Transaction Tx, UInt256? changedGasBottleneck)>> changingElements)
        {
            _transactionsToRemove.Clear();
            Transaction? lastElement = bucket.Max;

            foreach ((Transaction tx, UInt256? changedGasBottleneck) in changingElements(groupKey, groupValue, bucket))
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

            ReadOnlySpan<Transaction> txs = CollectionsMarshal.AsSpan(_transactionsToRemove);
            for (int i = 0; i < txs.Length; i++)
            {
                TryRemoveNonLocked(txs[i].Hash!, evicted: false, out _, out _);
            }
        }

        public void UpdateGroup(Address groupKey, AccountStruct groupValue, Func<Address, AccountStruct, EnhancedSortedSet<Transaction>, IEnumerable<(Transaction Tx, UInt256? changedGasBottleneck)>> changingElements)
        {
            using var lockRelease = Lock.Acquire();

            ArgumentNullException.ThrowIfNull(groupKey);
            if (_buckets.TryGetValue(groupKey, out EnhancedSortedSet<Transaction>? bucket))
            {
                Debug.Assert(bucket.Count > 0);

                UpdateGroupNonLocked(groupKey, groupValue, bucket, changingElements);
            }
        }

        public virtual void VerifyCapacity()
        {
            if (_logger.IsWarn && Count > _poolCapacity)
                _logger.Warn($"TxPool exceeds the config size {Count}/{_poolCapacity}");
        }
    }
}
