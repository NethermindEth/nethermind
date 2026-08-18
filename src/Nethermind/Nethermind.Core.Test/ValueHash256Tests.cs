// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public class ValueHash256Tests
{
    // A single-byte difference at every position guards the split-compare paths:
    // a compare that checks only one 16-byte half would miss half of these cases.
    [Test]
    public void Equals_detects_difference_at_every_byte_position()
    {
        byte[] baseBytes = new byte[32];
        for (int i = 0; i < baseBytes.Length; i++)
        {
            baseBytes[i] = (byte)(i + 1);
        }
        ValueHash256 baseHash = new(baseBytes);

        Assert.That(baseHash.Equals(new ValueHash256(baseBytes)), Is.True);
        for (int i = 0; i < baseBytes.Length; i++)
        {
            byte[] mutated = (byte[])baseBytes.Clone();
            mutated[i] ^= 0xFF;
            Assert.That(baseHash.Equals(new ValueHash256(mutated)), Is.False, $"difference at byte {i} not detected");
        }
    }

    [Test]
    public void Null_hash_operator_equality_matches_zero_value()
    {
        Hash256? nullHash = null;
        byte[] lastByteSet = new byte[32];
        lastByteSet[31] = 1;
        byte[] firstByteSet = new byte[32];
        firstByteSet[0] = 1;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(nullHash == default(ValueHash256), Is.True);
            Assert.That(nullHash == new ValueHash256(lastByteSet), Is.False);
            Assert.That(nullHash == new ValueHash256(firstByteSet), Is.False);
            Assert.That(Keccak.Zero == new ValueHash256(Keccak.Zero.Bytes), Is.True);
        }
    }
}
