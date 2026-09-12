// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Core.Test.Collections;

public class RefList64Tests
{
    private const int Capacity = 64;

    [Test]
    public void Add_AppendsUpToCapacityThenThrowsAndLeavesCountUnchanged()
    {
        RefList64<Hash256> pool = new();
        Assert.That(pool.Count, Is.Zero);

        pool.Add(TestItem.KeccakA);
        pool.Add(TestItem.KeccakB);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pool.Count, Is.EqualTo(2));
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

        for (int i = pool.Count; i < Capacity; i++) pool.Add(Hash256.Zero);
        Assert.That(pool.Count, Is.EqualTo(Capacity));

        // a ref struct cannot be captured, so the throwing calls are asserted in place
        try
        {
            pool.Add(TestItem.KeccakA);
            Assert.Fail("adding past capacity must throw IndexOutOfRangeException");
        }
        catch (IndexOutOfRangeException)
        {
        }

        Assert.That(pool.Count, Is.EqualTo(Capacity), "a rejected Add must not modify Count");
    }

    [TestCase(-1, TestName = "negative")]
    [TestCase(Capacity + 1, TestName = "above capacity")]
    [TestCase(int.MinValue, TestName = "int.MinValue")]
    [TestCase(int.MaxValue, TestName = "int.MaxValue")]
    public void Constructor_WithInvalidInitialSize_ThrowsArgumentOutOfRange(int initialSize) =>
        Assert.That(() => { _ = new RefList64<Hash256>(initialSize); }, Throws.InstanceOf<ArgumentOutOfRangeException>());

    /// <summary>
    /// The sized constructor clears what it exposes, which is what lets a caller take the inline array
    /// off its prolog with <c>[SkipLocalsInit]</c>.
    /// </summary>
    [Test]
    public void Constructor_WithMaxInitialSize_SpansExactlyCapacityAndIsCleared()
    {
        RefList64<Hash256> pool = new(Capacity);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pool.Count, Is.EqualTo(Capacity), "64 is the maximum valid initial size");
            Assert.That(pool.AsSpan().Length, Is.EqualTo(Capacity), "the span must cover exactly the inline storage, never past it");
        }

        foreach (Hash256? item in pool.AsSpan()) Assert.That(item, Is.Null);
    }

    [Test]
    public void Indexer_WhenIndexOutsideCount_ThrowsIndexOutOfRange()
    {
        RefList64<Hash256> pool = new();
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
