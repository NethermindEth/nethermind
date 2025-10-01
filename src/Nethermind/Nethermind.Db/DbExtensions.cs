// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;

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
            var k = keys.Select(static k => k.Bytes.ToArray()).ToArray();
            return db[k];
        }

        /// <summary>
        /// Wraps regular <see cref="IWriteBatch"/> requiring explicit call to <see cref="IWriteBatch.Commit"/> for data submission.
        /// </summary>
        public static IWriteBatch RequireCommit(this IWriteBatch batch)
        {
            return new RequireCommitWriteBatch(batch);
        }
    }
}
