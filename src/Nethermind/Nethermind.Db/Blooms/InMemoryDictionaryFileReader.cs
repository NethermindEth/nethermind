// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Db.Blooms
{
    public class InMemoryDictionaryFileReader(IFileStore store) : IFileReader
    {
        private readonly IFileStore _store = store;

        public void Dispose() { }

        public int Read(long index, Span<byte> element) => _store.Read(index, element);
    }
}
