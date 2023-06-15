// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Core;

namespace Nethermind.Db
{
    public class NullDb : IDb
    {
        private NullDb()
        {
        }

        private static NullDb? _instance;

        public static NullDb Instance => LazyInitializer.EnsureInitialized(ref _instance, () => new NullDb());

        public string Name { get; } = "NullDb";

        public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            return null;
        }

        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            throw new NotSupportedException();
        }

        public KeyValuePair<byte[], byte[]>[] this[byte[][] keys] => keys.Select(k => new KeyValuePair<byte[], byte[]>(k, null)).ToArray();

        public void Remove(ReadOnlySpan<byte> key)
        {
            throw new NotSupportedException();
        }

        public bool KeyExists(ReadOnlySpan<byte> key)
        {
            return false;
        }

        public long GetSize() => 0;
        public long GetCacheSize() => 0;
        public long GetIndexSize() => 0;
        public long GetMemtableSize() => 0;

        public IDb Innermost => this;
        public void Flush() { }
        public void Clear() { }

        public IEnumerable<KeyValuePair<byte[], byte[]>> GetAll(bool ordered = false) => Enumerable.Empty<KeyValuePair<byte[], byte[]>>();

        public IEnumerable<byte[]> GetAllValues(bool ordered = false) => Enumerable.Empty<byte[]>();

        public IBatch StartBatch()
        {
            throw new NotSupportedException();
        }

        public void Dispose()
        {
        }
    }
}
