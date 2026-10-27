// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.State.Pbt;

public static class PbtKeyValueStoreExtensions
{
    /// <summary>Copies the value of <paramref name="key"/> into a new array, for a caller whose contract hands out one.</summary>
    /// <returns>The value, or null when the key is missing.</returns>
    public static byte[]? ReadArray(this IReadOnlyKeyValueStore store, ReadOnlySpan<byte> key)
    {
        byte[]? value = null;
        store.TryGet(key, static (span, value) => value.Value = span.ToArray(), new PbtReadResult<byte[]?>(ref value));
        return value;
    }
}
