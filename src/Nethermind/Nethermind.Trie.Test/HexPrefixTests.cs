// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

public class HexPrefixTests
{
    [TestCase(false, (byte)3, (byte)19)]
    [TestCase(true, (byte)3, (byte)51)]
    public void Encode_gives_correct_output_when_one(bool flag, byte nibble1, byte byte1)
    {
        byte[] output = HexPrefix.ToBytes(new[] { nibble1 }, flag);
        Assert.That(output.Length, Is.EqualTo(1));
        Assert.That(output[0], Is.EqualTo(byte1));
    }

    [TestCase(false, (byte)3, (byte)7, (byte)13, (byte)19, (byte)125)]
    [TestCase(true, (byte)3, (byte)7, (byte)13, (byte)51, (byte)125)]
    public void Encode_gives_correct_output_when_odd(bool flag, byte nibble1, byte nibble2, byte nibble3,
        byte byte1, byte byte2)
    {
        byte[] output = HexPrefix.ToBytes(new[] { nibble1, nibble2, nibble3 }, flag);

        Assert.That(output.Length, Is.EqualTo(2));
        Assert.That(output[0], Is.EqualTo(byte1));
        Assert.That(output[1], Is.EqualTo(byte2));
    }

    [TestCase(false, (byte)3, (byte)7, (byte)0, (byte)55)]
    [TestCase(true, (byte)3, (byte)7, (byte)32, (byte)55)]
    public void Encode_gives_correct_output_when_even(bool flag, byte nibble1, byte nibble2, byte byte1, byte byte2)
    {
        byte[] output = HexPrefix.ToBytes(new[] { nibble1, nibble2 }, flag);

        Assert.That(output.Length, Is.EqualTo(2));
        Assert.That(output[0], Is.EqualTo(byte1));
        Assert.That(output[1], Is.EqualTo(byte2));
    }

    [TestCase(false, (byte)3, (byte)7, (byte)0, (byte)55)]
    [TestCase(true, (byte)3, (byte)7, (byte)32, (byte)55)]
    public void Decode_gives_correct_output_when_even(bool expectedFlag, byte nibble1, byte nibble2, byte byte1,
        byte byte2)
    {
        (byte[] key, bool isLeaf) = HexPrefix.FromBytes(new[] { byte1, byte2 });
        Assert.That(isLeaf, Is.EqualTo(expectedFlag));
        Assert.That(key.Length, Is.EqualTo(2));
        Assert.That(key[0], Is.EqualTo(nibble1));
        Assert.That(key[1], Is.EqualTo(nibble2));
    }

    [TestCase(false, (byte)3, (byte)19)]
    [TestCase(true, (byte)3, (byte)51)]
    public void Decode_gives_correct_output_when_one(bool expectedFlag, byte nibble1, byte byte1)
    {
        (byte[] key, bool isLeaf) = HexPrefix.FromBytes(new[] { byte1 });

        Assert.That(isLeaf, Is.EqualTo(expectedFlag));
        Assert.That(key.Length, Is.EqualTo(1));
        Assert.That(key[0], Is.EqualTo(nibble1));
    }

    [TestCase(false, (byte)3, (byte)7, (byte)13, (byte)19, (byte)125)]
    [TestCase(true, (byte)3, (byte)7, (byte)13, (byte)51, (byte)125)]
    public void Decode_gives_correct_output_when_odd(bool expectedFlag, byte nibble1, byte nibble2, byte nibble3,
        byte byte1, byte byte2)
    {
        (byte[] key, bool isLeaf) = HexPrefix.FromBytes(new[] { byte1, byte2 });
        Assert.That(isLeaf, Is.EqualTo(expectedFlag));
        Assert.That(key.Length, Is.EqualTo(3));
        Assert.That(key[0], Is.EqualTo(nibble1));
        Assert.That(key[1], Is.EqualTo(nibble2));
        Assert.That(key[2], Is.EqualTo(nibble3));
    }

    // According to: https://ethereum.github.io/yellowpaper/paper.pdf#appendix.C
    // Leaf flag (t) is omitted
    [TestCase(new byte[] { 1, 2, 3, 4 }, new byte[] { 0, 1 * 16 + 2, 3 * 16 + 4 })]
    [TestCase(new byte[] { 1, 2, 3 }, new byte[] { 16 + 1, 2 * 16 + 3 })]
    public void Compact_hex_encoding_correct_output(byte[] nibbles, byte[] bytes)
    {
        byte[] result = Nibbles.ToCompactHexEncoding(nibbles);
        Assert.That(bytes, Is.EqualTo(result).AsCollection);
    }

    // Just pack nibbles to bytes
    [Test]
    public void Nibbles_to_bytes_correct_output()
    {
        byte[] nibbles = Enumerable.Repeat((byte)1, 64).ToArray();
        byte[] bytes = Enumerable.Repeat((byte)17, 32).ToArray();
        byte[] result = Nibbles.ToBytes(nibbles);
        Assert.That(bytes, Is.EqualTo(result).AsCollection);
    }

