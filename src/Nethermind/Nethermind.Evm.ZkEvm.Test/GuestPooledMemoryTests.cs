// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.ZkEvm.Test;

/// <summary>Memory buffers of frames that outnumber the per-thread cache are reused rather than reallocated.</summary>
/// <remarks>
/// The guest has no GC, so every allocation is permanent: a buffer dropped on return would make heap use grow with
/// the number of frames a block enters (a recursive self-CALL expanding memory each frame) rather than with how many
/// are live at once.
/// </remarks>
public class GuestPooledMemoryTests
{
    private const int LiveFrames = 64;
    private const int BufferSize = 16 * 1024;
    private const int Rounds = 10;

    // Past 2^13, so each frame rents a 16 KiB buffer.
    private static readonly UInt256 Offset = 8223;

    [Test]
    public void Buffers_of_more_live_frames_than_the_cache_holds_are_reused()
    {
        EvmPooledMemory[] frames = new EvmPooledMemory[LiveFrames];
        ExpandAndDispose(frames);

        bool expanded = true;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int round = 0; round < Rounds; round++)
            expanded &= ExpandAndDispose(frames);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(expanded, Is.True);
            // Arrays a cache full of other tests' buffers holds back may need replacing once; a leak would reallocate every round.
            Assert.That(allocated, Is.LessThan((long)LiveFrames * BufferSize));
        }
    }

    private static bool ExpandAndDispose(EvmPooledMemory[] frames)
    {
        bool expanded = true;
        for (int i = 0; i < frames.Length; i++)
            expanded &= frames[i].TrySaveByte(in Offset, 1);

        for (int i = frames.Length - 1; i >= 0; i--)
            frames[i].Dispose();

        return expanded;
    }
}
