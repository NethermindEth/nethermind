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

        public byte[]? this[byte[] key]
        {
            get => null;
            set => throw new NotSupportedException();
        }

        public KeyValuePair<byte[], byte[]>[] this[byte[][] keys] => keys.Select(k => new KeyValuePair<byte[], byte[]>(k, null)).ToArray();

        public void Remove(byte[] key)
        {
            throw new NotSupportedException();
        }

        public bool KeyExists(byte[] key)
        {
            return false;
        }

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
