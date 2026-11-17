// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nethermind.Core.Test;
using Nethermind.Core;

namespace Nethermind.Blockchain.Test.Blocks;

/// <summary>A memory database that serves native slices from pinned copies and fills a copy with 0xff when it is released.</summary>
internal sealed class PinnedTestDb : TestMemDb, IReadOnlyNativeKeyValueStore, IDisposable
{
    private readonly Dictionary<nint, byte[]> _pins = [];
    public int NativeReads { get; private set; }
    public int Released { get; private set; }
    public int LiveHandles => _pins.Count;

    public ReadOnlySpan<byte> GetNativeSlice(scoped ReadOnlySpan<byte> key, out nint handle, ReadFlags flags = ReadFlags.None)
    {
        NativeReads++;
        handle = 0;
        byte[]? value = Get(key, flags);
        if (value is null) return null;
        byte[] bytes = (byte[])value.Clone();
        GCHandle pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        handle = GCHandle.ToIntPtr(pin);
        _pins.Add(handle, bytes);
        return bytes;
    }

    public void DangerousReleaseHandle(nint handle)
    {
        if (!_pins.Remove(handle, out byte[]? bytes)) throw new InvalidOperationException("Native handle released twice.");
        bytes.AsSpan().Fill(0xff);
        GCHandle.FromIntPtr(handle).Free();
        Released++;
    }

    public new void Dispose()
    {
        foreach (nint handle in _pins.Keys) GCHandle.FromIntPtr(handle).Free();
        _pins.Clear();
        base.Dispose();
    }
}
