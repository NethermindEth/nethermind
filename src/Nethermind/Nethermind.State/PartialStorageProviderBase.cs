// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Resettables;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.State
{
    /// <summary>
    /// Contains common code for both Persistent and Transient storage providers
    /// </summary>
    public abstract class PartialStorageProviderBase
    {
        protected readonly ResettableDictionary<StorageCell, StackList<int>> _intraBlockCache = new();

        protected readonly ILogger _logger;

        private const int StartCapacity = Resettable.StartCapacity;
        private int _capacity = StartCapacity;
        protected Change?[] _changes = new Change[StartCapacity];
        protected int _currentPosition = Resettable.EmptyPosition;

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
        public UInt256 Get(in StorageCell storageCell)
        {
            return GetCurrentValue(in storageCell);
        }

        /// <summary>
        /// Set the provided value to storage at the specified storage cell
        /// </summary>
        /// <param name="storageCell">Storage location</param>
        /// <param name="newValue">Value to store</param>
        public void Set(in StorageCell storageCell, in UInt256 newValue)
        {
            PushUpdate(in storageCell, in newValue);
        }

        /// <summary>
        /// Creates a restartable snapshot.
        /// </summary>
        /// <param name="newTransactionStart"> Indicates new transaction will start here.</param>
        /// <returns>Snapshot index</returns>
        public int TakeSnapshot(bool newTransactionStart)
        {
            if (_logger.IsTrace) _logger.Trace($"Storage snapshot {_currentPosition}");
            if (newTransactionStart && _currentPosition != Resettable.EmptyPosition)
            {
                _transactionChangesSnapshots.Push(_currentPosition);
            }

            return _currentPosition;
        }

        /// <summary>
        /// Restore the state to the provided snapshot
        /// </summary>
        /// <param name="snapshot">Snapshot index</param>
        /// <exception cref="InvalidOperationException">Throws exception if snapshot is invalid</exception>
        public void Restore(int snapshot)
        {
            if (_logger.IsTrace) _logger.Trace($"Restoring storage snapshot {snapshot}");

            if (snapshot > _currentPosition)
            {
                throw new InvalidOperationException($"{GetType().Name} tried to restore snapshot {snapshot} beyond current position {_currentPosition}");
            }

            if (snapshot == _currentPosition)
            {
                return;
            }

            List<Change> keptInCache = new();

            for (int i = 0; i < _currentPosition - snapshot; i++)
            {
                Change change = _changes[_currentPosition - i];
                if (_intraBlockCache[change!.StorageCell].Count == 1)
                {
                    if (_changes[_intraBlockCache[change.StorageCell].Peek()]!.ChangeType == ChangeType.JustCache)
                    {
                        int actualPosition = _intraBlockCache[change.StorageCell].Pop();
                        if (actualPosition != _currentPosition - i)
                        {
                            throw new InvalidOperationException($"Expected actual position {actualPosition} to be equal to {_currentPosition} - {i}");
                        }

                        keptInCache.Add(change);
                        _changes[actualPosition] = null;
                        continue;
                    }
                }

                int forAssertion = _intraBlockCache[change.StorageCell].Pop();
                if (forAssertion != _currentPosition - i)
                {
                    throw new InvalidOperationException($"Expected checked value {forAssertion} to be equal to {_currentPosition} - {i}");
                }

                _changes[_currentPosition - i] = null;

                if (_intraBlockCache[change.StorageCell].Count == 0)
                {
                    _intraBlockCache.Remove(change.StorageCell);
                }
            }

            _currentPosition = snapshot;
            foreach (Change kept in keptInCache)
            {
                _currentPosition++;
                _changes[_currentPosition] = kept;
                _intraBlockCache[kept.StorageCell].Push(_currentPosition);
            }

            while (_transactionChangesSnapshots.TryPeek(out int lastOriginalSnapshot) && lastOriginalSnapshot > snapshot)
            {
                _transactionChangesSnapshots.Pop();
            }

        }

        /// <summary>
        /// Commit persistent storage
        /// </summary>
        public void Commit()
        {
            Commit(NullStorageTracer.Instance);
        }

        protected readonly struct ChangeTrace
        {
            public ChangeTrace(in UInt256 before, in UInt256 after)
            {
                After = after;
                Before = before;
            }

            public ChangeTrace(in UInt256 after)
            {
                After = after;
                Before = default;
            }

            public readonly UInt256 Before;
            public readonly UInt256 After;
        }

        /// <summary>
        /// Commit persistent storage
        /// </summary>
        /// <param name="stateTracer">State tracer</param>
        public void Commit(IStorageTracer tracer)
        {
            if (_currentPosition == Snapshot.EmptyPosition)
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
        protected virtual void CommitCore(IStorageTracer tracer)
        {
            Resettable<Change>.Reset(ref _changes, ref _capacity, ref _currentPosition);
            _intraBlockCache.Reset();
            _transactionChangesSnapshots.Clear();
        }

        /// <summary>
        /// Reset the storage state
        /// </summary>
        public virtual void Reset()
        {
            if (_logger.IsTrace) _logger.Trace("Resetting storage");

            _intraBlockCache.Clear();
            _transactionChangesSnapshots.Clear();
            _currentPosition = -1;
            Array.Clear(_changes, 0, _changes.Length);
        }

        /// <summary>
        /// Attempt to get the current value at the storage cell
        /// </summary>
        /// <param name="storageCell">Storage location</param>
        /// <param name="bytes">Resulting value</param>
        /// <returns>True if value has been set</returns>
        protected bool TryGetCachedValue(in StorageCell storageCell, out UInt256 bytes)
        {
            if (_intraBlockCache.TryGetValue(storageCell, out StackList<int> stack))
            {
                int lastChangeIndex = stack.Peek();
                {
                    bytes = _changes[lastChangeIndex]!.Value;
                    return true;
                }
            }

            bytes = default;
            return false;
        }

        /// <summary>
        /// Get the current value at the specified location
        /// </summary>
        /// <param name="storageCell">Storage location</param>
        /// <returns>Value at location</returns>
        protected abstract UInt256 GetCurrentValue(in StorageCell storageCell);

        /// <summary>
        /// Update the storage cell with provided value
        /// </summary>
        /// <param name="cell">Storage location</param>
        /// <param name="value">Value to set</param>
        private void PushUpdate(in StorageCell cell, in UInt256 value)
        {
            SetupRegistry(cell);
            IncrementChangePosition();
            _intraBlockCache[cell].Push(_currentPosition);
            _changes[_currentPosition] = new Change(ChangeType.Update, cell, in value);
        }

        /// <summary>
        /// Increment position and size (if needed) of _changes
        /// </summary>
        protected void IncrementChangePosition()
        {
            Resettable<Change>.IncrementPosition(ref _changes, ref _capacity, ref _currentPosition);
        }

        /// <summary>
        /// Initialize the StackList at the storage cell position if needed
        /// </summary>
        /// <param name="cell"></param>
        protected void SetupRegistry(in StorageCell cell)
        {
            if (!_intraBlockCache.ContainsKey(cell))
            {
                _intraBlockCache[cell] = new StackList<int>();
            }
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
                    Set(cellByAddress.Key, default);
                }
            }
        }

        /// <summary>
        /// Used for tracking each change to storage
        /// </summary>
        protected class Change
        {
            public Change(ChangeType changeType, in StorageCell storageCell, in UInt256 value)
            {
                StorageCell = storageCell;
                Value = value;
                ChangeType = changeType;
            }

            public ChangeType ChangeType { get; }
            public StorageCell StorageCell { get; }
            public UInt256 Value { get; }
        }

        /// <summary>
        /// Type of change to track
        /// </summary>
        protected enum ChangeType
        {
            JustCache,
            Update,
            Destroy,
        }
    }
}
