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

namespace Nethermind.Db
{
    public class MemDb : IFullDb, IDbWithSpan
    {
        private readonly int _writeDelay; // for testing scenarios
        private readonly int _readDelay; // for testing scenarios
        public long ReadsCount { get; private set; }
        public long WritesCount { get; private set; }

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
        }

        public string Name { get; }

        public virtual byte[]? this[ReadOnlySpan<byte> key]
        {
            get
            {
                return Get(key);
            }
            set
            {
                Set(key, value);
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

                ReadsCount += keys.Length;
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

        public virtual IBatch StartBatch()
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
            return Get(key).AsSpan();
        }

        public void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
        {
            Set(key, value.ToArray());
        }

        public void DangerousReleaseMemory(in Span<byte> span)
        {
        }

        public virtual byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            if (_readDelay > 0)
            {
                Thread.Sleep(_readDelay);
            }

            ReadsCount++;
            return _db.TryGetValue(key, out byte[] value) ? value : null;
        }

        public virtual void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            if (_writeDelay > 0)
            {
                Thread.Sleep(_writeDelay);
            }

            WritesCount++;
            _db[key] = value;
        }
    }
}
