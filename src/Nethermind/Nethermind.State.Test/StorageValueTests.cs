// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Store.Test;

public class StorageValueTests
{
    [Test]
    public void TrailingLeadingZeros([Values(0, 1, 7, 8, 9, 15, 16, 17, 23, 24, 25, 31)] int firstSet)
    {
        const int length = 32;
        const byte set = 13;

        Span<byte> span = stackalloc byte[length];
        span[firstSet] = set;

        var value = new StorageValue(span);

        var sliced = value.BytesWithNoLeadingZeroes;
        sliced.Length.Should().Be(length - firstSet);
        sliced[0].Should().Be(set);
    }
}
