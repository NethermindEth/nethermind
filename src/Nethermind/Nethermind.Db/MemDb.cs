// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Db
{
    public class MemDb : IFullDb, IDbWithSpan
    {
        private readonly int _writeDelay; // for testing scenarios
        private readonly int _readDelay; // for testing scenarios
        private long _writeCount;
        private long _readCount;

        public long ReadsCount { get => _readCount; }
        public long WritesCount { get => _writeCount; }

        private ILogger logger = new TestLogManager.NUnitLogger(LogLevel.Info);

        private readonly SpanConcurrentDictionary<byte, byte[]?> _db;

        public MemDb(string name)
            : this(0, 0)
        {
            Name = name;
        }

        public MemDb() : this(0, 0)
        {
        }

        public MemDb(int writeDelay, int readDelay)
        {
            _writeDelay = writeDelay;
            _readDelay = readDelay;
            _db = new SpanConcurrentDictionary<byte, byte[]>(Bytes.SpanEqualityComparer);
            _writeCount = 0;
            _readCount = 0;
        }

        public string Name { get; }

        public virtual byte[]? this[ReadOnlySpan<byte> key]
        {
            get
            {
                if (_readDelay > 0)
                {
                    Thread.Sleep(_readDelay);
                }

                Interlocked.Increment(ref _readCount);
                return _db.TryGetValue(key, out byte[] value) ? value : null;
            }
            set
            {
                if (_writeDelay > 0)
                {
                    Thread.Sleep(_writeDelay);
                }

                Interlocked.Increment(ref _writeCount);
                _db[key] = value;
            }
        }

        public KeyValuePair<byte[], byte[]>[] this[byte[][] keys]
        {
            get
            {
                if (_readDelay > 0)
                {
                    Thread.Sleep(_readDelay);
                }

                Interlocked.Add(ref _readCount, keys.Length);
                return keys.Select(k => new KeyValuePair<byte[], byte[]>(k, _db.TryGetValue(k, out var value) ? value : null)).ToArray();
            }
        }

        public virtual void Remove(ReadOnlySpan<byte> key)
        {
            _db.TryRemove(key, out _);
        }

        public bool KeyExists(ReadOnlySpan<byte> key)
        {
            return _db.ContainsKey(key);
        }

        public IDb Innermost => this;

        public void Flush()
        {
        }

        public void Clear()
        {
            _db.Clear();
        }

        public IEnumerable<KeyValuePair<byte[], byte[]?>> GetAll(bool ordered = false) => _db;

        public IEnumerable<byte[]> GetAllValues(bool ordered = false) => Values;

        public IBatch StartBatch()
        {
            return this.LikeABatch();
        }

        public ICollection<byte[]> Keys => _db.Keys;
        public ICollection<byte[]> Values => _db.Values;

        public int Count => _db.Count;

        public void Dispose()
        {
        }

        public virtual Span<byte> GetSpan(ReadOnlySpan<byte> key)
        {
            return this[key].AsSpan();
        }

        public void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
        {
            this[key] = value.ToArray();
        }

        public void DangerousReleaseMemory(in Span<byte> span)
        {
        }

        public void DeleteByRange(Span<byte> startKey, Span<byte> endKey)
        {
            List<byte[]> keys = new();
            foreach (byte[] key in _db.Keys)
            {
                if (Bytes.Comparer.Compare(key, startKey) >= 0 && Bytes.Comparer.Compare(key, endKey) < 0)
                    keys.Add(key);
            }
            foreach (byte[] key in keys)
            {
                _db.Remove(key, out _);
            }
        }
    }
}
