//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Core.Resettables;
using Nethermind.Logging;

namespace Nethermind.State
{
    /// <summary>
    /// EIP-1153 provides a transient store for contracts that doesn't persist
    /// storage across calls. Reverts will rollback any transient state changes.
    /// </summary>
    public class TransientStorageProvider : IPartialStorageProvider
    {
        private readonly ResettableDictionary<StorageCell, StackList<int>> _intraBlockCache = new();

        /// <summary>
        /// EIP-1283
        /// </summary>
        private readonly ResettableDictionary<StorageCell, byte[]> _originalValues = new();

        private readonly ResettableHashSet<StorageCell> _committedThisRound = new();

        private readonly ILogger _logger;
        private readonly ILogManager _logManager;

        private readonly ResettableDictionary<Address, StorageTree> _storages = new();

        private const int StartCapacity = Resettable.StartCapacity;
        private int _capacity = StartCapacity;
        private Change?[] _changes = new Change[StartCapacity];
        private int _currentPosition = Resettable.EmptyPosition;

        // stack of snapshot indexes on changes for start of each transaction
        // this is needed for OriginalValues for new transactions
        private readonly Stack<int> _transactionChangesSnapshots = new();

        public TransientStorageProvider(ILogManager? logManager)
        {
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _logger = logManager.GetClassLogger<TransientStorageProvider>() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public byte[] GetOriginal(StorageCell storageCell)
        {
            if (!_originalValues.ContainsKey(storageCell))
            {
                throw new InvalidOperationException("Get original should only be called after get within the same caching round");
            }

            if (_transactionChangesSnapshots.TryPeek(out int snapshot))
            {
                if (_intraBlockCache.TryGetValue(storageCell, out StackList<int> stack))
                {
                    if (stack.TryGetSearchedItem(snapshot, out int lastChangeIndexBeforeOriginalSnapshot))
                    {
                        return _changes[lastChangeIndexBeforeOriginalSnapshot]!.Value;
                    }
                }
            }

            return _originalValues[storageCell];
        }

        public byte[] Get(StorageCell storageCell)
        {
            return GetCurrentValue(storageCell);
        }

        public void Set(StorageCell storageCell, byte[] newValue)
        {
            PushUpdate(storageCell, newValue);
        }

        int IPartialStorageProvider.TakeSnapshot(bool newTransactionStart)
        {
            if (_logger.IsTrace) _logger.Trace($"Storage snapshot {_currentPosition}");
            if (newTransactionStart && _currentPosition != Resettable.EmptyPosition)
            {
                _transactionChangesSnapshots.Push(_currentPosition);
            }
            return _currentPosition;
        }

        /// <summary>
        /// Restore transient state to the snapshot index
        /// </summary>
        /// <param name="snapshot"></param>
        /// <exception cref="InvalidOperationException"></exception>
        public void Restore(int snapshot)
        {
            if (_logger.IsTrace) _logger.Trace($"Restoring storage snapshot {snapshot}");

            if (snapshot > _currentPosition)
            {
                throw new InvalidOperationException($"{nameof(TransientStorageProvider)} tried to restore snapshot {snapshot} beyond current position {_currentPosition}");
            }
            
            if (snapshot == _currentPosition)
            {
                return;
            }

            for (int i = 0; i < _currentPosition - snapshot; i++)
            {
                Change change = _changes[_currentPosition - i];

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
            
            while (_transactionChangesSnapshots.TryPeek(out int lastOriginalSnapshot) && lastOriginalSnapshot > snapshot)
            {
                _transactionChangesSnapshots.Pop();
            }
        }

        public void Commit()
        {
            Commit(NullStorageTracer.Instance);
        }

        private static readonly byte[] _zeroValue = {0};

        private readonly struct ChangeTrace
        {
            public ChangeTrace(byte[]? before, byte[]? after)
            {
                After = after ?? _zeroValue;
                Before = before ?? _zeroValue;
            }

            public ChangeTrace(byte[]? after)
            {
                After = after ?? _zeroValue;
                Before = _zeroValue;
            }

            public byte[] Before { get; }
            public byte[] After { get; }
        }

        /// <summary>
        /// Nothing to commit to permanent storage
        /// Reset the caches and return
        /// </summary>
        /// <param name="tracer"></param>
        /// <exception cref="InvalidOperationException"></exception>
        public void Commit(IStorageTracer tracer)
        {
            if (_currentPosition == -1)
            {
                if (_logger.IsTrace) _logger.Trace("No storage changes to commit");
                return;
            }

            if (_logger.IsTrace) _logger.Trace("Committing transient storage changes");

            Resettable<Change>.Reset(ref _changes, ref _capacity, ref _currentPosition, StartCapacity);
            _committedThisRound.Reset();
            _intraBlockCache.Reset();
            _originalValues.Reset();
            _transactionChangesSnapshots.Clear();
        }

        private static void ReportChanges(IStorageTracer tracer, Dictionary<StorageCell, ChangeTrace> trace)
        {
            foreach ((StorageCell address, ChangeTrace change) in trace)
            {
                byte[] before = change.Before;
                byte[] after = change.After;

                if (!Bytes.AreEqual(before, after))
                {
                    tracer.ReportStorageChange(address, before, after);
                }
            }
        }

        public void Reset()
        {
            if (_logger.IsTrace) _logger.Trace("Resetting storage");

            _intraBlockCache.Clear();
            _originalValues.Clear();
            _transactionChangesSnapshots.Clear();
            _currentPosition = -1;
            _committedThisRound.Clear();
            Array.Clear(_changes, 0, _changes.Length);
            _storages.Reset();
        }

        public void CommitTrees(long blockNumber)
        {
            // _logger.Warn($"Storage block commit {blockNumber}");
            foreach (KeyValuePair<Address, StorageTree> storage in _storages)
            {
                storage.Value.Commit(blockNumber);
            }

            // TODO: maybe I could update storage roots only now?

            // only needed here as there is no control over cached storage size otherwise
            _storages.Reset();
        }

        private byte[] GetCurrentValue(StorageCell storageCell)
        {
            if (_intraBlockCache.TryGetValue(storageCell, out StackList<int> stack))
            {
                int lastChangeIndex = stack.Peek();
                return _changes[lastChangeIndex]!.Value;
            }

            return _zeroValue;
        }


        private void PushUpdate(StorageCell cell, byte[] value)
        {
            SetupRegistry(cell);
            IncrementChangePosition();
            _intraBlockCache[cell].Push(_currentPosition);
            _changes[_currentPosition] = new Change(ChangeType.Update, cell, value);
        }

        private void IncrementChangePosition()
        {
            Resettable<Change>.IncrementPosition(ref _changes, ref _capacity, ref _currentPosition);
        }

        private void SetupRegistry(StorageCell cell)
        {
            if (!_intraBlockCache.ContainsKey(cell))
            {
                _intraBlockCache[cell] = new StackList<int>();
            }
        }

        private class Change
        {
            public Change(ChangeType changeType, StorageCell storageCell, byte[] value)
            {
                StorageCell = storageCell;
                Value = value;
                ChangeType = changeType;
            }

            public ChangeType ChangeType { get; }
            public StorageCell StorageCell { get; }
            public byte[] Value { get; }
        }

        public void ClearStorage(Address address)
        {
            /* we are setting cached values to zero so we do not use previously set values
               when the contract is revived with CREATE2 inside the same block */
            foreach (var cellByAddress in _intraBlockCache)
            {
                if (cellByAddress.Key.Address == address)
                {
                    Set(cellByAddress.Key, _zeroValue);
                }
            }
        }

        private enum ChangeType
        {
            Update,
        }
    }
}
