// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core
{
    public static class KeyValueStoreExtensions
    {
        public static IWriteBatch LikeABatch(this IKeyValueStoreWithBatching keyValueStore)
        {
            return LikeABatch(keyValueStore, null);
        }

        public static IWriteBatch LikeABatch(this IKeyValueStoreWithBatching keyValueStore, Action? onDispose)
        {
            return new FakeWriteBatch(keyValueStore, onDispose);
        }
    }
}
