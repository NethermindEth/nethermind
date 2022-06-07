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
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Resettables;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.State
{
    public abstract class PartialStorageProviderBase : IPartialStorageProvider
    {
        protected readonly ResettableDictionary<StorageCell, StackList<int>> _intraBlockCache = new();

        /// <summary>
        /// EIP-1283
        /// </summary>
        protected readonly ResettableDictionary<StorageCell, byte[]> _originalValues = new();

        protected readonly ResettableHashSet<StorageCell> _committedThisRound = new();

        protected readonly ILogger _logger;
        protected readonly ILogManager _logManager;

        protected readonly ResettableDictionary<Address, StorageTree> _storages = new();

        protected const int StartCapacity = Resettable.StartCapacity;
        protected int _capacity = StartCapacity;
        protected Change?[] _changes = new Change[StartCapacity];
        protected int _currentPosition = Resettable.EmptyPosition;

        // stack of snapshot indexes on changes for start of each transaction
        // this is needed for OriginalValues for new transactions
        protected readonly Stack<int> _transactionChangesSnapshots = new();

        public PartialStorageProviderBase(ILogManager? logManager)
        {
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _logger = logManager.GetClassLogger<PartialStorageProviderBase>() ?? throw new ArgumentNullException(nameof(logManager));
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

        public void Restore(int snapshot)
        {
            if (_logger.IsTrace) _logger.Trace($"Restoring storage snapshot {snapshot}");

            if (snapshot > _currentPosition)
            {
                throw new InvalidOperationException($"{nameof(PartialStorageProviderBase)} tried to restore snapshot {snapshot} beyond current position {_currentPosition}");
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

        public void Commit()
        {
            Commit(NullStorageTracer.Instance);
        }

        protected static readonly byte[] _zeroValue = {0};

        protected readonly struct ChangeTrace
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

        public abstract void Commit(IStorageTracer tracer);

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

        public abstract void CommitTrees(long blockNumber);

        protected abstract byte[] GetCurrentValue(StorageCell storageCell);

        private void PushUpdate(StorageCell cell, byte[] value)
        {
            SetupRegistry(cell);
            IncrementChangePosition();
            _intraBlockCache[cell].Push(_currentPosition);
            _changes[_currentPosition] = new Change(ChangeType.Update, cell, value);
        }

        protected void IncrementChangePosition()
        {
            Resettable<Change>.IncrementPosition(ref _changes, ref _capacity, ref _currentPosition);
        }

        protected void SetupRegistry(StorageCell cell)
        {
            if (!_intraBlockCache.ContainsKey(cell))
            {
                _intraBlockCache[cell] = new StackList<int>();
            }
        }

        public abstract void ClearStorage(Address address);

        protected class Change
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

        protected enum ChangeType
        {
            JustCache,
            Update,
            Destroy,
        }
    }
}
