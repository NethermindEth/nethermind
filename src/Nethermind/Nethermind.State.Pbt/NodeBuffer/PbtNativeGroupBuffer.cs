// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Runtime.InteropServices;

namespace Nethermind.State.Pbt.NodeBuffer;

/// <summary>Native record storage, released only once no pin covers its generation.</summary>
public sealed unsafe class PbtNativeGroupBuffer : MemoryManager<byte>
{
    private const nuint HugePageBytes = 2 * 1024 * 1024;
    private const int MadvHugePage = 14;
    private const int PageBytes = 4096;

    private nint _address;
    private readonly void* _allocation;
    private readonly nuint _allocatedBytes;

    [DllImport("libc", EntryPoint = "madvise")]
    private static extern int Madvise(void* address, nuint length, int advice);

    private PbtNativeGroupBuffer(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        Length = length;
        bool useHugePages = OperatingSystem.IsLinux() && (nuint)length >= HugePageBytes;
        nuint alignment = useHugePages ? HugePageBytes : 64;
        nuint alignedLength = ((nuint)Math.Max(length, 1) + alignment - 1) & ~(alignment - 1);
        _allocatedBytes = alignedLength + alignment;
        // The allocator zeroes a large allocation by mapping fresh pages, which become resident only once written, so a
        // generation costs only what is appended to it. Clearing it here would commit every page up front.
        _allocation = NativeMemory.AllocZeroed(_allocatedBytes);
        if (_allocation == null) throw new OutOfMemoryException();

        void* data = (void*)(((nuint)_allocation + alignment - 1) & ~(alignment - 1));
        bool pressureAdded = false;
        try
        {
            GC.AddMemoryPressure((long)_allocatedBytes);
            pressureAdded = true;
            if (useHugePages) Madvise(data, alignedLength, MadvHugePage);
            _address = (nint)data;
        }
        catch
        {
            NativeMemory.Free(_allocation);
            if (pressureAdded) GC.RemoveMemoryPressure((long)_allocatedBytes);
            throw;
        }
    }

    public int Length { get; }

    public static PbtNativeGroupBuffer Allocate(int length) => new(length);

    /// <summary>Makes every page resident, so the writers that later fill it take no page faults.</summary>
    /// <remarks>Writes a zero to each page, which keeps the buffer zero-filled.</remarks>
    public void Prefault()
    {
        Span<byte> span = GetSpan();
        for (int offset = 0; offset < span.Length; offset += PageBytes) span[offset] = 0;
    }

    // Native spans do not root their owner: the pin protecting them must outlive their use.
    public override Span<byte> GetSpan() => new((void*)UnsafeAddress(), Length);

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

        NativeMemory.Free(_allocation);
        GC.RemoveMemoryPressure((long)_allocatedBytes);
    }
}
