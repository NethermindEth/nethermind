// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
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
        RefList16<Hash256> pool = new RefList16<Hash256>();

        pool.Count.Should().Be(0);

        pool.Add(TestItem.KeccakA);
        pool.Count.Should().Be(1);

        pool.Add(TestItem.KeccakB);
        pool.Count.Should().Be(2);

        pool[0].Should().Be(TestItem.KeccakA);
        pool[1].Should().Be(TestItem.KeccakB);

        Span<Hash256> span = pool.AsSpan();
        span.Length.Should().Be(2);
        span[0].Should().Be(TestItem.KeccakA);
        span[1].Should().Be(TestItem.KeccakB);
    }

    [Test]
    public void WillThrowExceptionWhenTryingToAddMoreThan16Item()
    {
        RefList16<Hash256> pool = new RefList16<Hash256>();

        for (int i = 0; i < 16; i++)
        {
            pool.Add(Hash256.Zero);
        }
        pool.Count.Should().Be(16);

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
