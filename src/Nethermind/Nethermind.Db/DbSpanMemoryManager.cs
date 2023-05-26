// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Db;

namespace Nethermind.Core.Buffers;

public unsafe sealed class DbSpanMemoryManager : MemoryManager<byte>
{
    private readonly IDbWithSpan _db;
    private void* _ptr;
    private readonly int _length;

    public DbSpanMemoryManager(IDbWithSpan db, Span<byte> unmanagedSpan)
    {
        _db = db;
        _ptr = Unsafe.AsPointer(ref MemoryMarshal.GetReference(unmanagedSpan));
        _length = unmanagedSpan.Length;
    }

    protected override void Dispose(bool disposing)
    {
        if (_ptr != null)
        {
            _db.DangerousReleaseMemory(GetSpan());
        }

        _ptr = null;
    }

    public override Span<byte> GetSpan()
    {
        if (_ptr == null && _length > 0)
        {
            ThrowDisposed();
        }

        return new Span<byte>(_ptr, _length);
    }

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        if (_ptr == null && _length > 0)
        {
            ThrowDisposed();
        }

        return new MemoryHandle(_ptr);
    }

    public override void Unpin()
    {
    }

    [DoesNotReturn]
    [StackTraceHidden]
    private static void ThrowDisposed()
    {
        throw new ObjectDisposedException(nameof(DbSpanMemoryManager));
    }
}
