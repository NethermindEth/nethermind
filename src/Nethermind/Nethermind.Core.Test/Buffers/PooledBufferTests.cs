// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Buffers;
using NUnit.Framework;

namespace Nethermind.Core.Test.Buffers;

public class PooledBufferTests
{
    [Test]
    public void Rent_returns_writable_span_of_requested_length()
    {
        using PooledBuffer buffer = PooledBuffer.Rent(16);

        Assert.That(buffer.Length, Is.EqualTo(16));
        buffer.Span.Fill(7);
        Assert.That(buffer.ReadOnlySpan.ToArray(), Is.All.EqualTo((byte)7));
    }

    [Test]
    public void Dispose_is_idempotent()
    {
        PooledBuffer buffer = PooledBuffer.Rent(8);
        buffer.Dispose();

        Assert.DoesNotThrow(() => buffer.Dispose());
    }

    [Test]
    public void Slice_keeps_rental_alive_after_owner_dispose()
    {
        PooledBuffer buffer = PooledBuffer.Rent(8);
        buffer.Span.Fill(9);
        PooledBuffer.Slice slice = buffer[2..6];
        buffer.Dispose();

        Assert.That(slice.ReadOnlySpan.ToArray(), Is.All.EqualTo((byte)9));
        slice.Dispose();
    }

    [Test]
    public void Slice_rejects_use_after_all_releases()
    {
        PooledBuffer buffer = PooledBuffer.Rent(8);
        PooledBuffer.Slice slice = buffer[..];
        buffer.Dispose();
        slice.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _ = buffer[..]);
        Assert.Throws<ObjectDisposedException>(() => _ = slice.Span);
    }

    [Test]
    public void Slice_rejects_out_of_range()
    {
        using PooledBuffer buffer = PooledBuffer.Rent(8);

        Assert.Throws<ArgumentOutOfRangeException>(() => _ = buffer[6..10]);
    }

    [Test]
    public void SliceRange_creates_sub_view()
    {
        using PooledBuffer buffer = PooledBuffer.Rent(8);
        buffer.Span.Fill(3);
        using PooledBuffer.Slice slice = buffer[..];
        using PooledBuffer.Slice sub = slice.SliceRange(2, 4);

        Assert.That(sub.Length, Is.EqualTo(4));
        Assert.That(sub.ReadOnlySpan.ToArray(), Is.All.EqualTo((byte)3));
    }

    [Test]
    public void RentSlice_supports_prepend_into_headroom()
    {
        using PooledBuffer.Slice slice = PooledBuffer.RentSlice(4, headroom: 2);
        slice.Span.Fill(5);

        Assert.That(slice.TryPrepend(2, out PooledBuffer.Slice prepended), Is.True);
        using (prepended)
        {
            Assert.That(prepended.Length, Is.EqualTo(6));
            prepended.Span.Slice(0, 2).Fill(6);
            Assert.That(prepended.ReadOnlySpan.Slice(2).ToArray(), Is.All.EqualTo((byte)5));
        }

        Assert.That(slice.TryPrepend(3, out _), Is.False);
    }
}
