// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>Stores immutable parent-state storage values by declared-read ordinal without eviction.</summary>
/// <remarks>
/// Concurrent writers must resolve against the same parent root and publish identical values.
/// Neither writers nor readers may mutate the value arrays. Dispose only after all readers and writers finish.
/// </remarks>
public sealed class BalStorageValueCache(int count) : IDisposable
{
    private byte[]?[] _values = new byte[count][];

    /// <summary>Publishes a value; null and empty arrays both represent a known-zero slot.</summary>
    public void Set(int ordinal, byte[]? value) => Volatile.Write(ref _values[ordinal], value ?? []);

    /// <summary>Returns a published value, including an empty array for zero; false means not loaded.</summary>
    public bool TryGet(int ordinal, out byte[]? value)
    {
        value = Volatile.Read(ref _values[ordinal]);
        return value is not null;
    }

    /// <inheritdoc/>
    public void Dispose() => _values = [];
}
