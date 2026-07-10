// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Resettables;
using Nethermind.Evm.Tracing.State;
using Nethermind.Logging;

namespace Nethermind.State
{
    /// <summary>
    /// Contains common code for both Persistent and Transient storage providers
    /// </summary>
    internal abstract class PartialStorageProviderBase(ILogManager? logManager)
    {
        protected readonly Dictionary<StorageCell, HeadChange> _intraBlockCache = [];
        protected readonly ILogger _logger = logManager?.GetClassLogger<PartialStorageProviderBase>() ?? throw new ArgumentNullException(nameof(logManager));
        protected readonly List<Change> _changes = new(Resettable.StartCapacity);
        private readonly List<Change> _keptInCache = [];

        // stack of snapshot indexes on changes for start of each transaction
        // this is needed for OriginalValues for new transactions
        protected readonly Stack<int> _transactionChangesSnapshots = new();

        /// <summary>
        /// Get the storage value at the specified storage cell
        /// </summary>
        /// <param name="storageCell">Storage location</param>
        /// <returns>Value at cell</returns>
        public ReadOnlySpan<byte> Get(in StorageCell storageCell) => GetCurrentValue(in storageCell);

        /// <summary>
        /// Set the provided value to storage at the specified storage cell
        /// </summary>
        /// <param name="storageCell">Storage location</param>
        /// <param name="newValue">Value to store</param>
        public virtual void Set(in StorageCell storageCell, byte[] newValue) => PushUpdate(in storageCell, newValue);

        /// <summary>
        /// Creates a restartable snapshot.
        /// </summary>
        /// <param name="newTransactionStart"> Indicates new transaction will start here.</param>
        /// <returns>Snapshot index</returns>
        public int TakeSnapshot(bool newTransactionStart)
        {
            int position = _changes.Count - 1;
            if (_logger.IsTrace) _logger.Trace($"Storage snapshot {position}");
            if (newTransactionStart && position != Resettable.EmptyPosition)
            {
                _transactionChangesSnapshots.Push(position);
            }

            return position;
        }

        /// <summary>
        /// Restore the state to the provided snapshot
        /// </summary>
        /// <param name="snapshot">Snapshot index</param>
        /// <exception cref="InvalidOperationException">Throws exception if snapshot is invalid</exception>
        public void Restore(int snapshot)
        {
            if (_logger.IsTrace) _logger.Trace($"Restoring storage snapshot {snapshot}");

            int currentPosition = _changes.Count - 1;
            if (snapshot > currentPosition)
            {
                throw new InvalidOperationException($"{GetType().Name} tried to restore snapshot {snapshot} beyond current position {currentPosition}");
            }

            if (snapshot == currentPosition)
            {
                return;
            }

            for (int i = 0; i < currentPosition - snapshot; i++)
            {
                int position = currentPosition - i;
                Change change = _changes[position];
                ref HeadChange head = ref CollectionsMarshal.GetValueRefOrNullRef(_intraBlockCache, change.StorageCell);
                if (Unsafe.IsNullRef(ref head))
                {
                    throw new InvalidOperationException($"Missing head entry for {change.StorageCell} at position {position}");
                }

                if (head.CurrentIdx != position)
                {
                    throw new InvalidOperationException($"Expected checked value {head.CurrentIdx} to be equal to {currentPosition} - {i}");
                }

                if (change.PrevIdx != -1)
                {
                    Change previous = _changes[change.PrevIdx];
                    head = new HeadChange(previous.Value, change.PrevIdx, previous.OriginalIdx);
                }
                else if (change.ChangeType == ChangeType.JustCache)
                {
                    // Keep the read-only entry; its head is stale until re-appended below.
                    _keptInCache.Add(change);
                }
                else
                {
                    _intraBlockCache.Remove(change.StorageCell);
                }
            }

            CollectionsMarshal.SetCount(_changes, snapshot + 1);
            currentPosition = _changes.Count - 1;
            foreach (Change kept in _keptInCache)
            {
                currentPosition++;
                _changes.Add(kept);
                _intraBlockCache[kept.StorageCell] = new HeadChange(kept.Value, currentPosition, kept.OriginalIdx);
            }

            _keptInCache.Clear();

            while (_transactionChangesSnapshots.TryPeek(out int lastOriginalSnapshot) && lastOriginalSnapshot > snapshot)
            {
                _transactionChangesSnapshots.Pop();
            }

        }

        /// <summary>
        /// Commit persistent storage
        /// </summary>
        /// <param name="stateTracer">State tracer</param>
        public virtual void Commit(IStorageTracer tracer)
        {
            if (_changes.Count == 0)
            {
                if (_logger.IsTrace) _logger.Trace("No storage changes to commit");
            }
            else
            {
                CommitCore(tracer);
            }
        }

