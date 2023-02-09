// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Db
{
    public interface IDb : IKeyValueStoreWithBatching, IDisposable
    {
        string Name { get; }
        KeyValuePair<byte[], byte[]?>[] this[byte[][] keys] { get; }
        IEnumerable<KeyValuePair<byte[], byte[]>> GetAll(bool ordered = false);
        IEnumerable<byte[]> GetAllValues(bool ordered = false);
        void Remove(byte[] key);
        bool KeyExists(byte[] key);

        void Flush();

        void Clear();

        public IReadOnlyDb CreateReadOnly(bool createInMemWriteStore) => new ReadOnlyDb(this, createInMemWriteStore);
    }
}
