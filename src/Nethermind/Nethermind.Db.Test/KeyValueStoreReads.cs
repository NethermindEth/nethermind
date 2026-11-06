// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Db.Test;

public static class KeyValueStoreReads
{
    /// <summary>Reads <paramref name="key"/> through the span-action overload when <paramref name="spanAction"/> is set, otherwise through <see cref="IReadOnlyKeyValueStore.Get(ReadOnlySpan{byte}, ReadFlags)"/>.</summary>
    public static byte[]? Read(IReadOnlyKeyValueStore store, ReadOnlySpan<byte> key, ReadFlags flags, bool spanAction)
    {
        if (!spanAction) return store.Get(key, flags);

        byte[]? value = null;
        store.TryGet(key, flags, static (span, result) => result[0] = span.ToArray(), new Span<byte[]?>(ref value));
        return value;
    }
}