        /// <summary>
        /// Called by Commit
        /// Used for storage-specific logic
        /// </summary>
        /// <param name="tracer">Storage tracer</param>
        protected virtual void CommitCore(IStorageTracer tracer) => Reset();

        /// <summary>
        /// Reset the storage state
        /// </summary>
        public virtual void Reset(bool resetBlockChanges = true) => Reset();

        private void Reset()
        {
            if (_logger.IsTrace) _logger.Trace("Resetting storage");

            _changes.Clear();
            _intraBlockCache.Clear();
            _transactionChangesSnapshots.Clear();
        }

        /// <summary>
        /// Attempt to get the current value at the storage cell
        /// </summary>
        /// <param name="storageCell">Storage location</param>
        /// <param name="bytes">Resulting value</param>
        /// <returns>True if value has been set</returns>
        protected bool TryGetCachedValue(in StorageCell storageCell, out byte[]? bytes)
        {
            // If the cache is completely empty (no writes or reads yet this transaction),
            // skip hashing the 52-byte cell — TryGetValue would miss anyway.
            if (_intraBlockCache.Count != 0 && _intraBlockCache.TryGetValue(storageCell, out HeadChange head))
            {
                bytes = head.Value;
                return true;
            }

            bytes = null;
            return false;
        }

        /// <summary>
        /// Get the current value at the specified location
        /// </summary>
        /// <param name="storageCell">Storage location</param>
        /// <returns>Value at location</returns>
        protected abstract ReadOnlySpan<byte> GetCurrentValue(in StorageCell storageCell);

        /// <summary>
        /// Update the storage cell with provided value
        /// </summary>
        /// <param name="cell">Storage location</param>
        /// <param name="value">Value to set</param>
        private void PushUpdate(in StorageCell cell, byte[] value)
        {
            // Overwrites the head in place, never removes+re-adds — ClearStorage relies on this
            // to legally call Set while enumerating _intraBlockCache.
            ref HeadChange head = ref CollectionsMarshal.GetValueRefOrAddDefault(_intraBlockCache, cell, out bool exists);
            int prevIdx = exists ? head.CurrentIdx : -1;

            // The first write to a cell in a tx (head at or before the tx boundary) captures the
            // overwritten value as the tx original; later writes carry it forward.
            int currentSnapshot = _transactionChangesSnapshots.TryPeek(out int s) ? s : Resettable.EmptyPosition;
            bool firstWriteThisTx = !exists || head.CurrentIdx <= currentSnapshot;
            int originalIdx = firstWriteThisTx ? prevIdx : head.OriginalIdx;

            head = new HeadChange(value, _changes.Count, originalIdx);
            _changes.Add(new Change(in cell, value, ChangeType.Update, prevIdx, originalIdx));
        }

        /// <summary>
        /// Clear all storage at specified address
        /// </summary>
        /// <param name="address">Contract address</param>
        public virtual void ClearStorage(Address address)
        {
            // We are setting cached values to zero so we do not use previously set values
            // when the contract is revived with CREATE2 inside the same block.
            // Set never adds or removes keys here, so enumerating while mutating is legal.
            foreach (KeyValuePair<StorageCell, HeadChange> cellByAddress in _intraBlockCache)
            {
                if (cellByAddress.Key.Address == address)
                {
                    Set(cellByAddress.Key, StorageTree.ZeroBytes);
                }
            }
        }

        /// <summary>
        /// Used for tracking each change to storage
        /// </summary>
        protected readonly struct Change(in StorageCell storageCell, byte[] value, ChangeType changeType, int prevIdx, int originalIdx)
        {
            public readonly StorageCell StorageCell = storageCell;
            public readonly byte[] Value = value;
            public readonly ChangeType ChangeType = changeType;

            /// <summary>Index into <c>_changes</c> of the previous change for the same cell, or -1 if none.</summary>
            public readonly int PrevIdx = prevIdx;

            /// <summary>
            /// Index into <c>_changes</c> of this cell's value at the transaction's start (its EIP-2200
            /// "original"), or -1 when that is the block-level value in <c>_originalValues</c>. Carried
            /// forward on later same-tx writes so <see cref="PersistentStorageProvider.GetOriginal"/> is O(1).
            /// </summary>
            public readonly int OriginalIdx = originalIdx;

            public bool IsNull => ChangeType == ChangeType.Null;
        }

        /// <summary>
        /// Head of a cell's change chain, with the newest value and original-index inlined so reads
        /// and <see cref="PersistentStorageProvider.GetOriginal"/> resolve with a single lookup.
        /// </summary>
        protected readonly struct HeadChange(byte[] value, int currentIdx, int originalIdx)
        {
            public readonly byte[] Value = value;
            public readonly int CurrentIdx = currentIdx;
            public readonly int OriginalIdx = originalIdx;
        }

        /// <summary>
        /// Type of change to track
        /// </summary>
        protected enum ChangeType
        {
            Null = 0,
            JustCache,
            Update,
        }
    }
}
