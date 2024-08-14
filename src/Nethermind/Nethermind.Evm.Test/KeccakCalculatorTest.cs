// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
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
        using var cts = new CancellationTokenSource();

        KeccakCalculator.StartWorker(cts.Token);

        var calculator = new KeccakCalculator();

        const int seed = 13;
        var random = new Random(seed);
        var payload = new byte[32];

        const int count = 128;

        var ids = new OffHeapStack.Slot[count];

        for (int i = 0; i < count; i++)
        {
            random.NextBytes(payload);
            ids[i] = calculator.TrySchedule(payload);
            ids[i].IsAcquired.Should().BeTrue();
        }

        // Reset and assert
        random = new Random(seed);
        for (int i = 0; i < count; i++)
        {
            random.NextBytes(payload);
            Span<byte> expected = ValueKeccak.Compute(payload).BytesAsSpan;

            ReadOnlySpan<byte> actual = KeccakCalculator.Get(ids[i]);
            expected.SequenceEqual(actual).Should().BeTrue();
        }

        cts.Cancel();
    }
}
