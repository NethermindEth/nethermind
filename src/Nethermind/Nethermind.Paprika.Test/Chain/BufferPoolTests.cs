// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using NUnit.Framework;
using Paprika.Chain;
using Paprika.Store;

namespace Paprika.Tests.Chain;

public class BufferPoolTests
{
    [Test]
    public void Simple_reuse()
    {
        using BufferPool pool = new(1, BufferPool.PageTracking.AssertCount);

        Page initial = pool.Rent();
        pool.Return(initial);

        for (int i = 0; i < 1000; i++)
        {
            Page page = pool.Rent();
            BinaryPrimitives.WriteInt64BigEndian(page.Span, i);

            Assert.That(page, Is.EqualTo(initial));
            pool.Return(page);
        }
    }

    [Test]
    public void Stack_trace_tracking()
    {
        BufferPool pool = new(1, BufferPool.PageTracking.StackTrace);

        // Rent and not return
        pool.Rent();

        Exception exception = Assert.Throws<Exception>(() => pool.Dispose());

        Assert.That(exception.StackTrace, Does.Contain(nameof(BufferPoolTests)).And.Contain(nameof(Stack_trace_tracking)));
    }

    [Test]
    public void Rented_is_clear()
    {
        const int index = 5;
        using BufferPool pool = new(1);

        Page initial = pool.Rent();
        initial.Span[index] = 13;

        pool.Return(initial);

        Page page = pool.Rent();
        Assert.That(page.Span[index], Is.EqualTo(0));

        pool.Return(initial);
    }

    [Test]
    public void Big_pool()
    {
        const int pageCount = 1024;
        using BufferPool pool = new(pageCount, BufferPool.PageTracking.None);

        HashSet<UIntPtr> rawPointers = [];

        for (int i = 0; i < pageCount; i++)
        {
            rawPointers.Add(pool.Rent().Raw);
        }

        Assert.That(rawPointers, Has.Count.EqualTo(pageCount));
    }
}
