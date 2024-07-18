// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;

namespace Nethermind.Trie
{
    public class NullKeyValueStore : IKeyValueStore
    {
        private NullKeyValueStore()
        {
        }

        private static NullKeyValueStore _instance;
        public static NullKeyValueStore Instance => LazyInitializer.EnsureInitialized(ref _instance, () => new NullKeyValueStore());

        public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            return null;
        }

        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            throw new NotSupportedException();
        }
    }
}
