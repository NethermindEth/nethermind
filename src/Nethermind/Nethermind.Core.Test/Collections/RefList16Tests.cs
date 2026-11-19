// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Core.Test.Collections;

public class RefList16Tests
{
    [Test]
    public void CanAddItem()
    {
        RefList16<Hash256> pool = new();

        Assert.That(pool.Count, Is.EqualTo(0));

        pool.Add(TestItem.KeccakA);
        Assert.That(pool.Count, Is.EqualTo(1));

        pool.Add(TestItem.KeccakB);
        Assert.That(pool.Count, Is.EqualTo(2));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pool[0], Is.EqualTo(TestItem.KeccakA));
            Assert.That(pool[1], Is.EqualTo(TestItem.KeccakB));
        }

        Span<Hash256> span = pool.AsSpan();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(span.Length, Is.EqualTo(2));
            Assert.That(span[0], Is.EqualTo(TestItem.KeccakA));
            Assert.That(span[1], Is.EqualTo(TestItem.KeccakB));
        }
    }

    [Test]
    public void WillThrowExceptionWhenTryingToAddMoreThan16Item()
    {
        RefList16<Hash256> pool = new();

        for (int i = 0; i < 16; i++)
        {
            pool.Add(Hash256.Zero);
        }
        Assert.That(pool.Count, Is.EqualTo(16));

        try
        {
            pool.Add(TestItem.KeccakA);
            Assert.Fail("Should throw `IndexOutOfRangeException`");
        }
        catch (IndexOutOfRangeException)
        {
        }
    }

    [TestCase(-1, TestName = "negative")]
    [TestCase(17, TestName = "above capacity")]
    [TestCase(int.MinValue, TestName = "int.MinValue")]
    [TestCase(int.MaxValue, TestName = "int.MaxValue")]
    public void Constructor_WithInvalidInitialSize_ThrowsArgumentOutOfRange(int initialSize) => Assert.That(() => { _ = new RefList16<Hash256>(initialSize); }, Throws.InstanceOf<ArgumentOutOfRangeException>());

    [Test]
    public void Constructor_WithMaxInitialSize_IsAllowedAndSpansExactlyCapacity()
    {
        RefList16<Hash256> pool = new(16);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pool.Count, Is.EqualTo(16), "16 is the maximum valid initial size");
            Assert.That(pool.AsSpan().Length, Is.EqualTo(16), "the span must cover exactly the inline storage, never past it");
        }
    }

    [Test]
    public void Add_WhenAtCapacity_ThrowsAndLeavesCountUnchanged()
    {
        RefList16<Hash256> pool = new(16);
        Assert.That(pool.Count, Is.EqualTo(16), "precondition: the list is constructed full");

        try
        {
            pool.Add(TestItem.KeccakA);
            Assert.Fail("adding past capacity must throw IndexOutOfRangeException");
        }
        catch (IndexOutOfRangeException)
        {
        }

        Assert.That(pool.Count, Is.EqualTo(16), "a rejected Add must not modify Count");
    }

    [Test]
    public void Indexer_WhenIndexOutsideCount_ThrowsIndexOutOfRange()
    {
        RefList16<Hash256> pool = new();
        pool.Add(TestItem.KeccakA);

        Assert.That(pool[0], Is.EqualTo(TestItem.KeccakA), "an index within Count returns the stored item");

        try
        {
            _ = pool[1];
            Assert.Fail("indexing at Count must throw IndexOutOfRangeException");
        }
        catch (IndexOutOfRangeException)
        {
        }

        try
        {
            _ = pool[-1];
            Assert.Fail("a negative index must throw IndexOutOfRangeException");
        }
        catch (IndexOutOfRangeException)
        {
        }
    }
}
