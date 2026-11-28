// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
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
    public class TxDistinctSortedPool : DistinctValueSortedPool<ValueHash256, MempoolTransaction, AddressAsKey>
    {
        public delegate void UpdateGroupDelegate(in AccountStruct account, EnhancedSortedSet<MempoolTransaction> transactions, ref MempoolTransaction? lastElement, UpdateTransactionDelegate updateTx);
        public delegate void UpdateTransactionDelegate(EnhancedSortedSet<MempoolTransaction> bucket, MempoolTransaction tx, in UInt256? changedGasBottleneck, MempoolTransaction? lastElement);

        private const int RemovalGenerationStripes = 256;

        private readonly UpdateTransactionDelegate _updateTx;
        private readonly List<MempoolTransaction> _transactionsToRemove = [];
        private readonly Dictionary<AddressAsKey, int> _keyedNonceCounts = [];
        private readonly long[] _removalGenerations = new long[RemovalGenerationStripes];
        protected int _poolCapacity;

        public TxDistinctSortedPool(int capacity, IComparer<MempoolTransaction> comparer, ILogManager logManager)
            : base(capacity, comparer, CompetingTransactionEqualityComparer.Instance, logManager)
        {
            _poolCapacity = capacity;
            _updateTx = UpdateTransaction;
        }

        protected override IComparer<MempoolTransaction> GetUniqueComparer(IComparer<MempoolTransaction> comparer) => comparer.GetPoolUniqueTxComparer();
        protected override IComparer<MempoolTransaction> GetGroupComparer(IComparer<MempoolTransaction> comparer) => comparer.GetPoolUniqueTxComparerByNonce();
        protected override IComparer<MempoolTransaction> GetReplacementComparer(IComparer<MempoolTransaction> comparer) => comparer.GetReplacementComparer();

        protected override AddressAsKey MapToGroup(MempoolTransaction value) => value.Transaction.MapTxToGroup() ?? throw new ArgumentException("MapTxToGroup() returned null!");
        protected override ValueHash256 GetKey(MempoolTransaction value) => value.Transaction.Hash!;

        protected override bool InsertCore(ValueHash256 key, MempoolTransaction value, AddressAsKey groupKey)
        {
            if (!base.InsertCore(key, value, groupKey))
            {
                return false;
            }

            if (KeyedNonceManager.UsesKeyedNonce(value.Transaction))
            {
                _keyedNonceCounts[groupKey] = _keyedNonceCounts.GetValueOrDefault(groupKey) + 1;
            }

            return true;
        }

        protected override bool Remove(ValueHash256 key, out MempoolTransaction? value)
        {
            if (!base.Remove(key, out value))
            {
                return false;
            }

            if (value is not null)
            {
                CountRemoval(value.Transaction);
            }

            return true;
        }

        /// <inheritdoc/>
        /// <remarks>The keyed count tracks bucket membership rather than the pool's removal attempt: a removal the
        /// group comparer cannot locate leaves the entry in the bucket, and capacity eviction drops one from the
        /// bucket without raising <see cref="SortedPool{TKey, TValue, TGroupKey}.Removed"/>.</remarks>
        protected override bool RemoveFromBucket(MempoolTransaction value, out EnhancedSortedSet<MempoolTransaction>? bucketSet)
        {
            if (!base.RemoveFromBucket(value, out bucketSet))
            {
                return false;
            }

            CountRemoval(value.Transaction);
            if (KeyedNonceManager.UsesKeyedNonce(value.Transaction))
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

        /// <summary>Counts removals of <paramref name="sender"/>'s transactions, and of every sender sharing its stripe.</summary>
        /// <remarks>Counted where key and bucket membership shrink, the only two places either does, so an unchanged
        /// count proves the sender lost nothing. Both run under the pool lock, which serializes the writers and which
        /// every membership query takes, so a reader that sees a new count sees the removal behind it.</remarks>
        internal long GetRemovalGeneration(Address sender) =>
            Volatile.Read(ref _removalGenerations[RemovalGenerationStripe(sender)]);

        private void CountRemoval(Transaction value)
        {
            ref long generation = ref _removalGenerations[RemovalGenerationStripe(value.SenderAddress!)];
            Volatile.Write(ref generation, generation + 1);
        }

        private static int RemovalGenerationStripe(Address sender) => sender.GetHashCode() & (RemovalGenerationStripes - 1);

        /// <summary>Number of a sender's pending transactions that consume its account nonce.</summary>
        /// <remarks>An EIP-8250 keyed transaction shares the sender's bucket but spends no account nonce, so an
        /// account-nonce caller sizing the sender's nonce window by the raw bucket count would widen it by entries
        /// that never fill it.</remarks>
        internal int GetAccountDomainBucketCount(AddressAsKey group)
        {
            using McsLock.Disposable lockRelease = Lock.Acquire();

            return _buckets.TryGetValue(group, out EnhancedSortedSet<MempoolTransaction>? bucket)
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
                && _buckets.TryGetValue(group, out EnhancedSortedSet<MempoolTransaction>? bucket)
                && accountNonce + (ulong)bucket.Count - 1 == bucket.Max!.Nonce)
            {
                pendingNonce = bucket.Max.Nonce + 1;
                return true;
            }

            pendingNonce = accountNonce;
            return false;
        }

        protected override void UpdateGroup(AddressAsKey groupKey, EnhancedSortedSet<MempoolTransaction> bucket, Func<AddressAsKey, IReadOnlySortedSet<MempoolTransaction>, IEnumerable<(MempoolTransaction Tx, Action<MempoolTransaction>? Change)>> changingElements)
        {
            _transactionsToRemove.Clear();
            MempoolTransaction? lastElement = bucket.Max;

            foreach ((MempoolTransaction tx, Action<MempoolTransaction>? change) in changingElements(groupKey, bucket))
            {
                if (change is null)
                {
                    _transactionsToRemove.Add(tx);
                }
                else if (Equals(lastElement, tx))
                {
                    change(tx);
                    _worstSortedValues.Update(tx);

                    UpdateWorstValue();
                }
                else
                {
                    change(tx);
                }
            }

            for (int i = 0; i < _transactionsToRemove.Count; i++)
            {
                TryRemoveNonLocked(_transactionsToRemove[i].Hash!, evicted: false, out _, out _);
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
            foreach ((AddressAsKey address, EnhancedSortedSet<MempoolTransaction> bucket) in _buckets)
            {
                Debug.Assert(bucket.Count > 0);

                accounts.TryGetAccount(address, out AccountStruct account);
                UpdateGroupNonLocked(account, bucket, updateElements);
            }
        }

        private void UpdateGroupNonLocked(AccountStruct groupValue, EnhancedSortedSet<MempoolTransaction> bucket, UpdateGroupDelegate updateElements)
        {
            _transactionsToRemove.Clear();
            MempoolTransaction? lastElement = bucket.Max;

            updateElements(groupValue, bucket, ref lastElement, _updateTx);

            ReadOnlySpan<MempoolTransaction> txs = CollectionsMarshal.AsSpan(_transactionsToRemove);
            for (int i = 0; i < txs.Length; i++)
            {
                TryRemoveNonLocked(txs[i].Hash!, evicted: false, out _, out _);
            }
        }

        private void UpdateTransaction(EnhancedSortedSet<MempoolTransaction> bucket, MempoolTransaction tx, in UInt256? changedGasBottleneck, MempoolTransaction? lastElement)
        {
            if (changedGasBottleneck is null)
            {
                _transactionsToRemove.Add(tx);
            }
            else if (Equals(lastElement, tx))
            {
                tx.GasBottleneck = changedGasBottleneck;
                _worstSortedValues.Update(tx);

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
            if (_buckets.TryGetValue(groupKey, out EnhancedSortedSet<MempoolTransaction>? bucket))
            {
                Debug.Assert(bucket.Count > 0);

                UpdateGroupNonLocked(groupValue, bucket, updateElements);
            }
        }

        protected override string ShortPoolName => "TxPool";
    }
}
