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

namespace Nethermind.Store
{
    public class SnapshotableDb : ISnapshotableDb
    {
        private const int InitialCapacity = 4;

        private readonly LruCache<Keccak, byte[]> _cache;
        private readonly bool _cachingEnabled;
        private readonly IDb _db;

        private int _capacity = InitialCapacity;

        private Change[] _changes = new Change[InitialCapacity];

        private int _currentPosition = -1;
        private Dictionary<Keccak, int> _pendingChanges = new Dictionary<Keccak, int>(InitialCapacity);

        public SnapshotableDb(IDb db, bool cachingEnabled = false, int cacheSize = 1024 * 1024 * 2)
        {
            _db = db;
            _cachingEnabled = cachingEnabled;
            if (_cachingEnabled) _cache = new LruCache<Keccak, byte[]>(cacheSize);
        }

        public byte[] this[byte[] key]
        {
            get => Get(new Keccak(key));
            set => Set(new Keccak(key), value);
        }

        public void StartBatch()
        {
            _db.StartBatch();
        }

        public void CommitBatch()
        {
            _db.CommitBatch();
        }

        public void Restore(int snapshot)
        {
            if (snapshot > _currentPosition) throw new InvalidOperationException($"Trying to restore snapshot beyond current positions at {nameof(SnapshotableDb)}");

            for (int i = _currentPosition; i > snapshot; i--)
            {
                Change change = _changes[i];
                _pendingChanges.Remove(change.Hash);
            }

            _currentPosition = snapshot;
        }

        public void Commit()
        {
            if (_currentPosition == -1) return;

            for (int i = 0; i <= _currentPosition; i++)
            {
                Change change = _changes[_currentPosition - i];
                _db[change.Hash.Bytes] = change.Value;
                if (_cachingEnabled) _cache.Set(change.Hash, change.Value);
            }

            _currentPosition = -1;
            AdjustSize();
            _pendingChanges.Clear();
//            _pendingChanges = new Dictionary<Keccak, int>(_capacity);
        }

        public int TakeSnapshot()
        {
            return _currentPosition;
        }

        public void Dispose()
        {
            _db?.Dispose();
        }

        private byte[] Get(Keccak hash)
        {
            if (_pendingChanges.TryGetValue(hash, out int pendingCHangeIndex)) return _changes[pendingCHangeIndex].Value;

            if (_cachingEnabled)
            {
                var value = _cache.Get(hash);
                if (value != null) return value;

                value = _db[hash.Bytes];
                _cache.Set(hash, value);

                return value;
            }

            return _db[hash.Bytes];
        }

        private void Set(Keccak hash, byte[] value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value), "Cannot store null values");

            _currentPosition++;
            AdjustSize();

            Change change = new Change(hash, value);
            _changes[_currentPosition] = change;
            _pendingChanges[hash] = _currentPosition;
        }

        private void AdjustSize()
        {
            if (_currentPosition > _capacity - 1)
            {
                _capacity *= 2;
                Array.Resize(ref _changes, _capacity);
            }
            else if (_currentPosition + 1 < _capacity / 4)
            {
                _capacity = Math.Max(_capacity / 2, InitialCapacity);
                Array.Resize(ref _changes, _capacity);
            }
        }

        private struct Change
        {
            public Change(Keccak hash, byte[] value)
            {
                Hash = hash;
                Value = value;
            }

            public Keccak Hash { get; }
            public byte[] Value { get; }
        }
    }
}