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
            byte[] path1 = HexPrefix.GetArray(new byte[] { i });
            byte[] path2 = HexPrefix.GetArray(new byte[] { i });

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
                byte[] path1 = HexPrefix.GetArray(new byte[] { i, j });
                byte[] path2 = HexPrefix.GetArray(new byte[] { i, j });

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
                    byte[] path1 = HexPrefix.GetArray(new byte[] { i, j, k });
                    byte[] path2 = HexPrefix.GetArray(new byte[] { i, j, k });

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
        byte[] path1 = HexPrefix.GetArray(new byte[] { 16 });
        byte[] path2 = HexPrefix.GetArray(new byte[] { 16 });
        Assert.That(ReferenceEquals(path1, path2), Is.False, "Should allocate new array for nibble value >= 16");
        Assert.That(path1[0], Is.EqualTo(16));

        // Test double nibble with value >= 16
        byte[] path3 = HexPrefix.GetArray(new byte[] { 5, 16 });
        byte[] path4 = HexPrefix.GetArray(new byte[] { 5, 16 });
        Assert.That(ReferenceEquals(path3, path4), Is.False, "Should allocate new array for nibble value >= 16");
        Assert.That(path3[0], Is.EqualTo(5));
        Assert.That(path3[1], Is.EqualTo(16));

        // Test triple nibble with value >= 16
        byte[] path5 = HexPrefix.GetArray(new byte[] { 3, 7, 20 });
        byte[] path6 = HexPrefix.GetArray(new byte[] { 3, 7, 20 });
        Assert.That(ReferenceEquals(path5, path6), Is.False, "Should allocate new array for nibble value >= 16");
        Assert.That(path5[0], Is.EqualTo(3));
        Assert.That(path5[1], Is.EqualTo(7));
        Assert.That(path5[2], Is.EqualTo(20));
    }

    [Test]
    public void GetPathArray_allocates_new_array_for_longer_paths()
    {
        // Test paths longer than 3 nibbles
        byte[] path1 = HexPrefix.GetArray(new byte[] { 1, 2, 3, 4 });
        byte[] path2 = HexPrefix.GetArray(new byte[] { 1, 2, 3, 4 });

        Assert.That(ReferenceEquals(path1, path2), Is.False, "Should allocate new array for paths longer than 3");
        Assert.That(path1.Length, Is.EqualTo(4));
        Assert.That(path1, Is.EqualTo(new byte[] { 1, 2, 3, 4 }).AsCollection);
    }

    [Test]
    public void GetPathArray_returns_empty_array_for_empty_path()
    {
        byte[] path = HexPrefix.GetArray(ReadOnlySpan<byte>.Empty);
        Assert.That(path.Length, Is.EqualTo(0));
    }

    [Test]
    public void PrependNibble_returns_cached_array_for_single_nibble_result()
    {
        // Prepending to empty array should return cached single nibble
        for (byte i = 0; i < 16; i++)
        {
            byte[] result1 = HexPrefix.PrependNibble(i, []);
            byte[] result2 = HexPrefix.PrependNibble(i, []);

            Assert.That(ReferenceEquals(result1, result2), Is.True, $"PrependNibble({i}, []) should return cached array");
            Assert.That(result1.Length, Is.EqualTo(1));
            Assert.That(result1[0], Is.EqualTo(i));
        }
    }

    [Test]
    public void PrependNibble_returns_cached_array_for_double_nibble_result()
    {
        // Prepending to single nibble array should return cached double nibble
        for (byte i = 0; i < 16; i++)
        {
            for (byte j = 0; j < 16; j++)
            {
                byte[] result1 = HexPrefix.PrependNibble(i, new byte[] { j });
                byte[] result2 = HexPrefix.PrependNibble(i, new byte[] { j });

                Assert.That(ReferenceEquals(result1, result2), Is.True, $"PrependNibble({i}, [{j}]) should return cached array");
                Assert.That(result1.Length, Is.EqualTo(2));
                Assert.That(result1[0], Is.EqualTo(i));
                Assert.That(result1[1], Is.EqualTo(j));
            }
        }
    }

    [Test]
    public void PrependNibble_returns_cached_array_for_triple_nibble_result()
    {
        // Prepending to double nibble array should return cached triple nibble
        for (byte i = 0; i < 4; i++)
        {
            for (byte j = 0; j < 4; j++)
            {
                for (byte k = 0; k < 4; k++)
                {
                    byte[] result1 = HexPrefix.PrependNibble(i, new byte[] { j, k });
                    byte[] result2 = HexPrefix.PrependNibble(i, new byte[] { j, k });

                    Assert.That(ReferenceEquals(result1, result2), Is.True, $"PrependNibble({i}, [{j},{k}]) should return cached array");
                    Assert.That(result1.Length, Is.EqualTo(3));
                    Assert.That(result1[0], Is.EqualTo(i));
                    Assert.That(result1[1], Is.EqualTo(j));
                    Assert.That(result1[2], Is.EqualTo(k));
                }
            }
        }
    }

    [Test]
    public void PrependNibble_allocates_new_array_for_invalid_nibble_values()
    {
        // Test with nibble value >= 16
        byte[] result1 = HexPrefix.PrependNibble(16, []);
        byte[] result2 = HexPrefix.PrependNibble(16, []);
        Assert.That(ReferenceEquals(result1, result2), Is.False, "Should allocate new array for nibble value >= 16");
        Assert.That(result1.Length, Is.EqualTo(1));
        Assert.That(result1[0], Is.EqualTo(16));
    }

    [Test]
    public void PrependNibble_allocates_new_array_for_longer_results()
    {
        // Prepending to array of length 3+ should allocate new array
        byte[] result1 = HexPrefix.PrependNibble(1, new byte[] { 2, 3, 4 });
        byte[] result2 = HexPrefix.PrependNibble(1, new byte[] { 2, 3, 4 });

        Assert.That(ReferenceEquals(result1, result2), Is.False, "Should allocate new array for length > 3");
        Assert.That(result1.Length, Is.EqualTo(4));
        Assert.That(result1, Is.EqualTo(new byte[] { 1, 2, 3, 4 }).AsCollection);
    }

    [Test]
    public void ConcatNibbles_returns_empty_array_for_empty_inputs()
    {
        byte[] result = HexPrefix.ConcatNibbles([], []);
        Assert.That(result.Length, Is.EqualTo(0));
    }

    [Test]
    public void ConcatNibbles_returns_cached_array_for_single_nibble_result()
    {
        // Test [1] + [] = [1]
        for (byte i = 0; i < 16; i++)
        {
            byte[] result1 = HexPrefix.ConcatNibbles(new byte[] { i }, []);
            byte[] result2 = HexPrefix.ConcatNibbles(new byte[] { i }, []);
            Assert.That(ReferenceEquals(result1, result2), Is.True, $"ConcatNibbles([{i}], []) should return cached array");
            Assert.That(result1.Length, Is.EqualTo(1));
            Assert.That(result1[0], Is.EqualTo(i));

            // Test [] + [1] = [1]
            byte[] result3 = HexPrefix.ConcatNibbles([], new byte[] { i });
            byte[] result4 = HexPrefix.ConcatNibbles([], new byte[] { i });
            Assert.That(ReferenceEquals(result3, result4), Is.True, $"ConcatNibbles([], [{i}]) should return cached array");
            Assert.That(result3.Length, Is.EqualTo(1));
            Assert.That(result3[0], Is.EqualTo(i));
        }
    }

    [Test]
    public void ConcatNibbles_returns_cached_array_for_double_nibble_result()
    {
        // Test various combinations that result in length 2
        for (byte i = 0; i < 16; i++)
        {
            for (byte j = 0; j < 16; j++)
            {
                // Test [i,j] + [] = [i,j]
                byte[] result1 = HexPrefix.ConcatNibbles(new byte[] { i, j }, []);
                byte[] result2 = HexPrefix.ConcatNibbles(new byte[] { i, j }, []);
                Assert.That(ReferenceEquals(result1, result2), Is.True, $"ConcatNibbles([{i},{j}], []) should return cached array");
                Assert.That(result1.Length, Is.EqualTo(2));
                Assert.That(result1[0], Is.EqualTo(i));
                Assert.That(result1[1], Is.EqualTo(j));

                // Test [i] + [j] = [i,j]
                byte[] result3 = HexPrefix.ConcatNibbles(new byte[] { i }, new byte[] { j });
                byte[] result4 = HexPrefix.ConcatNibbles(new byte[] { i }, new byte[] { j });
                Assert.That(ReferenceEquals(result3, result4), Is.True, $"ConcatNibbles([{i}], [{j}]) should return cached array");
                Assert.That(result3.Length, Is.EqualTo(2));
                Assert.That(result3[0], Is.EqualTo(i));
                Assert.That(result3[1], Is.EqualTo(j));

                // Test [] + [i,j] = [i,j]
                byte[] result5 = HexPrefix.ConcatNibbles([], new byte[] { i, j });
                byte[] result6 = HexPrefix.ConcatNibbles([], new byte[] { i, j });
                Assert.That(ReferenceEquals(result5, result6), Is.True, $"ConcatNibbles([], [{i},{j}]) should return cached array");
                Assert.That(result5.Length, Is.EqualTo(2));
                Assert.That(result5[0], Is.EqualTo(i));
                Assert.That(result5[1], Is.EqualTo(j));
            }
        }
    }

    [Test]
    public void ConcatNibbles_returns_cached_array_for_triple_nibble_result()
    {
        // Test various combinations that result in length 3
        for (byte i = 0; i < 4; i++)
        {
            for (byte j = 0; j < 4; j++)
            {
                for (byte k = 0; k < 4; k++)
                {
                    // Test [i,j,k] + [] = [i,j,k]
                    byte[] result1 = HexPrefix.ConcatNibbles(new byte[] { i, j, k }, []);
                    byte[] result2 = HexPrefix.ConcatNibbles(new byte[] { i, j, k }, []);
                    Assert.That(ReferenceEquals(result1, result2), Is.True, $"ConcatNibbles([{i},{j},{k}], []) should return cached array");
                    Assert.That(result1.Length, Is.EqualTo(3));
                    Assert.That(result1[0], Is.EqualTo(i));
                    Assert.That(result1[1], Is.EqualTo(j));
                    Assert.That(result1[2], Is.EqualTo(k));

                    // Test [i] + [j,k] = [i,j,k]
                    byte[] result3 = HexPrefix.ConcatNibbles(new byte[] { i }, new byte[] { j, k });
                    byte[] result4 = HexPrefix.ConcatNibbles(new byte[] { i }, new byte[] { j, k });
                    Assert.That(ReferenceEquals(result3, result4), Is.True, $"ConcatNibbles([{i}], [{j},{k}]) should return cached array");
                    Assert.That(result3.Length, Is.EqualTo(3));
                    Assert.That(result3[0], Is.EqualTo(i));
                    Assert.That(result3[1], Is.EqualTo(j));
                    Assert.That(result3[2], Is.EqualTo(k));

                    // Test [i,j] + [k] = [i,j,k]
                    byte[] result5 = HexPrefix.ConcatNibbles(new byte[] { i, j }, new byte[] { k });
                    byte[] result6 = HexPrefix.ConcatNibbles(new byte[] { i, j }, new byte[] { k });
                    Assert.That(ReferenceEquals(result5, result6), Is.True, $"ConcatNibbles([{i},{j}], [{k}]) should return cached array");
                    Assert.That(result5.Length, Is.EqualTo(3));
                    Assert.That(result5[0], Is.EqualTo(i));
                    Assert.That(result5[1], Is.EqualTo(j));
                    Assert.That(result5[2], Is.EqualTo(k));

                    // Test [] + [i,j,k] = [i,j,k]
                    byte[] result7 = HexPrefix.ConcatNibbles([], new byte[] { i, j, k });
                    byte[] result8 = HexPrefix.ConcatNibbles([], new byte[] { i, j, k });
                    Assert.That(ReferenceEquals(result7, result8), Is.True, $"ConcatNibbles([], [{i},{j},{k}]) should return cached array");
                    Assert.That(result7.Length, Is.EqualTo(3));
                    Assert.That(result7[0], Is.EqualTo(i));
                    Assert.That(result7[1], Is.EqualTo(j));
                    Assert.That(result7[2], Is.EqualTo(k));
                }
            }
        }
    }

    [Test]
    public void ConcatNibbles_allocates_new_array_for_longer_results()
    {
        // Test combinations that result in length > 3
        byte[] result1 = HexPrefix.ConcatNibbles(new byte[] { 1, 2 }, new byte[] { 3, 4 });
        byte[] result2 = HexPrefix.ConcatNibbles(new byte[] { 1, 2 }, new byte[] { 3, 4 });

        Assert.That(ReferenceEquals(result1, result2), Is.False, "Should allocate new array for length > 3");
        Assert.That(result1.Length, Is.EqualTo(4));
        Assert.That(result1, Is.EqualTo(new byte[] { 1, 2, 3, 4 }).AsCollection);

        // Test longer arrays
        byte[] result3 = HexPrefix.ConcatNibbles(new byte[] { 1, 2, 3 }, new byte[] { 4, 5 });
        byte[] result4 = HexPrefix.ConcatNibbles(new byte[] { 1, 2, 3 }, new byte[] { 4, 5 });

        Assert.That(ReferenceEquals(result3, result4), Is.False, "Should allocate new array for length > 3");
        Assert.That(result3.Length, Is.EqualTo(5));
        Assert.That(result3, Is.EqualTo(new byte[] { 1, 2, 3, 4, 5 }).AsCollection);
    }

    [Test]
    public void ConcatNibbles_preserves_values_correctly()
    {
        // Test that values are preserved in the correct order
        byte[] result = HexPrefix.ConcatNibbles(new byte[] { 15, 14, 13 }, new byte[] { 12, 11, 10 });
        Assert.That(result, Is.EqualTo(new byte[] { 15, 14, 13, 12, 11, 10 }).AsCollection);
    }
}
