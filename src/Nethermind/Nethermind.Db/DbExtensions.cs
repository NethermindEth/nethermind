// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Db
{
    public static class DbExtensions
    {
        public static ReadOnlyDb AsReadOnly(this IDb db, bool createInMemoryWriteStore)
        {
            return new(db, createInMemoryWriteStore);
        }

        public static KeyValuePair<byte[], byte[]>[] MultiGet(this IDb db, IEnumerable<ValueHash256> keys)
        {
            var k = keys.Select(k => k.Bytes.ToArray()).ToArray();
            return db[k];
        }

        public static void Set(this IDb db, long key, byte[] value)
        {
            db[key.ToBigEndianByteArrayWithoutLeadingZeros()] = value;
        }

        public static byte[]? Get(this IDb db, long key) => db[key.ToBigEndianByteArrayWithoutLeadingZeros()];

    }
}
