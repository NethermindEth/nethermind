// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Core.ZkEvm.Test.Extensions;

/// <summary>
/// Covers the guest form of <see cref="Bytes.LeadingZeroBytes"/>, which the host suite cannot reach:
/// the host build is <see cref="BitOperations.LeadingZeroCount(ulong)"/> shifted, so only a ZK_EVM
/// build exercises the byte-boundary switch that replaces it.
/// </summary>
/// <remarks>
/// Every byte boundary is spelled out as its own threshold, so an off-by-one in one of them moves the
/// answer only for values straddling that single boundary. The cases therefore sit on both sides of
/// every boundary rather than sampling the range.
/// </remarks>
public class LeadingZeroBytesTests
{
    private static readonly ulong[] Values =
    [
        0, 1, ulong.MaxValue, 0x0123456789ABCDEFUL, 0x8000000000000000UL,
        0xFFUL, 0x100UL, 0xFFFFUL, 0x10000UL, 0xFFFFFFUL, 0x1000000UL,
        0xFFFFFFFFUL, 0x100000000UL, 0xFFFFFFFFFFUL, 0x10000000000UL,
        0xFFFFFFFFFFFFUL, 0x1000000000000UL, 0xFFFFFFFFFFFFFFUL, 0x100000000000000UL
    ];

    [Test]
    public void Counts_the_same_bytes_as_a_clz([ValueSource(nameof(Values))] ulong value)
        => Assert.That(Bytes.LeadingZeroBytes(value), Is.EqualTo(BitOperations.LeadingZeroCount(value) >> 3));

    [Test]
    public void A_single_set_byte_leaves_the_bytes_above_it([Range(0, 7)] int index)
        => Assert.That(Bytes.LeadingZeroBytes(0xA5UL << (index * 8)), Is.EqualTo(7 - index));

    [Test]
    public void An_empty_word_is_all_zero_bytes()
        => Assert.That(Bytes.LeadingZeroBytes(0), Is.EqualTo(sizeof(ulong)));
}
