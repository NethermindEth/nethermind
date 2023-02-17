// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;

namespace Nethermind.TxPool
{
    /// <summary>
    /// Hash cache prevents transactions from being analyzed multiple times.
    /// It has a 2-layer structure -> transactions received many times within the same block will be ignored after
    /// the first check (using this block cache).
    /// After new chain head, the current block should be reset so that transactions can be analyzed again in case
    /// conditions changed (sender balance, basefee, etc.)
    /// User responsibility is to clear the current block cache when current block changes.
    /// While it could be natural to rename CurrentBlock to CurrentScope, this class
    /// exists as an internal helper for TX pool and I am aware only of a block scope use cases.
    /// I claim that this class is thread safe due to thread safety of underlying structures and
    /// careful ordering of the operations.
    /// </summary>
    internal class HashCache
    {
        private const int SafeCapacity = 1024 * 16;

        private readonly LruKeyCache<KeccakKey> _longTermCache = new(
            MemoryAllowance.TxHashCacheSize,
            Math.Min(SafeCapacity, MemoryAllowance.TxHashCacheSize),
            "long term hash cache");

        private readonly LruKeyCache<KeccakKey> _currentBlockCache = new(
            SafeCapacity,
            Math.Min(SafeCapacity, MemoryAllowance.TxHashCacheSize),
            "current block hash cache");

        public bool Get(Keccak hash)
        {
            return _currentBlockCache.Get(hash) || _longTermCache.Get(hash);
        }

        public void SetLongTerm(Keccak hash)
        {
            _longTermCache.Set(hash);
        }

        public void SetForCurrentBlock(Keccak hash)
        {
            _currentBlockCache.Set(hash);
        }

        public void DeleteFromLongTerm(Keccak hash)
        {
            _longTermCache.Delete(hash);
        }

        public void Delete(Keccak hash)
        {
            _longTermCache.Delete(hash);
            _currentBlockCache.Delete(hash);
        }

        public void ClearCurrentBlockCache()
        {
            _currentBlockCache.Clear();
        }
    }
}