    [Test]
    public void GetPathArray_returns_cached_array_for_single_nibble()
    {
        // Test all valid single nibble values (0-15)
        for (byte i = 0; i < 16; i++)
        {
            byte[] path1 = HexPrefix.GetPathArray(new byte[] { i });
            byte[] path2 = HexPrefix.GetPathArray(new byte[] { i });
            
            // Should return the same cached instance
            Assert.That(ReferenceEquals(path1, path2), Is.True, $"Single nibble {i} should return cached array");
            Assert.That(path1.Length, Is.EqualTo(1));
            Assert.That(path1[0], Is.EqualTo(i));
        }
    }

    [Test]
    public void GetPathArray_returns_cached_array_for_double_nibble()
    {
        // Test a sample of double nibble values
        for (byte i = 0; i < 16; i++)
        {
            for (byte j = 0; j < 16; j++)
            {
                byte[] path1 = HexPrefix.GetPathArray(new byte[] { i, j });
                byte[] path2 = HexPrefix.GetPathArray(new byte[] { i, j });
                
                // Should return the same cached instance
                Assert.That(ReferenceEquals(path1, path2), Is.True, $"Double nibble [{i},{j}] should return cached array");
                Assert.That(path1.Length, Is.EqualTo(2));
                Assert.That(path1[0], Is.EqualTo(i));
                Assert.That(path1[1], Is.EqualTo(j));
            }
        }
    }

    [Test]
    public void GetPathArray_returns_cached_array_for_triple_nibble()
    {
        // Test a sample of triple nibble values
        for (byte i = 0; i < 4; i++)
        {
            for (byte j = 0; j < 4; j++)
            {
                for (byte k = 0; k < 4; k++)
                {
                    byte[] path1 = HexPrefix.GetPathArray(new byte[] { i, j, k });
                    byte[] path2 = HexPrefix.GetPathArray(new byte[] { i, j, k });
                    
                    // Should return the same cached instance
                    Assert.That(ReferenceEquals(path1, path2), Is.True, $"Triple nibble [{i},{j},{k}] should return cached array");
                    Assert.That(path1.Length, Is.EqualTo(3));
                    Assert.That(path1[0], Is.EqualTo(i));
                    Assert.That(path1[1], Is.EqualTo(j));
                    Assert.That(path1[2], Is.EqualTo(k));
                }
            }
        }
    }

    [Test]
    public void GetPathArray_allocates_new_array_for_invalid_nibble_values()
    {
        // Test single nibble with value >= 16
        byte[] path1 = HexPrefix.GetPathArray(new byte[] { 16 });
        byte[] path2 = HexPrefix.GetPathArray(new byte[] { 16 });
        Assert.That(ReferenceEquals(path1, path2), Is.False, "Should allocate new array for nibble value >= 16");
        Assert.That(path1[0], Is.EqualTo(16));

        // Test double nibble with value >= 16
        byte[] path3 = HexPrefix.GetPathArray(new byte[] { 5, 16 });
        byte[] path4 = HexPrefix.GetPathArray(new byte[] { 5, 16 });
        Assert.That(ReferenceEquals(path3, path4), Is.False, "Should allocate new array for nibble value >= 16");
        Assert.That(path3[0], Is.EqualTo(5));
        Assert.That(path3[1], Is.EqualTo(16));

        // Test triple nibble with value >= 16
        byte[] path5 = HexPrefix.GetPathArray(new byte[] { 3, 7, 20 });
        byte[] path6 = HexPrefix.GetPathArray(new byte[] { 3, 7, 20 });
        Assert.That(ReferenceEquals(path5, path6), Is.False, "Should allocate new array for nibble value >= 16");
        Assert.That(path5[0], Is.EqualTo(3));
        Assert.That(path5[1], Is.EqualTo(7));
        Assert.That(path5[2], Is.EqualTo(20));
    }

    [Test]
    public void GetPathArray_allocates_new_array_for_longer_paths()
    {
        // Test paths longer than 3 nibbles
        byte[] path1 = HexPrefix.GetPathArray(new byte[] { 1, 2, 3, 4 });
        byte[] path2 = HexPrefix.GetPathArray(new byte[] { 1, 2, 3, 4 });
        
        Assert.That(ReferenceEquals(path1, path2), Is.False, "Should allocate new array for paths longer than 3");
        Assert.That(path1.Length, Is.EqualTo(4));
        Assert.That(path1, Is.EqualTo(new byte[] { 1, 2, 3, 4 }).AsCollection);
    }

    [Test]
    public void GetPathArray_returns_empty_array_for_empty_path()
    {
        byte[] path = HexPrefix.GetPathArray(ReadOnlySpan<byte>.Empty);
        Assert.That(path.Length, Is.EqualTo(0));
    }
}
