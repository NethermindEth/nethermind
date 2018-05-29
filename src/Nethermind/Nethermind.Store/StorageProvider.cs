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
using Nethermind.Core.Specs;

namespace Nethermind.Store
{
    public class StorageProvider : IStorageProvider
    {
        internal const int StartCapacity = 16;

        private readonly Dictionary<StorageAddress, Stack<int>> _cache = new Dictionary<StorageAddress, Stack<int>>();

        private readonly HashSet<StorageAddress> _committedThisRound = new HashSet<StorageAddress>();
        
        private readonly IDbProvider _dbProvider;

        private readonly ILogger _logger;
        
        private readonly IStateProvider _stateProvider;

        private readonly LruCache<StorageAddress, byte[]> _storageCache = new LruCache<StorageAddress, byte[]>(1024 * 32 * 10); // ~100MB

        private readonly Dictionary<Address, StorageTree> _storages = new Dictionary<Address, StorageTree>();

        private int _capacity = StartCapacity;
        private Change[] _changes = new Change[StartCapacity];
        private int _currentPosition = -1;

        public StorageProvider(IDbProvider dbProvider, IStateProvider stateProvider, ILogger logger)
        {
            _dbProvider = dbProvider;
            _stateProvider = stateProvider;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public byte[] Get(StorageAddress storageAddress)
        {
            return GetThroughCache(storageAddress);
        }

        public void Set(StorageAddress storageAddress, byte[] newValue)
        {
            PushUpdate(storageAddress, newValue);
        }

        public Keccak GetRoot(Address address)
        {
            StorageTree storageTree = GetOrCreateStorage(address);
            storageTree.UpdateRootHash();
            return storageTree.RootHash;
        }

        public int TakeSnapshot()
        {
            if (_logger.IsDebugEnabled)
            {
                _logger.Debug($"  STORAGE SNAPSHOT {_currentPosition}");
            }

            return _currentPosition;
        }

        public void Restore(int snapshot)
        {
            if (_logger.IsDebugEnabled)
            {
                _logger.Debug($"  RESTORING STORAGE SNAPSHOT {snapshot}");
            }

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
                if (_cache[change.StorageAddress].Count == 1)
                {
                    if (_changes[_cache[change.StorageAddress].Peek()].ChangeType == ChangeType.JustCache)
                    {
                        int actualPosition = _cache[change.StorageAddress].Pop();
                        if (actualPosition != _currentPosition - i)
                        {
                            throw new InvalidOperationException($"Expected actual position {actualPosition} to be equal to {_currentPosition} - {i}");
                        }

                        keptInCache.Add(change);
                        _changes[actualPosition] = null;
                        continue;
                    }
                }

                int forAssertion = _cache[change.StorageAddress].Pop();
                if (forAssertion != _currentPosition - i)
                {
                    throw new InvalidOperationException($"Expected checked value {forAssertion} to be equal to {_currentPosition} - {i}");
                }

                _changes[_currentPosition - i] = null;

                if (_cache[change.StorageAddress].Count == 0)
                {
                    _cache.Remove(change.StorageAddress);
                    _storageCache.Set(change.StorageAddress, null);
                }
            }

            _currentPosition = snapshot;
            foreach (Change kept in keptInCache)
            {
                _currentPosition++;
                _changes[_currentPosition] = kept;
                _storageCache.Set(kept.StorageAddress, kept.Value);
                _cache[kept.StorageAddress].Push(_currentPosition);
            }
        }

        public void Commit(IReleaseSpec spec)
        {
            if (_currentPosition == -1)
            {
                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug("  NO STORAGE CHANGES TO COMMIT");
                }

                return;
            }

            if (_logger.IsDebugEnabled)
            {
                _logger.Debug("  COMMITTING STORAGE CHANGES");
            }

            if (_changes[_currentPosition] == null)
            {
                throw new InvalidOperationException($"Change at current position {_currentPosition} was null when commiting {nameof(StorageProvider)}");
            }

            if (_changes[_currentPosition + 1] != null)
            {
                throw new InvalidOperationException($"Change after current position ({_currentPosition} + 1) was not null when commiting {nameof(StorageProvider)}");
            }

            HashSet<Address> toUpdateRoots = new HashSet<Address>();

