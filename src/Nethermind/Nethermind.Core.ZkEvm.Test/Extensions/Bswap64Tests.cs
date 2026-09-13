// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Core.ZkEvm.Test.Extensions;

/// <summary>
/// Covers the guest forms of <see cref="Bytes.Bswap64"/> and <see cref="Bytes.HoistBswap64"/>, which
/// the host suite cannot reach: the host build defers to
/// <see cref="BinaryPrimitives.ReverseEndianness(ulong)"/>, so only a ZK_EVM build exercises the
/// masked shift/or form and the masks the hoisted variant carries in locals.
/// </summary>
/// <remarks>
/// A byte swap that drops or transposes one lane still round-trips and still passes a palindrome, so
/// the cases are asymmetric and each is checked against the BCL rather than against a second swap.
/// </remarks>
public class Bswap64Tests
{
    private static readonly ulong[] Values =
    [
        0, ulong.MaxValue, 1, 0x8000000000000000UL, 0x0123456789ABCDEFUL, 0xFF00FF00FF00FF00UL,
        0x00000000FFFFFFFFUL, 0xFFFFFFFF00000000UL, 0x0000FFFFFFFF0000UL, 0xDEADBEEFCAFEBABEUL
    ];

    [Test]
    public void Swaps_the_bytes_of_a_word([ValueSource(nameof(Values))] ulong value)
        => Assert.That(Bytes.Bswap64(value), Is.EqualTo(BinaryPrimitives.ReverseEndianness(value)));

    [Test]
    public void Hoisted_masks_swap_the_same_way([ValueSource(nameof(Values))] ulong value)
    {
        Bytes.Bswap64Hoist swap = Bytes.HoistBswap64();

        Assert.That(swap.Bswap64(value), Is.EqualTo(BinaryPrimitives.ReverseEndianness(value)));
    }

    [Test]
    public void Every_byte_lands_in_its_mirrored_position([Range(0, 7)] int index)
        => Assert.That(Bytes.Bswap64(0xA5UL << (index * 8)), Is.EqualTo(0xA5UL << ((7 - index) * 8)));
}
