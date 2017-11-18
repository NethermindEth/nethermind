using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Nevermind.Core;
using Nevermind.Core.Encoding;
using Nevermind.Store;

namespace Nevermind.Evm
{
    public class StorageProvider : IStorageProvider
    {
        private const int StartCapacity = 1024;

        private readonly Dictionary<StorageAddress, Stack<int>> _cache = new Dictionary<StorageAddress, Stack<int>>();

        private readonly HashSet<StorageAddress> _committedThisRound = new HashSet<StorageAddress>();
        private readonly ILogger _logger;

        private readonly Dictionary<Address, StorageTree> _storages = new Dictionary<Address, StorageTree>();

        private int _capacity = StartCapacity;
        private Change[] _changes = new Change[StartCapacity];
        private int _currentPosition = -1;

        public StorageProvider(ILogger logger)
        {
            _logger = logger;
        }

        public byte[] Get(Address address, BigInteger index)
        {
            return GetThroughCache(new StorageAddress(address, index));
        }

        public void Set(Address address, BigInteger index, byte[] newValue)
        {
            StorageAddress storageAddress = new StorageAddress(address, index);
            PushUpdate(storageAddress, newValue);
        }

        public Keccak GetRoot(Address address)
        {
            return GetOrCreateStorage(address).RootHash;
        }

        public int TakeSnapshot()
        {
            return _currentPosition;
        }

        public void Restore(int snapshot)
        {
            {
                _logger?.Log($"  RESTORING SNAPSHOT {snapshot}");
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
                        Debug.Assert(_currentPosition - i == actualPosition);
                        keptInCache.Add(change);
                        _changes[actualPosition] = null;
                        continue;
                    }
                }

                int forAssertion = _cache[change.StorageAddress].Pop();
                Debug.Assert(forAssertion == _currentPosition - i);

                _changes[_currentPosition - i] = null;

                if (_cache[change.StorageAddress].Count == 0)
                {
                    _cache.Remove(change.StorageAddress);
                }
            }

            _currentPosition = snapshot;
            foreach (Change kept in keptInCache)
            {
                _currentPosition++;
                _changes[_currentPosition] = kept;
                _cache[kept.StorageAddress].Push(_currentPosition);
            }
        }

        public void Commit(IStateProvider stateProvider)
        {
            {
                _logger?.Log("  COMMITTING CHANGES");
            }

            if (_currentPosition == -1)
            {
                return;
            }

            Debug.Assert(_changes[_currentPosition] != null);
            Debug.Assert(_changes[_currentPosition + 1] == null);

            HashSet<Address> toUpdateRoots = new HashSet<Address>();

            for (int i = 0; i <= _currentPosition; i++)
            {
                Change change = _changes[_currentPosition - i];
                if (_committedThisRound.Contains(change.StorageAddress))
                {
                    continue;
                }

                int forAssertion = _cache[change.StorageAddress].Pop();
                Debug.Assert(forAssertion == _currentPosition - i);

                _committedThisRound.Add(change.StorageAddress);
                toUpdateRoots.Add(change.StorageAddress.Address);

                switch (change.ChangeType)
                {
                    case ChangeType.JustCache:
                        break;
                    case ChangeType.Update:

                    {
                        _logger?.Log($"  UPDATE {change.StorageAddress.Address}_{change.StorageAddress.Index} V = {Hex.FromBytes(change.Value, true)}");
                    }

                        StorageTree tree = GetOrCreateStorage(change.StorageAddress.Address);
                        tree.Set(change.StorageAddress.Index, change.Value);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            foreach (Address address in toUpdateRoots)
            {
                // TODO: this is tricky... for EIP-158
                if (stateProvider.AccountExists(address))
                {
                    Keccak root = GetRoot(address);


                    {
                        _logger?.Log($"  UPDATE {address} STORAGE ROOT = {root}");
                    }

                    stateProvider.UpdateStorageRoot(address, root);
                }
            }

            _capacity = 1024;
            _changes = new Change[_capacity];
            _currentPosition = -1;
            _committedThisRound.Clear();
            _cache.Clear();
        }

        private StorageTree GetOrCreateStorage(Address address)
        {
            if (!_storages.ContainsKey(address))
            {
                _storages[address] = new StorageTree(new InMemoryDb());
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
            StorageTree tree = GetOrCreateStorage(storageAddress.Address);
            byte[] value = tree.Get(storageAddress.Index);
            PushJustCache(storageAddress, value);
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
            if (_currentPosition > _capacity - 1)
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

        private struct StorageAddress : IEquatable<StorageAddress>
        {
            public Address Address { get; }
            public BigInteger Index { get; }

            public StorageAddress(Address address, BigInteger index)
            {
                Address = address;
                Index = index;
            }

            public bool Equals(StorageAddress other)
            {
                return Equals(Address, other.Address) && Index.Equals(other.Index);
            }

            public override bool Equals(object obj)
            {
                if (ReferenceEquals(null, obj))
                {
                    return false;
                }
                return obj is StorageAddress address && Equals(address);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return ((Address != null ? Address.GetHashCode() : 0) * 397) ^ Index.GetHashCode();
                }
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