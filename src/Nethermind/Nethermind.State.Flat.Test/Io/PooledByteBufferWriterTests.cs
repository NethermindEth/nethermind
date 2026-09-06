// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Io;

[TestFixture]
public class PooledByteBufferWriterTests
{
    [TestCase(1)]
    [TestCase(5000)]
    public void ZeroCapacity_GrowsToFitFirstWrite(int size)
    {
        using PooledByteBufferWriter pooled = new(initialCapacity: 0);
        ref PooledByteBufferWriter.Writer w = ref pooled.GetWriter();

        System.Span<byte> span = w.GetSpan(size);
        for (int i = 0; i < size; i++) span[i] = (byte)(i & 0xff);
        w.Advance(size);

        System.ReadOnlySpan<byte> written = pooled.WrittenSpan;
        Assert.That(written.Length, Is.EqualTo(size));
        for (int i = 0; i < size; i++) Assert.That(written[i], Is.EqualTo((byte)(i & 0xff)));
    }

    // Exercises the Buffer.MemoryCopy branch inside Grow (_written > 0).
    [Test]
    public void Grow_PreservesExistingContentAcrossMultipleGrows()
    {
        using PooledByteBufferWriter pooled = new(initialCapacity: 4);
        ref PooledByteBufferWriter.Writer w = ref pooled.GetWriter();

        for (int chunk = 0; chunk < 6; chunk++)
        {
            const int len = 100;
            System.Span<byte> span = w.GetSpan(len);
            for (int i = 0; i < len; i++) span[i] = (byte)((chunk * 100 + i) & 0xff);
            w.Advance(len);
        }

        System.ReadOnlySpan<byte> written = pooled.WrittenSpan;
        Assert.That(written.Length, Is.EqualTo(600));
        for (int j = 0; j < 600; j++) Assert.That(written[j], Is.EqualTo((byte)(j & 0xff)));
    }
}
