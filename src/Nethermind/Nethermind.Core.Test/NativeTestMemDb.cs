// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Test;

/// <summary>A <see cref="TestMemDb"/> that also lends native slices, as RocksDB does, over its stored arrays.</summary>
/// <remarks>Reads through the slice are recorded like <see cref="TestMemDb.Get"/> reads, flags included.</remarks>
public sealed class NativeTestMemDb : TestMemDb, IReadOnlyNativeKeyValueStore
{
    public ReadOnlySpan<byte> GetNativeSlice(scoped ReadOnlySpan<byte> key, out nint handle, ReadFlags flags = ReadFlags.None)
    {
        handle = 0;
        return Get(key, flags);
    }

    public void DangerousReleaseHandle(nint handle) { }
}
