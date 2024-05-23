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
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(CappedArrayMemoryManager));
        }

        return _data.AsSpan();
    }

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(CappedArrayMemoryManager));
        }

        if (elementIndex < 0 || elementIndex >= _data.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(elementIndex));
        }
        // Pinning is a no-op in this managed implementation
        return new MemoryHandle();
    }

    public override void Unpin()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(CappedArrayMemoryManager));
        }
        // Unpinning is a no-op in this managed implementation
    }

    protected override void Dispose(bool disposing)
    {
        _isDisposed = true;
    }

    protected override bool TryGetArray(out ArraySegment<byte> segment)
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(CappedArrayMemoryManager));
        }

        segment = new ArraySegment<byte>(_data.ToArray() ?? throw new InvalidOperationException());
        return true;
    }
}
