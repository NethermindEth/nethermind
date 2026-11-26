// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NonBlocking;
using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;
using Nethermind.Trie;

namespace Nethermind.TxPool;

public class NonceManager(
    IChainHeadInfoProvider chainHeadInfoProvider,
    IStateHeaderProvider stateHeaderProvider,
    IStateReader stateReader,
    int minSweepThreshold = NonceManager.DefaultMinSweepThreshold) : INonceManager
{
    internal const int DefaultMinSweepThreshold = 1024;

    private readonly ConcurrentDictionary<AddressAsKey, AddressNonceManager> _addressNonceManagers = new();
    private readonly IAccountStateProvider _accounts = chainHeadInfoProvider.ReadOnlyStateProvider;
    private readonly IChainHeadInfoProvider _chainHeadInfoProvider = chainHeadInfoProvider;
    private readonly IStateHeaderProvider _stateHeaderProvider = stateHeaderProvider;
    private readonly IStateReader _stateReader = stateReader;
    private readonly int _minSweepThreshold = minSweepThreshold;
    private int _sweepThreshold = minSweepThreshold;
    private int _sweeping;

    internal int TrackedAddressCount => _addressNonceManagers.Count;

    public NonceLocker ReserveNonce(Address address, IPendingTxsBySender pendingTxs, out ulong reservedNonce)
    {
        while (true)
        {
            AddressNonceManager addressNonceManager = GetAddressNonceManager(address);
            NonceLocker locker = addressNonceManager.ReserveNonce(address, _accounts, pendingTxs, out reservedNonce);
            if (!addressNonceManager.IsRetired) return locker;
            locker.Dispose();
        }
    }

    public NonceLocker TxWithNonceReceived(Address address, ulong nonce)
    {
        while (true)
        {
            AddressNonceManager addressNonceManager = GetAddressNonceManager(address);
            NonceLocker locker = addressNonceManager.TxWithNonceReceived(nonce);
            if (!addressNonceManager.IsRetired) return locker;
            locker.Dispose();
        }
    }

    private AddressNonceManager GetAddressNonceManager(Address address)
    {
        if (_addressNonceManagers.Count >= Volatile.Read(ref _sweepThreshold))
        {
            SweepConfirmed();
        }

        return _addressNonceManagers.GetOrAdd(address, static _ => new AddressNonceManager());
    }

    /// <summary>
    /// Drops the addresses whose every accepted nonce is below their nonce at the reorg-safe block, and the addresses
    /// that never had a nonce accepted. A reorg that keeps the reorg-safe block cannot lower the account nonce below
    /// what it was there, so a fresh entry never hands out a nonce the dropped one did. Busy entries are skipped
    /// rather than waited for.
    /// </summary>
    /// <remarks>
    /// Runs on the submitting thread. Worst case, one request pays one reorg-safe state read per tracked entry that had
    /// a nonce accepted. The threshold doubles after each sweep, even one that throws, so that cost is amortized across
    /// submissions. Without state at the reorg-safe block only the never-accepted entries go.
    /// </remarks>
    private void SweepConfirmed()
    {
        if (Interlocked.CompareExchange(ref _sweeping, 1, 0) != 0) return;

        try
        {
            BlockHeader? reorgSafeHeader = FindReorgSafeHeader();
            foreach (KeyValuePair<AddressAsKey, AddressNonceManager> entry in _addressNonceManagers)
            {
                ulong? reorgSafeNonce = reorgSafeHeader is not null && entry.Value.HasAcceptedNonce
                    ? GetReorgSafeNonce(reorgSafeHeader, entry.Key)
                    : null;
                if (entry.Value.TryRetire(reorgSafeNonce))
                {
                    _addressNonceManagers.TryRemove(entry);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _sweepThreshold, Math.Max(_minSweepThreshold, _addressNonceManagers.Count * 2));
            Volatile.Write(ref _sweeping, 0);
        }
    }

    /// <summary>
    /// The canonical header at <c>min(finalized, head - </c><see cref="Reorganization.MaxDepth"/><c>)</c>, or
    /// <c>null</c> when it or its state is unavailable.
    /// </summary>
    private BlockHeader? FindReorgSafeHeader()
    {
        ulong reorgSafeNumber = ulong.Min(
            _stateHeaderProvider.FinalizedBlockNumber,
            _chainHeadInfoProvider.HeadNumber.SaturatingSub(Reorganization.MaxDepth));
        BlockHeader? header = _stateHeaderProvider.GetFinalizedHeader(reorgSafeNumber);
        return header is not null && _stateReader.HasStateForBlock(header) ? header : null;
    }

    private ulong? GetReorgSafeNonce(BlockHeader reorgSafeHeader, Address address)
    {
        try
        {
            return _stateReader.GetNonce(reorgSafeHeader, address);
        }
        catch (MissingTrieNodeException)
        {
            // Pruned after FindReorgSafeHeader looked, which counts as unavailable, not as nonce 0.
            return null;
        }
    }

    private class AddressNonceManager
    {
        private readonly Dictionary<ulong, (Hash256 Hash, TxType Type)> _usedNonces = [];
        private readonly Action<Transaction> _txAccepted;
        private ulong _reservedNonce;
        private ulong _previousAccountNonce;
        private ulong? _highestAcceptedNonce;
        private volatile bool _isRetired;
        private ulong _freeNonceFloor;
        private ulong _floorAccountNonce;
        private long _floorRemovalGeneration = -1;

        private readonly SemaphoreSlim _accountLock = new(1);

        public AddressNonceManager() => _txAccepted = TxAccepted;

        public bool IsRetired => _isRetired;

        public bool HasAcceptedNonce => _highestAcceptedNonce.HasValue;

        /// <remarks>
        /// Rederived on every call, so a nonce whose transaction has since left the pool is handed out again. Every
        /// nonce below the free one the last call found was taken, and stays taken until the account nonce moves or
        /// the pool reports a removal for this sender, so while neither happened the search resumes there instead
        /// of at the account nonce; the removal generation is read before the search, so a removal racing it
        /// forces the next call back to the account nonce. The account lock is held until the locker is disposed,
        /// so there is never more than one reservation in flight and an accepted one is recorded before the next
        /// starts. The account nonce is read only once the lock is held, since a block may consume the previous
        /// floor while this call waits for it.
        /// </remarks>
        public NonceLocker ReserveNonce(Address address, IAccountStateProvider accounts, IPendingTxsBySender pendingTxs, out ulong reservedNonce)
        {
            NonceLocker locker = new(_accountLock, _txAccepted);
            try
            {
                ulong accountNonce = accounts.GetNonce(address);
                ReleaseNonces(accountNonce);
                long removalGeneration = pendingTxs.GetRemovalGeneration(address);
                ulong searchFrom = removalGeneration == _floorRemovalGeneration && accountNonce == _floorAccountNonce
                    ? _freeNonceFloor
                    : accountNonce;
                _reservedNonce = FindFreeNonce(address, searchFrom, pendingTxs);
                _freeNonceFloor = _reservedNonce;
                _floorAccountNonce = accountNonce;
                _floorRemovalGeneration = removalGeneration;
            }
            catch
            {
                locker.Dispose();
                throw;
            }

            reservedNonce = _reservedNonce;
            return locker;
        }

        private void TxAccepted(Transaction transaction)
        {
            _usedNonces[_reservedNonce] = (transaction.Hash!, transaction.Type);
            _highestAcceptedNonce = ulong.Max(_highestAcceptedNonce ?? 0, _reservedNonce);
        }

        /// <remarks>
        /// A nonce above <see cref="_highestAcceptedNonce"/> was never accepted here, so it is free without asking the
        /// pool. Up to it, a nonce is taken while the pool still holds its recorded transaction (the persistent
        /// broadcaster included) or any pending account-domain transaction of the sender at that nonce, which covers
        /// replacements and transactions returned to the pool by a reorg. The pool snapshots are taken only when
        /// the recorded transaction is gone. Only an accepted transaction changes the record, so a reservation that
        /// is never accepted leaves it as it found it.
        /// </remarks>
        private ulong FindFreeNonce(Address address, ulong searchFrom, IPendingTxsBySender pendingTxs)
        {
            Transaction[]? pending = null;
            Transaction[]? pendingBlobs = null;
            ulong nextUnusedNonce = _highestAcceptedNonce is { } highest ? highest + 1 : 0;
            ulong nonce = searchFrom;
            for (; nonce < nextUnusedNonce; nonce++)
            {
                if (_usedNonces.TryGetValue(nonce, out (Hash256 Hash, TxType Type) used) && pendingTxs.ContainsTx(used.Hash, used.Type))
                {
                    continue;
                }

                pending ??= pendingTxs.GetPendingTransactionsBySender(address);
                pendingBlobs ??= pendingTxs.GetPendingLightBlobTransactionsBySender(address);
                if (!HoldsAccountNonce(pending, nonce) && !HoldsAccountNonce(pendingBlobs, nonce))
                {
                    return nonce;
                }
            }

            return nonce;
        }

        private static bool HoldsAccountNonce(Transaction[] transactions, ulong nonce)
        {
            foreach (Transaction transaction in transactions)
            {
                if (transaction.Nonce == nonce && !KeyedNonceManager.UsesKeyedNonce(transaction))
                {
                    return true;
                }
            }

            return false;
        }

        public NonceLocker TxWithNonceReceived(ulong nonce)
        {
            NonceLocker locker = new(_accountLock, _txAccepted);
            _reservedNonce = nonce;
            return locker;
        }

        /// <param name="reorgSafeNonce">
        /// The account nonce at the reorg-safe block, or <c>null</c> when that state is unavailable.
        /// </param>
        public bool TryRetire(ulong? reorgSafeNonce)
        {
            if (!_accountLock.Wait(0)) return false;

            try
            {
                bool everyAcceptedNonceIsReorgSafe = _highestAcceptedNonce is null || _highestAcceptedNonce < reorgSafeNonce;
                if (!everyAcceptedNonceIsReorgSafe) return false;

                _isRetired = true;
                return true;
            }
            finally
            {
                _accountLock.Release();
            }
        }

        private void ReleaseNonces(ulong accountNonce)
        {
            if (accountNonce > _previousAccountNonce)
            {
                if ((ulong)_usedNonces.Count < accountNonce - _previousAccountNonce)
                {
                    foreach (ulong nonce in _usedNonces.Keys)
                    {
                        if (nonce >= _previousAccountNonce && nonce < accountNonce)
                        {
                            _usedNonces.Remove(nonce);
                        }
                    }
                }
                else
                {
                    for (ulong i = _previousAccountNonce; i < accountNonce; i++)
                    {
                        _usedNonces.Remove(i);
                    }
                }
            }

            _previousAccountNonce = accountNonce;
        }
    }
}
