// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Db;

namespace Nethermind.Core.Buffers;

public class DbSpanMemoryManager : MemoryManager<byte>
{
    private readonly IDbWithSpan _db;
    private readonly unsafe void* _ptr;
    private readonly int _length;

    public DbSpanMemoryManager(IDbWithSpan db, Span<byte> span)
    {
        unsafe
        {
            _db = db;
            _ptr = Unsafe.AsPointer(ref span.GetPinnableReference());
            _length = span.Length;
        }
    }

    protected override void Dispose(bool disposing)
    {
        _db.DangerousReleaseMemory(GetSpan());
    }

    public override Span<byte> GetSpan()
    {
        unsafe
        {
            return new Span<byte>(_ptr, _length);
        }
    }

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        unsafe
        {
            return new MemoryHandle(_ptr);
        }
    }

    public override void Unpin()
    {
    }
}
