// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Paprika.Store;

namespace Paprika.Tests.Store;

/// <summary>
/// Tests ensuring that unmanaged structs do no leak out beyond their sizes.
/// </summary>
[Parallelizable(ParallelScope.Self)]
public class MemoryFencingTests
{
    private const int FenceSize = 32;

    private readonly Stack<nuint> _alignedAllocations = [];

    [Test]
    public void Int_for_sanity() => Assert.That(Alloc<int>(sizeof(int)), Is.EqualTo(0));

    [Test]
    public void AbandonedPage()
    {
        ref AbandonedList list = ref Alloc<AbandonedList>(AbandonedList.Size);
        Assert.That(list.IsFullyEmpty, Is.True);
    }

    [TearDown]
    public unsafe void TearDown()
    {
        while (_alignedAllocations.TryPop(out nuint alloc))
        {
            NativeMemory.AlignedFree((void*)alloc);
        }
    }

    private unsafe ref T Alloc<T>(int size)
        where T : struct
    {
        nuint sizeTotal = (nuint)size + FenceSize * 2;
        byte* memory = (byte*)NativeMemory.AlignedAlloc(sizeTotal, sizeof(int));
        NativeMemory.Clear(memory, sizeTotal);

        _alignedAllocations.Push((nuint)memory);

        byte* actualStart = memory + FenceSize;

        Fence(memory);
        Fence(actualStart + size);

        return ref Unsafe.AsRef<T>(actualStart);

        static void Fence(byte* ptr) => new Span<byte>(ptr, FenceSize).Fill(0xFF);
    }
}
