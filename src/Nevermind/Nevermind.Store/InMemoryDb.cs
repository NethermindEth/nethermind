using System;
using System.Collections.Generic;
using System.Diagnostics;
using Nevermind.Core;
using Nevermind.Core.Crypto;
using Nevermind.Core.Encoding;

namespace Nevermind.Store
{
    public class InMemoryDb : IDb
    {
        private const int InitialCapacity = 1024;

        private readonly Dictionary<Keccak, byte[]> _db;

        private readonly Dictionary<Keccak, Stack<int>> _cache = new Dictionary<Keccak, Stack<int>>();

        private readonly HashSet<Keccak> _committedThisRound = new HashSet<Keccak>();

        private readonly ILogger _logger = null; // TODO: inject logge here        

        private int _capacity = InitialCapacity;

        private int _currentPosition = -1;

        private Change[] _changes = new Change[InitialCapacity];

        private InMemoryDb(Dictionary<Keccak, byte[]> toCopy)
        {
            _db = new Dictionary<Keccak, byte[]>(toCopy);
        }

        public InMemoryDb()
        {
            _db = new Dictionary<Keccak, byte[]>(4);
        }

        private byte[] GetThroughCache(Keccak hash)
        {
            if (_cache.ContainsKey(hash))
            {
                Change change = _changes[_cache[hash].Peek()];
                return change.ChangeType == ChangeType.Delete ? null : change.Value;
            }

            byte[] value = _db.ContainsKey(hash) ? _db[hash] : null;
            PushJustCache(hash, value);
            return value;
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

        public byte[] this[Keccak key]
        {
            get => GetThroughCache(key);
            set => PushSet(key, value);
        }

        public void Delete(Keccak key)
        {
            PushDelete(key);
        }

        private readonly List<Change> _keptInCache = new List<Change>();

        public void Restore(int snapshot)
        {
            Debug.Assert(snapshot <= _currentPosition, "INVALID DB SNAPSHOT");
            _logger?.Log($"  RESTORING DB SNAPSHOT {snapshot}");

            for (int i = 0; i < _currentPosition - snapshot; i++)
            {
                Change change = _changes[_currentPosition - i];
                if (_cache[change.Hash].Count == 1)
                {
                    if (change.ChangeType == ChangeType.JustCache)
                    {
                        int actualPosition = _cache[change.Hash].Pop();
                        Debug.Assert(_currentPosition - i == actualPosition);
                        _keptInCache.Add(change);
                        _changes[actualPosition] = null;
                        continue;
                    }
                }

                _changes[_currentPosition - i] = null; // TODO: temp
                int forAssertion = _cache[change.Hash].Pop();
                Debug.Assert(forAssertion == _currentPosition - i);

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

        public void Commit()
        {
            _logger?.Log("  COMMITTING DB CHANGES");

            if (_currentPosition == -1)
            {
                return;
            }

            Debug.Assert(_changes[_currentPosition] != null);
            Debug.Assert(_changes[_currentPosition + 1] == null);

            for (int i = 0; i <= _currentPosition; i++)
            {
                Change change = _changes[_currentPosition - i];
                if (_committedThisRound.Contains(change.Hash))
                {
                    continue;
                }

                int forAssertion = _cache[change.Hash].Pop();
                Debug.Assert(forAssertion == _currentPosition - i);

                _committedThisRound.Add(change.Hash);

                switch (change.ChangeType)
                {
                    case ChangeType.JustCache:
                    {
                        break;
                    }
                    case ChangeType.Update:
                    {
                        _db[change.Hash] = change.Value;
                        break;
                    }
                    case ChangeType.Delete:
                    {
                        _logger?.Log($"  DELETE {change.Hash}");
                        if (_db.ContainsKey(change.Hash))
                        {
                            _db.Remove(change.Hash);
                        }
                        break;
                    }
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            _capacity = 1024;
            _changes = new Change[_capacity];
            _currentPosition = -1;
            _committedThisRound.Clear();
            _cache.Clear();
        }


        public void Print(Action<string> output)
        {
            foreach (KeyValuePair<Keccak, byte[]> keyValuePair in _db)
            {
                Node node = PatriciaTree.RlpDecode(new Rlp(keyValuePair.Value));
                output($"{keyValuePair.Key.ToString(true).Substring(0, 6)} : {node}");
            }
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
    }
}