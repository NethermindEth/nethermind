// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Resettables;
using Nethermind.Evm.Tracing.State;
using Nethermind.Logging;

namespace Nethermind.State
{
    /// <summary>
    /// Contains common code for both Persistent and Transient storage providers
    /// </summary>
    internal abstract class PartialStorageProviderBase
    {
        protected readonly Dictionary<StorageCell, StackList<int>> _intraBlockCache = new();
        protected readonly ILogger _logger;
        protected readonly List<Change> _changes = new(Resettable.StartCapacity);
        private readonly List<Change> _keptInCache = new();

        // stack of snapshot indexes on changes for start of each transaction
        // this is needed for OriginalValues for new transactions
        protected readonly Stack<int> _transactionChangesSnapshots = new();

        protected PartialStorageProviderBase(ILogManager? logManager)
        {
            _logger = logManager?.GetClassLogger<PartialStorageProviderBase>() ?? throw new ArgumentNullException(nameof(logManager));
        }

        /// <summary>
        /// Get the storage value at the specified storage cell
        /// </summary>
        /// <param name="storageCell">Storage location</param>
        /// <returns>Value at cell</returns>
        public ReadOnlySpan<byte> Get(in StorageCell storageCell)
        {
            return GetCurrentValue(in storageCell);
        }

        /// <summary>
        /// Set the provided value to storage at the specified storage cell
        /// </summary>
        /// <param name="storageCell">Storage location</param>
        /// <param name="newValue">Value to store</param>
        public void Set(in StorageCell storageCell, byte[] newValue)
        {
            PushUpdate(in storageCell, newValue);
        }

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
                Change change = _changes[currentPosition - i];
                StackList<int> stack = _intraBlockCache[change!.StorageCell];
                if (stack.Count == 1)
                {
                    if (_changes[stack.Peek()]!.ChangeType == ChangeType.JustCache)
                    {
                        int actualPosition = stack.Pop();
                        if (actualPosition != currentPosition - i)
                        {
                            throw new InvalidOperationException($"Expected actual position {actualPosition} to be equal to {currentPosition} - {i}");
                        }

                        _keptInCache.Add(change);
                        _changes[actualPosition] = default;
                        continue;
                    }
                }

                int forAssertion = stack.Pop();
                if (forAssertion != currentPosition - i)
                {
                    throw new InvalidOperationException($"Expected checked value {forAssertion} to be equal to {currentPosition} - {i}");
                }

                _changes[currentPosition - i] = default;

                if (stack.Count == 0)
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
                _intraBlockCache[kept.StorageCell].Push(currentPosition);
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
        public void Commit(bool commitRoots = true)
        {
            Commit(NullStateTracer.Instance, commitRoots);
        }

        protected struct ChangeTrace
        {
            public static readonly ChangeTrace _zeroBytes = new(StorageTree.ZeroBytes, StorageTree.ZeroBytes);
            public static ref readonly ChangeTrace ZeroBytes => ref _zeroBytes;

            public ChangeTrace(byte[]? before, byte[]? after)
            {
                After = after ?? StorageTree.ZeroBytes;
                Before = before ?? StorageTree.ZeroBytes;
            }

            public ChangeTrace(byte[]? after)
            {
                After = after ?? StorageTree.ZeroBytes;
                Before = StorageTree.ZeroBytes;
            }

            public byte[] Before;
            public byte[] After;
        }

        /// <summary>
        /// Commit persistent storage
        /// </summary>
        /// <param name="stateTracer">State tracer</param>
        public void Commit(IStorageTracer tracer, bool commitRoots = true)
        {
            if (_changes.Count == 0)
            {
                if (_logger.IsTrace) _logger.Trace("No storage changes to commit");
            }
            else
            {
                CommitCore(tracer);
            }

            if (commitRoots)
            {
                CommitStorageRoots();
            }
        }

        protected virtual void CommitStorageRoots()
        {
            // Commit storage roots
        }

        /// <summary>
        /// Called by Commit
        /// Used for storage-specific logic
        /// </summary>
        /// <param name="tracer">Storage tracer</param>
        protected virtual void CommitCore(IStorageTracer tracer)
        {
            _changes.Clear();
            _intraBlockCache.Clear();
            _transactionChangesSnapshots.Clear();
        }

        /// <summary>
        /// Reset the storage state
        /// </summary>
        public virtual void Reset(bool resetBlockChanges = true)
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
            if (_intraBlockCache.TryGetValue(storageCell, out StackList<int> stack))
            {
                int lastChangeIndex = stack.Peek();
                {
                    bytes = _changes[lastChangeIndex].Value;
                    return true;
                }
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
            StackList<int> stack = SetupRegistry(cell);
            stack.Push(_changes.Count);
            _changes.Add(new Change(in cell, value, ChangeType.Update));
        }

        /// <summary>
        /// Initialize the StackList at the storage cell position if needed
        /// </summary>
        /// <param name="cell"></param>
        protected StackList<int> SetupRegistry(in StorageCell cell)
        {
            ref StackList<int>? value = ref CollectionsMarshal.GetValueRefOrAddDefault(_intraBlockCache, cell, out bool exists);
            if (!exists)
            {
                value = new StackList<int>();
            }

            return value;
        }

        /// <summary>
        /// Clear all storage at specified address
        /// </summary>
        /// <param name="address">Contract address</param>
        public virtual void ClearStorage(Address address)
        {
            // We are setting cached values to zero so we do not use previously set values
            // when the contract is revived with CREATE2 inside the same block
            foreach (KeyValuePair<StorageCell, StackList<int>> cellByAddress in _intraBlockCache)
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
        protected readonly struct Change(in StorageCell storageCell, byte[] value, ChangeType changeType)
        {
            public readonly StorageCell StorageCell = storageCell;
            public readonly byte[] Value = value;
            public readonly ChangeType ChangeType = changeType;

            public bool IsNull => ChangeType == ChangeType.Null;
        }

        /// <summary>
        /// Type of change to track
        /// </summary>
        protected enum ChangeType
        {
            Null = 0,
            JustCache,
            Update,
            Destroy,
        }
    }
}
