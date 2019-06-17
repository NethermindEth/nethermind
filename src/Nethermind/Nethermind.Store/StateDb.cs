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
using Nethermind.Core.Extensions;

namespace Nethermind.Store
{
    /// <summary>
    ///     State DB where keys are hashes of values and only inserts are allowed.
    /// </summary>
    public class StateDb : ISnapshotableDb
    {
        private const int InitialCapacity = 4;

        internal readonly IDb _db;

        private int _capacity = InitialCapacity;

        private Change[] _changes = new Change[InitialCapacity];

        private int _currentPosition = -1;
        private Dictionary<byte[], int> _pendingChanges = new Dictionary<byte[], int>(InitialCapacity, Bytes.EqualityComparer);
        
        public string Name { get; } = "State";

        public StateDb()
            :this(new MemDb())
        {
        }
        
        public StateDb(IDb db)
        {
            _db = db;
        }

        public byte[] this[byte[] key]
        {
            get => Get(key);
            set => Set(key, value);
        }

        public byte[][] GetAll() => _db.GetAll();

        public void StartBatch()
        {
            _db.StartBatch();
        }

        public void CommitBatch()
        {
            _db.CommitBatch();
        }

        public void Remove(byte[] key)
        {
        }

        public bool KeyExists(byte[] key)
        {
            return _pendingChanges.ContainsKey(key) || _db.KeyExists(key);
        }

        public void Restore(int snapshot)
        {
            if (snapshot > _currentPosition) throw new InvalidOperationException($"Trying to restore snapshot beyond current positions at {nameof(StateDb)}");

            for (int i = _currentPosition; i > snapshot; i--)
            {
                Change change = _changes[i];
                _pendingChanges.Remove(change.Key);
            }

            _currentPosition = snapshot;
        }

        public void Commit()
        {
            if (_currentPosition == -1) return;

            _db.StartBatch();
            for (int i = 0; i <= _currentPosition; i++)
            {
                Change change = _changes[_currentPosition - i];
                _db[change.Key] = change.Value;
            }
            
            _db.CommitBatch();

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

        private byte[] Get(byte[] key)
        {
            if (_pendingChanges.TryGetValue(key, out int pendingChangeIndex)) return _changes[pendingChangeIndex].Value;
            return _db[key];
        }

        /// <summary>
        ///     Note that state DB assumes that they keys are hashes of values so trying to update values may to lead unexpected
        ///     results.
        ///     If the value has already been committed to the DB then the update succeeds, otherwise it is ignored.
        /// </summary>
        private void Set(byte[] key, byte[] value)
        {
            if (_pendingChanges.ContainsKey(key)) return;

            if (value == null) throw new ArgumentNullException(nameof(value), "Cannot store null values");

            _currentPosition++;
            AdjustSize();

            Change change = new Change(key, value);
            _changes[_currentPosition] = change;
            _pendingChanges[key] = _currentPosition;
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
            public Change(byte[] key, byte[] value)
            {
                Key = key;
                Value = value;
            }

            public byte[] Key { get; }
            public byte[] Value { get; }
        }
    }
}