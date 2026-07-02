// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Core.Collections
{
    /// <summary>
    /// A snapshot/restore set like <see cref="JournalSet{T}"/> whose <see cref="Reset"/> empties it
    /// without touching hash storage: membership means "stamped with the current epoch", so advancing
    /// the epoch makes every retained entry cold again.
    /// </summary>
    /// <typeparam name="T">Item type; constrained to <c>struct</c> so keys are never null.</typeparam>
    /// <remarks>
    /// Designed for the per-transaction EIP-2929 warm/cold sets: instead of clearing (O(high-water
    /// capacity) for a <see cref="HashSet{T}"/>) and re-inserting recurring keys every transaction,
    /// entries persist across <see cref="Reset"/> with an epoch stamp and re-warming a known key is a
    /// single lookup plus stamp write. The journal list tracks the order of cold-to-warm transitions
    /// in the current epoch, enabling <see cref="Restore"/> and warm-items-only enumeration.
    /// Due to snapshots <see cref="Remove"/> is not supported.
    /// </remarks>
    public sealed class VersionedJournalSet<T>(EqualityComparer<T> equalityComparer) : ICollection<T>, IJournal<int>
        where T : struct
    {
        private const int ColdEpoch = 0;

        /// <summary>
        /// Bound on entries retained across <see cref="Reset"/> before hash storage is dropped,
        /// keeping long-lived (pooled) instances from growing without limit.
        /// </summary>
        private const int RetainedEntriesLimit = 1 << 16;

        private readonly Dictionary<T, int> _warmedAt = new(GenericEqualityComparer.GetOptimized(equalityComparer));
        private readonly List<T> _journal = [];
        private int _epoch = 1;

        public int TakeSnapshot() => Position;

        private int Position => Count - 1;

        [SkipLocalsInit]
        public void Restore(int snapshot)
        {
            if (snapshot >= Count)
            {
                ThrowInvalidRestore(snapshot);
            }

            // Items journaled after the snapshot were cold at snapshot time in this epoch,
            // so any stamp other than the current epoch marks them cold again.
            foreach (T item in CollectionsMarshal.AsSpan(_journal)[(snapshot + 1)..])
            {
                CollectionsMarshal.GetValueRefOrNullRef(_warmedAt, item) = ColdEpoch;
            }

            CollectionsMarshal.SetCount(_journal, snapshot + 1);
        }

        [DoesNotReturn, StackTraceHidden]
        private void ThrowInvalidRestore(int snapshot)
            => throw new InvalidOperationException($"{nameof(VersionedJournalSet<T>)} tried to restore snapshot {snapshot} beyond current position {Count}");

        public bool Add(T item)
        {
            ref int epoch = ref CollectionsMarshal.GetValueRefOrAddDefault(_warmedAt, item, out _);
            if (epoch == _epoch)
            {
                return false;
            }

            epoch = _epoch;
            _journal.Add(item);
            return true;
        }

        /// <summary>
        /// Empties the set in O(items added this epoch) by advancing the epoch; hash entries are
        /// retained (up to <see cref="RetainedEntriesLimit"/>) so recurring keys re-warm cheaply.
        /// </summary>
        public void Reset()
        {
            _journal.Clear();
            if (++_epoch == int.MaxValue || _warmedAt.Count > RetainedEntriesLimit)
            {
                _warmedAt.Clear();
                _epoch = 1;
            }
        }

        /// <summary>Fully clears the set including retained hash storage; prefer <see cref="Reset"/>.</summary>
        public void Clear()
        {
            _journal.Clear();
            _warmedAt.Clear();
            _epoch = 1;
        }

        public List<T>.Enumerator GetEnumerator() => _journal.GetEnumerator();
        IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public bool Remove(T item) => throw new NotSupportedException("Cannot remove from Journal, use Restore(int snapshot) instead.");
        public int Count => _journal.Count;
        public bool IsReadOnly => false;
        void ICollection<T>.Add(T item) => Add(item);
        public bool Contains(T item) => _warmedAt.TryGetValue(item, out int epoch) && epoch == _epoch;
        public void CopyTo(T[] array, int arrayIndex) => _journal.CopyTo(array, arrayIndex);
    }
}
