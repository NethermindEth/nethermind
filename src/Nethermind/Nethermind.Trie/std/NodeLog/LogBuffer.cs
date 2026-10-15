// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading;

namespace Nethermind.Trie.NodeLog;

/// <summary>Native record storage, retired only after the log's readers have released their leases.</summary>
internal sealed unsafe class LogBuffer : MemoryManager<byte>
{
    private const nuint HugePageBytes = 2 * 1024 * 1024;
    private const int MadvHugePage = 14;

    private nint _address;
    private readonly nuint _allocatedBytes;

    [DllImport("libc", EntryPoint = "madvise")]
    private static extern int Madvise(void* address, nuint length, int advice);

    private LogBuffer(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        Length = length;
        bool useHugePages = OperatingSystem.IsLinux() && (nuint)length >= HugePageBytes;
        nuint alignment = useHugePages ? HugePageBytes : 64;
        _allocatedBytes = ((nuint)Math.Max(length, 1) + alignment - 1) & ~(alignment - 1);
        void* data = NativeMemory.AlignedAlloc(_allocatedBytes, alignment);
        if (data == null) throw new OutOfMemoryException();

        bool pressureAdded = false;
        try
        {
            GC.AddMemoryPressure((long)_allocatedBytes);
            pressureAdded = true;
            if (useHugePages) Madvise(data, _allocatedBytes, MadvHugePage);
            new Span<byte>(data, length).Clear();
            _address = (nint)data;
        }
        catch
        {
            NativeMemory.AlignedFree(data);
            if (pressureAdded) GC.RemoveMemoryPressure((long)_allocatedBytes);
            throw;
        }
    }

    public int Length { get; }

    internal bool IsDisposed => Volatile.Read(ref _address) == 0;

    public static LogBuffer Allocate(int length) => new(length);

    // Native spans do not root their owner: the enclosing operation lease or generation retain must outlive their use.
    public override Span<byte> GetSpan()
    {
        nint address = Volatile.Read(ref _address);
        ObjectDisposedException.ThrowIf(address == 0, this);
        return new Span<byte>((void*)address, Length);
    }

    public Span<byte> AsSpan() => GetSpan();
    public Span<byte> AsSpan(int start) => GetSpan()[start..];
    public Span<byte> AsSpan(int start, int length) => GetSpan().Slice(start, length);

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)elementIndex, (uint)Length);
        return new MemoryHandle((byte*)UnsafeAddress() + elementIndex);
    }

    private nint UnsafeAddress()
    {
        nint address = Volatile.Read(ref _address);
        ObjectDisposedException.ThrowIf(address == 0, this);
        return address;
    }

    public override void Unpin() { }

    protected override void Dispose(bool disposing)
    {
        nint address = Interlocked.Exchange(ref _address, 0);
        if (address == 0) return;

        NativeMemory.AlignedFree((void*)address);
        GC.RemoveMemoryPressure((long)_allocatedBytes);
    }
}
