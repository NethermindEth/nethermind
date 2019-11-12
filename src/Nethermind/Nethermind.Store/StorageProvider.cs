/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Store
{
    public class StorageProvider : IStorageProvider
    {
        private ResettableDictionary<StorageAddress, Stack<int>> _intraBlockCache = new ResettableDictionary<StorageAddress, Stack<int>>();

        /// <summary>
        /// EIP-1283
        /// </summary>
        private ResettableDictionary<StorageAddress, byte[]> _originalValues = new ResettableDictionary<StorageAddress, byte[]>();

        private ResettableHashSet<StorageAddress> _committedThisRound = new ResettableHashSet<StorageAddress>();

        private readonly ILogger _logger;

        private readonly ISnapshotableDb _stateDb;
        private readonly IStateProvider _stateProvider;

        private ResettableDictionary<Address, StorageTree> _storages = new ResettableDictionary<Address, StorageTree>();

        private const int StartCapacity = Resettable.StartCapacity;
        private int _capacity = StartCapacity;
        private Change[] _changes = new Change[StartCapacity];
        private int _currentPosition = -1;

        public StorageProvider(ISnapshotableDb stateDb, IStateProvider stateProvider, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _stateDb = stateDb ?? throw new ArgumentNullException(nameof(stateDb));
            _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
        }

        public byte[] GetOriginal(StorageAddress storageAddress)
        {
            if (!_originalValues.ContainsKey(storageAddress))
            {
                throw new InvalidOperationException("Get original should only be called after get within the same caching round");
            }

            return _originalValues[storageAddress];
        }

        public byte[] Get(StorageAddress storageAddress)
        {
            return GetCurrentValue(storageAddress);
        }

        public void Set(StorageAddress storageAddress, byte[] newValue)
        {
            PushUpdate(storageAddress, newValue);
        }

        private Keccak RecalculateRootHash(Address address)
        {
            StorageTree storageTree = GetOrCreateStorage(address);
            storageTree.UpdateRootHash();
            return storageTree.RootHash;
        }

        public int TakeSnapshot()
        {
            if (_logger.IsTrace) _logger.Trace($"Storage snapshot {_currentPosition}");
            return _currentPosition;
        }

        public void Restore(int snapshot)
        {
            if (_logger.IsTrace) _logger.Trace($"Restoring storage snapshot {snapshot}");

            if (snapshot > _currentPosition)
            {
                throw new InvalidOperationException($"{nameof(StorageProvider)} tried to restore snapshot {snapshot} beyond current position {_currentPosition}");
            }

            if (snapshot == _currentPosition)
            {
                return;
            }

            List<Change> keptInCache = new List<Change>();

            for (int i = 0; i < _currentPosition - snapshot; i++)
            {
                Change change = _changes[_currentPosition - i];
                if (_intraBlockCache[change.StorageAddress].Count == 1)
                {
                    if (_changes[_intraBlockCache[change.StorageAddress].Peek()].ChangeType == ChangeType.JustCache)
                    {
                        int actualPosition = _intraBlockCache[change.StorageAddress].Pop();
                        if (actualPosition != _currentPosition - i)
                        {
                            throw new InvalidOperationException($"Expected actual position {actualPosition} to be equal to {_currentPosition} - {i}");
                        }

                        keptInCache.Add(change);
                        _changes[actualPosition] = null;
                        continue;
                    }
                }

                int forAssertion = _intraBlockCache[change.StorageAddress].Pop();
                if (forAssertion != _currentPosition - i)
                {
                    throw new InvalidOperationException($"Expected checked value {forAssertion} to be equal to {_currentPosition} - {i}");
                }

//                if (change.ChangeType == ChangeType.Destroy)
//                {
//                    _storages[change.StorageAddress.Address] = _destructedStorages[change.StorageAddress.Address].Storage;
//                    _destructedStorages.Remove(change.StorageAddress.Address);
//                }
                
                _changes[_currentPosition - i] = null;

                if (_intraBlockCache[change.StorageAddress].Count == 0)
                {
                    _intraBlockCache.Remove(change.StorageAddress);
                }
            }

            _currentPosition = snapshot;
            foreach (Change kept in keptInCache)
            {
                _currentPosition++;
                _changes[_currentPosition] = kept;
                _intraBlockCache[kept.StorageAddress].Push(_currentPosition);
            }
        }

        public void Commit()
        {
            Commit(null);
        }

        private static byte[] _zeroValue = {0}; 
        
        private struct ChangeTrace
        {
            public ChangeTrace(byte[] before, byte[] after)
            {
                After = after ?? _zeroValue;
                Before = before ?? _zeroValue;
            }
            
            public ChangeTrace(byte[] after)
            {
                After = after ?? _zeroValue;
                Before = _zeroValue;
            }
            
            public byte[] Before { get; }
            public byte[] After { get; }
        }
        
        public void Commit(IStorageTracer tracer)
        {
            if (_currentPosition == -1)
            {
                if (_logger.IsTrace) _logger.Trace("No storage changes to commit");
                return;
            }

            if (_logger.IsTrace) _logger.Trace("Committing storage changes");

            if (_changes[_currentPosition] == null)
            {
                throw new InvalidOperationException($"Change at current position {_currentPosition} was null when commiting {nameof(StorageProvider)}");
            }

            if (_changes[_currentPosition + 1] != null)
            {
                throw new InvalidOperationException($"Change after current position ({_currentPosition} + 1) was not null when commiting {nameof(StorageProvider)}");
            }

            HashSet<Address> toUpdateRoots = new HashSet<Address>();

            bool isTracing = tracer != null;
            Dictionary<StorageAddress, ChangeTrace> trace = null;
            if (isTracing)
            {
                trace = new Dictionary<StorageAddress, ChangeTrace>();
            }
            
            for (int i = 0; i <= _currentPosition; i++)
            {
                Change change = _changes[_currentPosition - i];
                if (!isTracing && change.ChangeType == ChangeType.JustCache)
                {
                    continue;
                }
                
                if (_committedThisRound.Contains(change.StorageAddress))
                {
                    if (isTracing && change.ChangeType == ChangeType.JustCache)
                    {
                        trace[change.StorageAddress] = new ChangeTrace(change.Value, trace[change.StorageAddress].After);
                    }
                    
                    continue;
                }

//                if (_destructedStorages.ContainsKey(change.StorageAddress.Address))
//                {
//                    if (_destructedStorages[change.StorageAddress.Address].ChangeIndex > _currentPosition - i)
//                    {
//                        continue;
//                    }
//                }
                
                _committedThisRound.Add(change.StorageAddress);

                if (change.ChangeType == ChangeType.Destroy)
                {
                    continue;
                }

                int forAssertion = _intraBlockCache[change.StorageAddress].Pop();
                if (forAssertion != _currentPosition - i)
                {
                    throw new InvalidOperationException($"Expected checked value {forAssertion} to be equal to {_currentPosition} - {i}");
                }

                switch (change.ChangeType)
                {
                    case ChangeType.Destroy:
                        break;
                    case ChangeType.JustCache:
                        break;
                    case ChangeType.Update:
                        if (_logger.IsTrace)
                        {
                            _logger.Trace($"  Update {change.StorageAddress.Address}_{change.StorageAddress.Index} V = {change.Value.ToHexString(true)}");
                        }

                        StorageTree tree = GetOrCreateStorage(change.StorageAddress.Address);
                        Metrics.StorageTreeWrites++;
                        toUpdateRoots.Add(change.StorageAddress.Address);
                        tree.Set(change.StorageAddress.Index, change.Value);
                        if (isTracing)
                        {
                            trace[change.StorageAddress] = new ChangeTrace(change.Value);
                        }
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            foreach (Address address in toUpdateRoots)
            {
                // since the accounts could be empty accounts that are removing (EIP-158)
                if (_stateProvider.AccountExists(address))
                {
                    Keccak root = RecalculateRootHash(address);
                    _stateProvider.UpdateStorageRoot(address, root);
                }
            }
            
            Resettable<Change>.Reset(ref _changes, ref _capacity, ref _currentPosition, StartCapacity);
            _committedThisRound.Reset();
            _intraBlockCache.Reset();
            _originalValues.Reset();
//            _destructedStorages.Clear();
            
            if (isTracing)
            {
                ReportChanges(tracer, trace);
            }
        }
        
        private void ReportChanges(IStorageTracer tracer, Dictionary<StorageAddress, ChangeTrace> trace)
        {
            foreach ((StorageAddress address, ChangeTrace change) in trace)
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
            _currentPosition = -1;
            _committedThisRound.Clear();
            Array.Clear(_changes, 0, _changes.Length);
            _storages.Reset();
//            _destructedStorages.Clear();
        }

        /// <summary>
        /// The code handling destroy is commented out. There are plenty of ethereum tests which handle collision of addresses.
        /// I would like to clarify why we even consider it a possibility?
        /// </summary>
        /// <param name="address"></param>
        public void Destroy(Address address)
        {
//            IncrementPosition();
//            _destructedStorages.Add(address, (_currentPosition, GetOrCreateStorage(address)));
//            _changes[_currentPosition] = new Change(ChangeType.Destroy, new StorageAddress(address, 0), null);
//            _storages[address] = new StorageTree(_stateDb, Keccak.EmptyTreeHash);
        }

        public void CommitTrees()
        {
            foreach (KeyValuePair<Address, StorageTree> storage in _storages)
            {
                storage.Value.Commit();
            }

            // only needed here as there is no control over cached storage size otherwise
            _storages.Reset();
        }

        private StorageTree GetOrCreateStorage(Address address)
        {
            if (!_storages.ContainsKey(address))
            {
                StorageTree storageTree = new StorageTree(_stateDb, _stateProvider.GetStorageRoot(address));
                return _storages[address] = storageTree;
            }

            return _storages[address];
        }

//        private Dictionary<Address, (int ChangeIndex, StorageTree Storage)> _destructedStorages = new Dictionary<Address, (int, StorageTree)>();

        private byte[] GetCurrentValue(StorageAddress storageAddress)
        {
            if (_intraBlockCache.ContainsKey(storageAddress))
            {
                int lastChangeIndex = _intraBlockCache[storageAddress].Peek();
//                if (_destructedStorages.ContainsKey(storageAddress.Address))
//                {
//                    if (lastChangeIndex < _destructedStorages[storageAddress.Address].ChangeIndex)
//                    {
//                        return new byte[] {0};
//                    }
//                }

                return _changes[lastChangeIndex].Value;
            }

            return LoadFromTree(storageAddress);
        }

        private byte[] LoadFromTree(StorageAddress storageAddress)
        {
            StorageTree tree = GetOrCreateStorage(storageAddress.Address);

            Metrics.StorageTreeReads++;
            byte[] value = tree.Get(storageAddress.Index);
            PushToRegistryOnly(storageAddress, value);
            return value;
        }

        private void PushToRegistryOnly(StorageAddress address, byte[] value)
        {
            SetupRegistry(address);
            IncrementChangePosition();
            _intraBlockCache[address].Push(_currentPosition);
            _originalValues[address] = value;
            _changes[_currentPosition] = new Change(ChangeType.JustCache, address, value);
        }

        private void PushUpdate(StorageAddress address, byte[] value)
        {
            SetupRegistry(address);
            IncrementChangePosition();
            _intraBlockCache[address].Push(_currentPosition);
            _changes[_currentPosition] = new Change(ChangeType.Update, address, value);
        }

        private void IncrementChangePosition()
        {
            Resettable<Change>.IncrementPosition(ref _changes, ref _capacity, ref _currentPosition);
        }

        private void SetupRegistry(StorageAddress address)
        {
            if (!_intraBlockCache.ContainsKey(address))
            {
                _intraBlockCache[address] = new Stack<int>();
            }
        }

        private class Change
        {
            public Change(ChangeType changeType, StorageAddress storageAddress, byte[] value)
            {
                StorageAddress = storageAddress;
                Value = value;
                ChangeType = changeType;
            }

            public ChangeType ChangeType { get; }
            public StorageAddress StorageAddress { get; }
            public byte[] Value { get; }
        }

        private enum ChangeType
        {
            JustCache,
            Update,
            Destroy,
        }
    }
}