// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;

namespace Nethermind.Core.Buffers;

public class CappedArrayMemoryManager(CappedArray<byte>? data) : MemoryManager<byte>
{
    private readonly CappedArray<byte> _data = data ?? throw new ArgumentNullException(nameof(data));
    private bool _isDisposed;

    public override Span<byte> GetSpan()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        return _data.AsSpan();
    }

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)elementIndex, (uint)_data.Length);
        // Pinning is a no-op in this managed implementation
        return default;
    }

    public override void Unpin()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        // Unpinning is a no-op in this managed implementation
    }

    protected override void Dispose(bool disposing)
    {
        _isDisposed = true;
    }

    protected override bool TryGetArray(out ArraySegment<byte> segment)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        segment = _data.AsArraySegment();
        return true;
    }
}
