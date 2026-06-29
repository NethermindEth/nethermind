// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Avalanche.Parity;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Avalanche.Test.Parity;

/// <summary>
/// Coreth storage-key partitioning: normal keys clear bit 0 of the first byte; multi-coin keys set it.
/// </summary>
public class AvalancheStorageKeyTests
{
    private const string AllZeros = "0000000000000000000000000000000000000000000000000000000000000000";
    private const string AllOnes = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";

    // Normal storage clears bit 0 of byte 0 (key[0] &= 0xFE): 0xff -> 0xfe, rest unchanged.
    [TestCase(AllZeros, "00")]
    [TestCase(AllOnes, "fe")]
    [TestCase("0100000000000000000000000000000000000000000000000000000000000000", "00")]
    [TestCase("0200000000000000000000000000000000000000000000000000000000000000", "02")]
    public void Normalize_clears_bit0(string keyHex, string expectedFirstByteHex)
    {
        byte[] key = Bytes.FromHexString(keyHex);

        AvalancheStorageKey.Normalize(key);

        Assert.That(key[0], Is.EqualTo(Bytes.FromHexString(expectedFirstByteHex)[0]));
        // Only the first byte is touched.
        Assert.That(key.AsSpan(1).ToArray(), Is.EqualTo(Bytes.FromHexString(keyHex).AsSpan(1).ToArray()));
    }

    // Multi-coin storage sets bit 0 of byte 0 (coinID[0] |= 0x01): 0x00 -> 0x01, rest unchanged.
    [TestCase(AllZeros, "01")]
    [TestCase(AllOnes, "ff")]
    [TestCase("0200000000000000000000000000000000000000000000000000000000000000", "03")]
    [TestCase("0100000000000000000000000000000000000000000000000000000000000000", "01")]
    public void NormalizeCoinId_sets_bit0(string keyHex, string expectedFirstByteHex)
    {
        byte[] key = Bytes.FromHexString(keyHex);

        AvalancheStorageKey.NormalizeCoinId(key);

        Assert.That(key[0], Is.EqualTo(Bytes.FromHexString(expectedFirstByteHex)[0]));
        Assert.That(key.AsSpan(1).ToArray(), Is.EqualTo(Bytes.FromHexString(keyHex).AsSpan(1).ToArray()));
    }

    [Test]
    public void Partitions_never_collide_on_bit0()
    {
        // For any key, the normal and multi-coin transforms differ in bit 0, so they map into disjoint spaces.
        byte[] key = Bytes.FromHexString("8000000000000000000000000000000000000000000000000000000000000000");

        byte[] normal = AvalancheStorageKey.Normalized(key);
        byte[] coin = AvalancheStorageKey.NormalizedCoinId(key);

        Assert.That(normal[0] & AvalancheStorageKey.PartitionBit, Is.EqualTo(0));
        Assert.That(coin[0] & AvalancheStorageKey.PartitionBit, Is.EqualTo(AvalancheStorageKey.PartitionBit));
    }

    [Test]
    public void Normalized_copies_do_not_mutate_input()
    {
        byte[] key = Bytes.FromHexString(AllOnes);

        byte[] normal = AvalancheStorageKey.Normalized(key);
        byte[] coin = AvalancheStorageKey.NormalizedCoinId(key);

        Assert.That(key.ToHexString(), Is.EqualTo(AllOnes), "input must be untouched");
        Assert.That(normal[0], Is.EqualTo((byte)0xfe));
        Assert.That(coin[0], Is.EqualTo((byte)0xff));
    }

    [Test]
    public void Normalize_throws_on_empty()
    {
        Assert.Throws<ArgumentException>(() => AvalancheStorageKey.Normalize(Span<byte>.Empty));
        Assert.Throws<ArgumentException>(() => AvalancheStorageKey.NormalizeCoinId(Span<byte>.Empty));
    }
}
