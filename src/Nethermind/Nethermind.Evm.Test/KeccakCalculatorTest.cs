// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

[TestFixture]
public class KeccakCalculatorTest
{
    [Test]
    public void Consecutive()
    {
        var calculator = new KeccakCalculator();

        const int seed = 13;
        var random = new Random(seed);
        var payload = new byte[32];
        var actual = new byte[Hash256.Size];

        const int count = 16;

        ushort[] ids = new ushort[count];

        for (int i = 0; i < count; i++)
        {
            random.NextBytes(payload);
            calculator.TrySchedule(payload, out ids[i]).Should().BeTrue();
        }

        // Assert
        random = new Random(seed);
        for (int i = 0; i < count; i++)
        {
            random.NextBytes(payload);
            Span<byte> expected = ValueKeccak.Compute(payload).BytesAsSpan;

            calculator.Copy(ids[i], actual);
            expected.SequenceEqual(actual).Should().BeTrue();
        }
    }
}
