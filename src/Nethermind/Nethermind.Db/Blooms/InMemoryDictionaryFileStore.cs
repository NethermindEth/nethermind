// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Nethermind.Db.Blooms
{
    public class InMemoryDictionaryFileStore : IFileStore
    {
        readonly IDictionary<long, byte[]> _store = new Dictionary<long, byte[]>();

        public void Dispose()
        {
            _store.Clear();
        }

        public void Write(long index, ReadOnlySpan<byte> element)
        {
            _store[index] = element.ToArray();
        }

        public int Read(long index, Span<byte> element)
        {
            if (_store.TryGetValue(index, out var found))
            {
                found.CopyTo(element);
                return found.Length;
            }

            return 0;
        }

        public IFileReader CreateFileReader() => new InMemoryDictionaryFileReader(this);
    }
}
