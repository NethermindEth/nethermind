// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        private const int InvalidBlobProofsCapacity = 1024;

        private readonly AssociativeKeyCache<ValueHash256> _longTermCache = new(
            MemoryAllowance.TxHashCacheSize);

        private readonly AssociativeKeyCache<ValueHash256> _currentBlockCache = new(
            SafeCapacity);

        // Current-block hashes whose verdict was InvalidBlobProofs: the hash does not cover the sidecar,
        // so only these may be forgotten for another copy to be judged.
        private readonly AssociativeKeyCache<ValueHash256> _invalidBlobProofsCache = new(
            InvalidBlobProofsCapacity);

        public bool Get(Hash256 hash) => Get(in hash.ValueHash256);

        public bool Get(in ValueHash256 hash) =>
            _currentBlockCache.GetNoRefresh(in hash) || _longTermCache.Get(in hash);

        public void SetLongTerm(Hash256 hash)
        {
            ref readonly ValueHash256 valueHash = ref hash.ValueHash256;
            _longTermCache.Set(in valueHash);
        }

        public void SetForCurrentBlock(Hash256 hash)
        {
            ref readonly ValueHash256 valueHash = ref hash.ValueHash256;
            _currentBlockCache.Set(in valueHash);
        }

        public void DeleteFromLongTerm(Hash256 hash)
        {
            ref readonly ValueHash256 valueHash = ref hash.ValueHash256;
            _longTermCache.Delete(in valueHash);
        }

        public void DeleteFromCurrentBlock(Hash256 hash) => DeleteFromCurrentBlock(in hash.ValueHash256);

        public void DeleteFromCurrentBlock(in ValueHash256 hash)
        {
            _invalidBlobProofsCache.Delete(in hash);
            _currentBlockCache.Delete(in hash);
        }

        public void SetInvalidBlobProofs(Hash256 hash) => _invalidBlobProofsCache.Set(in hash.ValueHash256);

        /// <summary>
        /// Deletes <paramref name="hash"/> from the current block cache only when its verdict was InvalidBlobProofs.
        /// </summary>
        public bool TryDeleteInvalidBlobProofs(Hash256 hash)
        {
            ref readonly ValueHash256 valueHash = ref hash.ValueHash256;
            if (!_invalidBlobProofsCache.Delete(in valueHash)) return false;
            _currentBlockCache.Delete(in valueHash);
            return true;
        }

        public void Delete(Hash256 hash)
        {
            ref readonly ValueHash256 valueHash = ref hash.ValueHash256;
            _longTermCache.Delete(in valueHash);
            _currentBlockCache.Delete(in valueHash);
        }

        public void ClearCurrentBlockCache()
        {
            _invalidBlobProofsCache.Clear();
            _currentBlockCache.Clear();
        }

        /// <summary>
        /// Clears both long-term and current block caches.
        /// </summary>
        public void ClearAll()
        {
            _longTermCache.Clear();
            _invalidBlobProofsCache.Clear();
            _currentBlockCache.Clear();
        }
    }
}
