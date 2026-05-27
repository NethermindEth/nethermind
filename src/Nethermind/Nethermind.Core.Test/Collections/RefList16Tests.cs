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

        Assert.That(pool[0], Is.EqualTo(TestItem.KeccakA));
        Assert.That(pool[1], Is.EqualTo(TestItem.KeccakB));

        Span<Hash256> span = pool.AsSpan();
        Assert.That(span.Length, Is.EqualTo(2));
        Assert.That(span[0], Is.EqualTo(TestItem.KeccakA));
        Assert.That(span[1], Is.EqualTo(TestItem.KeccakB));
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
}
