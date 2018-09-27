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

        private readonly Dictionary<Keccak, Stack<int>> _cache = new Dictionary<Keccak, Stack<int>>();

        private readonly HashSet<Keccak> _committedThisRound = new HashSet<Keccak>();

        private readonly ILogger _logger = null; // TODO: inject logge here        

        private int _capacity = InitialCapacity;

        private int _currentPosition = -1;

        private Change[] _changes = new Change[InitialCapacity];

        public SnapshotableDb(IDb db)
        {
            _db = db;
        }

        private byte[] GetThroughCache(Keccak hash)
        {
            if (_cache.ContainsKey(hash))
            {
                Change change = _changes[_cache[hash].Peek()];
                return change.ChangeType == ChangeType.Delete ? null : change.Value;
            }

            byte[] value = _db[hash.Bytes];

            PushJustCache(hash, value);
            return value;
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

        private void SetupCache(Keccak hash)
        {
            if (!_cache.ContainsKey(hash))
            {
                _cache[hash] = new Stack<int>();
            }
        }

        private void PushJustCache(Keccak hash, byte[] value)
        {
            SetupCache(hash);
            IncrementPosition();
            _cache[hash].Push(_currentPosition);

            Change change = new Change();
            change.ChangeType = ChangeType.JustCache;
            change.Hash = hash;
            change.Value = value;
            _changes[_currentPosition] = change;
        }

        private void PushDelete(Keccak hash)
        {
            SetupCache(hash);
            IncrementPosition();
            _cache[hash].Push(_currentPosition);

            Change change = new Change();
            change.ChangeType = ChangeType.Delete;
            change.Hash = hash;
            _changes[_currentPosition] = change;
        }

        private void PushSet(Keccak hash, byte[] value)
        {
            SetupCache(hash);
            IncrementPosition();
            _cache[hash].Push(_currentPosition);

            Change change = new Change();
            change.ChangeType = ChangeType.Update;
            change.Hash = hash;
            change.Value = value;
            _changes[_currentPosition] = change;
        }

        public byte[] this[byte[] key]
        {
            get => GetThroughCache(new Keccak(key));
            set => PushSet(new Keccak(key), value);
        }

        // TODO: review the API
        public bool ContainsKey(byte[] key)
        {
            throw new NotImplementedException();
        }

        public void Remove(byte[] key)
        {
            PushDelete(new Keccak(key));
        }

        public void StartBatch()
        {
            _db.StartBatch();
        }

        public void CommitBatch()
        {
            _db.CommitBatch();
        }

        // TODO: implement
        public ICollection<byte[]> Keys => throw new NotImplementedException();

        // TODO: implement
        public ICollection<byte[]> Values => throw new NotImplementedException();

        private readonly List<Change> _keptInCache = new List<Change>();

        public void Restore(int snapshot)
        {
            if (snapshot > _currentPosition)
            {
                throw new InvalidOperationException($"Trying to restore snapshot beyond current positions at {nameof(SnapshotableDb)}");
            }

            _logger?.Debug($"  RESTORING DB SNAPSHOT {snapshot}");

            for (int i = 0; i < _currentPosition - snapshot; i++)
            {
                Change change = _changes[_currentPosition - i];
                if (_cache[change.Hash].Count == 1)
                {
                    if (change.ChangeType == ChangeType.JustCache)
                    {
                        int actualPosition = _cache[change.Hash].Pop();
                        if (actualPosition != _currentPosition - i)
                        {
                            throw new InvalidOperationException($"Expected actual position {actualPosition} to be equal to {_currentPosition} - {i}");
                        }

                        _keptInCache.Add(change);
                        _changes[actualPosition] = null;
                        continue;
                    }
                }

                _changes[_currentPosition - i] = null; // TODO: temp
                int forAssertion = _cache[change.Hash].Pop();
                if (forAssertion != _currentPosition - i)
                {
                    throw new InvalidOperationException($"Expected checked value {forAssertion} to be equal to {_currentPosition} - {i}");
                }

                if (_cache[change.Hash].Count == 0)
                {
                    _cache.Remove(change.Hash);
                }
            }

            _currentPosition = snapshot;
            foreach (Change kept in _keptInCache)
            {
                _currentPosition++;
                _changes[_currentPosition] = kept;
                _cache[kept.Hash].Push(_currentPosition);
            }

            _keptInCache.Clear();
        }

        public void Commit(IReleaseSpec spec)
        {
            _logger?.Debug("  COMMITTING DB CHANGES");

            if (_currentPosition == -1)
            {
                return;
            }

            if (_changes.Length <= _currentPosition + 1)
            {
                throw new InvalidOperationException($"{nameof(_currentPosition)} ({_currentPosition}) is outside of the range of {_changes} array (length {_changes.Length})");
            }

            if (_changes[_currentPosition] == null)
            {
                throw new InvalidOperationException($"Change at current position {_currentPosition} was null when commiting {nameof(SnapshotableDb)}");
            }

            if (_changes[_currentPosition + 1] != null)
            {
                throw new InvalidOperationException($"Change after current position ({_currentPosition} + 1) was not null when commiting {nameof(SnapshotableDb)}");
            }

            for (int i = 0; i <= _currentPosition; i++)
            {
                Change change = _changes[_currentPosition - i];
                if (_committedThisRound.Contains(change.Hash))
                {
                    continue;
                }

                int forAssertion = _cache[change.Hash].Pop();
                if (forAssertion != _currentPosition - i)
                {
                    throw new InvalidOperationException($"Expected checked value {forAssertion} to be equal to {_currentPosition} - {i}");
                }

                _committedThisRound.Add(change.Hash);

                switch (change.ChangeType)
                {
                    case ChangeType.JustCache:
                    {
                        break;
                    }
                    case ChangeType.Update:
                    {
                        _db[change.Hash.Bytes] = change.Value;
                        break;
                    }
                    case ChangeType.Delete:
                    {
                        _logger?.Info($"  DELETE {change.Hash}");
                        _db.Remove(change.Hash);
                        break;
                    }
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            _capacity = Math.Max(_capacity / 2, InitialCapacity);
            _changes = new Change[_capacity];
            _currentPosition = -1;
            _committedThisRound.Clear();
            _cache.Clear();
        }

        public int TakeSnapshot()
        {
            return _currentPosition;
        }

        private enum ChangeType
        {
            Delete,
            Update,
            JustCache
        }

        private class Change
        {
            public Keccak Hash { get; set; }
            public byte[] Value { get; set; }
            public ChangeType ChangeType { get; set; }
        }

        public void Dispose()
        {
            _db?.Dispose();
        }
    }
}