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
        internal readonly IDb _db;

        private const int StartCapacity = Resettable.StartCapacity;
        private int _capacity = StartCapacity;
        private Change[] _changes = new Change[StartCapacity];
        private int _currentPosition = -1;

        private ResettableDictionary<byte[], int> _pendingChanges = new ResettableDictionary<byte[], int>(Bytes.EqualityComparer, StartCapacity);
        
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
            
            Resettable<Change>.Reset(ref _changes, ref _capacity, ref _currentPosition, StartCapacity);
            _pendingChanges.Reset();
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
        ///     Note that state DB assumes that they keys are hashes of values so trying to update values may lead to unexpected
        ///     results.
        ///     If the value has already been committed to the DB then the update succeeds, otherwise it is ignored.
        /// </summary>
        private void Set(byte[] key, byte[] value)
        {
            if (_pendingChanges.ContainsKey(key)) return;

            if (value == null) throw new ArgumentNullException(nameof(value), "Cannot store null values");
            
            Resettable<Change>.IncrementPosition(ref _changes, ref _capacity, ref _currentPosition);
            Change change = new Change(key, value);
            _changes[_currentPosition] = change;
            _pendingChanges[key] = _currentPosition;
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