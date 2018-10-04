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
using Nethermind.Core.Logging;
using Nethermind.Core.Specs;

namespace Nethermind.Store
{
    public class SnapshotableDb : ISnapshotableDb
    {
        private readonly IDb _db;

        private const int InitialCapacity = 4;

        private readonly LruCache<Keccak, byte[]> _cache = new LruCache<Keccak, byte[]>(1024 * 1024 * 2);        

        private int _capacity = InitialCapacity;

        private int _currentPosition = -1;

        private Change[] _changes = new Change[InitialCapacity];

        public SnapshotableDb(IDb db)
        {
            _db = db;
        }

        private byte[] GetThroughCache(Keccak hash)
        {
            byte[] value = _cache.Get(hash);
            if (value != null)
            {
                return value;
            }

            value = _db[hash.Bytes];
            _cache.Set(hash, value);

            return value;
        }

        private void PushSet(Keccak hash, byte[] value)
        {
            _currentPosition++;
            AdjustSize();

            Change change = new Change(hash, value);
            _changes[_currentPosition] = change;
            _cache.Set(hash, value);
        }

        public byte[] this[byte[] key]
        {
            get => GetThroughCache(new Keccak(key));
            set => PushSet(new Keccak(key), value);
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
            if (snapshot > _currentPosition)
            {
                throw new InvalidOperationException($"Trying to restore snapshot beyond current positions at {nameof(SnapshotableDb)}");
            }

            for (int i = _currentPosition; i > snapshot; i--)
            {
                Change change = _changes[i];
                _cache.Delete(change.Hash);
            }
            
            _currentPosition = snapshot;
        }

        public void Commit()
        {
            if (_currentPosition == -1)
            {
                return;
            }

            for (int i = 0; i <= _currentPosition; i++)
            {
                Change change = _changes[_currentPosition - i];
                _db[change.Hash.Bytes] = change.Value;
            }

            _currentPosition = -1;
            AdjustSize();
        }

        private void AdjustSize()
        {
            if (_currentPosition > _capacity - 1)
            {
                _capacity *= 2;
                Array.Resize(ref _changes, _capacity);
            }
            else if((_currentPosition + 1) < _capacity / 4)
            {
                _capacity = Math.Max(_capacity / 2, InitialCapacity);
                Array.Resize(ref _changes, _capacity);
            }
        }

        public int TakeSnapshot()
        {
            return _currentPosition;
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

        public void Dispose()
        {
            _db?.Dispose();
        }
    }
}