            for (int i = 0; i <= _currentPosition; i++)
            {
                Change change = _changes[_currentPosition - i];
                if (_committedThisRound.Contains(change.StorageAddress))
                {
                    continue;
                }

                int forAssertion = _cache[change.StorageAddress].Pop();
                if (forAssertion != _currentPosition - i)
                {
                    throw new InvalidOperationException($"Expected checked value {forAssertion} to be equal to {_currentPosition} - {i}");
                }

                _committedThisRound.Add(change.StorageAddress);
                toUpdateRoots.Add(change.StorageAddress.Address);

                switch (change.ChangeType)
                {
                    case ChangeType.JustCache:
                        break;
                    case ChangeType.Update:

                        if (_logger.IsDebugEnabled)
                        {
                            _logger.Debug($"  UPDATE {change.StorageAddress.Address}_{change.StorageAddress.Index} V = {Hex.FromBytes(change.Value, true)}");
                        }

                        StorageTree tree = GetOrCreateStorage(change.StorageAddress.Address);
                        Metrics.StorageTreeWrites++;
                        tree.Set(change.StorageAddress.Index, change.Value);
                        _storageCache.Set(change.StorageAddress, change.Value);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            foreach (Address address in toUpdateRoots)
            {
                // TODO: this is tricky... for EIP-158
                if (_stateProvider.AccountExists(address))
                {
                    Keccak root = GetRoot(address);
                    _stateProvider.UpdateStorageRoot(address, root);
                }
            }

            _capacity = Math.Max(StartCapacity, _capacity / 2);
            _changes = new Change[_capacity];
            _currentPosition = -1;
            _committedThisRound.Clear();
            _cache.Clear();
        }

        public void ClearCaches()
        {
            if (_logger.IsDebugEnabled)
            {
                _logger.Debug("  CLEARING STORAGE PROVIDER CACHES");
            }

            _cache.Clear();
            _currentPosition = -1;
            _committedThisRound.Clear();
            Array.Clear(_changes, 0, _changes.Length);
            _storages.Clear();
        }

        public void CommitTrees()
        {
            foreach (KeyValuePair<Address, StorageTree> storage in _storages)
            {
                storage.Value.Commit();
            }
        }

        private StorageTree GetOrCreateStorage(Address address)
        {
            if (!_storages.ContainsKey(address))
            {
                _storages[address] = new StorageTree(_dbProvider.GetOrCreateStateDb(), _stateProvider.GetStorageRoot(address));
            }

            return GetStorage(address);
        }

        private StorageTree GetStorage(Address address)
        {
            return _storages[address];
        }

        private byte[] GetThroughCache(StorageAddress storageAddress)
        {
            if (_cache.ContainsKey(storageAddress))
            {
                return _changes[_cache[storageAddress].Peek()].Value;
            }

            return GetAndAddToCache(storageAddress);
        }

        private byte[] GetAndAddToCache(StorageAddress storageAddress)
        {
            byte[] cached = _storageCache.Get(storageAddress);
            if (cached != null)
            {
                return cached;
            }

            StorageTree tree = GetOrCreateStorage(storageAddress.Address);

            Metrics.StorageTreeReads++;
            byte[] value = tree.Get(storageAddress.Index);
            PushJustCache(storageAddress, value);
            _storageCache.Set(storageAddress, value);
            return value;
        }

        private void PushJustCache(StorageAddress address, byte[] value)
        {
            SetupCache(address);
            IncrementPosition();
            _cache[address].Push(_currentPosition);
            _changes[_currentPosition] = new Change(ChangeType.JustCache, address, value);
        }

        private void PushUpdate(StorageAddress address, byte[] value)
        {
            SetupCache(address);
            IncrementPosition();
            _cache[address].Push(_currentPosition);
            _changes[_currentPosition] = new Change(ChangeType.Update, address, value);
        }

        private void IncrementPosition()
        {
            _currentPosition++;
            if (_currentPosition >= _capacity - 1) // sometimes we ask about the _currentPosition + 1;
            {
                _capacity *= 2;
                Array.Resize(ref _changes, _capacity);
            }
        }

        private void SetupCache(StorageAddress address)
        {
            if (!_cache.ContainsKey(address))
            {
                _cache[address] = new Stack<int>();
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
            Update
        }
    }
}