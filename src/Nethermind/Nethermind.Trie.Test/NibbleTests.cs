// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.Trie.Test;

[Parallelizable(ParallelScope.All)]
public class NibbleTests
{
    private readonly byte[][] _hexEncoding =
    [
        [],
        [16],
        [1, 2, 3, 4, 5],
        [0, 1, 2, 3, 4, 5],
        [15, 1, 12, 11, 8, 16],
        [0, 15, 1, 12, 11, 8, 16]
    ];

    private readonly byte[][] _compactEncoding =
    [
        [0x00],
        [0x20],
        [0x11, 0x23, 0x45],
        [0x00, 0x01, 0x23, 0x45],
        [0x3f, 0x1c, 0xb8],
        [0x20, 0x0f, 0x1c, 0xb8]
    ];

    [Test]
    public void CompactDecodingTest()
    {
        for (int i = 0; i < _compactEncoding.Length; i++)
        {
            byte[]? encoded = _compactEncoding[i];
            Assert.That(Nibbles.CompactToHexEncode(encoded), Is.EqualTo(_hexEncoding[i]));
        }
    }

    [TestCase(new byte[] { 0x9C }, new byte[] { 0x09, 0x0C })]
    [TestCase(new byte[] { 0x12, 0x34, 0x56, 0x78 }, new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 })]
    public void BytesToNibbleBytes_SmallInput_ProducesCorrectOutput(byte[] input, byte[] expected)
    {
        byte[] result = Nibbles.BytesToNibbleBytes(input);

        Assert.That(result, Is.EqualTo(expected));
    }

    // [] is a null-reference span, so a write for a zero length faults here instead of corrupting the heap.
    [Test]
    public void BytesToNibbleBytes_EmptyInput_ReturnsEmpty() =>
        Assert.That(Nibbles.BytesToNibbleBytes([]), Is.Empty);

    [Test]
    public void BytesToNibbleBytes_Vector128Size_ProducesCorrectOutput()
    {
        // Test with 16 bytes - exactly one Vector128 chunk
        byte[] input = new byte[16];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = (byte)i;
        }

        byte[] result = Nibbles.BytesToNibbleBytes(input);

        // Verify each byte was correctly split into two nibbles
        for (int i = 0; i < input.Length; i++)
        {
            Assert.That(result[i * 2], Is.EqualTo((byte)(input[i] >> 4)), $"high nibble at position {i}");
            Assert.That(result[i * 2 + 1], Is.EqualTo((byte)(input[i] & 0x0F)), $"low nibble at position {i}");
        }
    }

    [Test]
    public void BytesToNibbleBytes_LargerThanVector128_ProducesCorrectOutput()
    {
        // 33 bytes: on the 128-bit path, blocks at 0 and 16, then a final block at 17
        // Distinct values in each chunk expose a block read from the wrong offset
        byte[] input = new byte[33];
        for (int i = 0; i < input.Length; i++)
        {
            // Use pattern: first 16 bytes = 0xA0-0xAF, next 16 = 0xB0-0xBF, last = 0xC0
            if (i < 16)
                input[i] = (byte)(0xA0 + i);
            else if (i < 32)
                input[i] = (byte)(0xB0 + (i - 16));
            else
                input[i] = 0xC0;
        }

        byte[] result = Nibbles.BytesToNibbleBytes(input);

        Assert.That(result.Length, Is.EqualTo(66));

        // Verify each byte was correctly split into two nibbles
        for (int i = 0; i < input.Length; i++)
        {
            byte expectedHigh = (byte)(input[i] >> 4);
            byte expectedLow = (byte)(input[i] & 0x0F);

            Assert.That(result[i * 2], Is.EqualTo(expectedHigh), $"high nibble at position {i} (byte value 0x{input[i]:X2})");
            Assert.That(result[i * 2 + 1], Is.EqualTo(expectedLow), $"low nibble at position {i} (byte value 0x{input[i]:X2})");
        }
    }

    [Test]
    public void BytesToNibbleBytes_Vector256Size_ProducesCorrectOutput()
    {
        // Test with 32 bytes - exactly one Vector256 chunk (on AVX2 systems)
        byte[] input = new byte[32];
        for (int i = 0; i < input.Length; i++)
        {
            // Use values 0x10-0x2F to make nibbles distinct and non-zero
            input[i] = (byte)(0x10 + i);
        }

        byte[] result = Nibbles.BytesToNibbleBytes(input);

        Assert.That(result.Length, Is.EqualTo(64));

        // Verify each byte was correctly split into two nibbles
        for (int i = 0; i < input.Length; i++)
        {
            Assert.That(result[i * 2], Is.EqualTo((byte)(input[i] >> 4)), $"high nibble at position {i}");
            Assert.That(result[i * 2 + 1], Is.EqualTo((byte)(input[i] & 0x0F)), $"low nibble at position {i}");
        }
    }

    [Test]
    public void BytesToNibbleBytes_LargerThanVector256_ProducesCorrectOutput()
    {
        // 65 bytes: on AVX2, blocks at 0 and 32, then a final block at 33
        // Distinct values in each chunk expose a block read from the wrong offset
        byte[] input = new byte[65];
        for (int i = 0; i < input.Length; i++)
        {
            // Use pattern: first 32 bytes = 0x00-0x1F, next 32 = 0x20-0x3F, last = 0x40
            if (i < 32)
                input[i] = (byte)i;
            else if (i < 64)
                input[i] = (byte)(0x20 + (i - 32));
            else
                input[i] = 0x40;
        }

        byte[] result = Nibbles.BytesToNibbleBytes(input);

        Assert.That(result.Length, Is.EqualTo(130));

        // Verify each byte was correctly split into two nibbles
        for (int i = 0; i < input.Length; i++)
        {
            byte expectedHigh = (byte)(input[i] >> 4);
            byte expectedLow = (byte)(input[i] & 0x0F);

            Assert.That(result[i * 2], Is.EqualTo(expectedHigh), $"high nibble at position {i} (byte value 0x{input[i]:X2})");
            Assert.That(result[i * 2 + 1], Is.EqualTo(expectedLow), $"low nibble at position {i} (byte value 0x{input[i]:X2})");
        }
    }

    [Test]
    public void BytesToNibbleBytes_Vector256OverlappingTail_ProducesCorrectOutput()
    {
        // 80 bytes: on AVX2, blocks at 0 and 32, then a final block at 48 that overlaps the second
        byte[] input = new byte[80];
        for (int i = 0; i < input.Length; i++)
        {
            // Each byte has a unique pattern so we can detect misread data
            input[i] = (byte)(i % 256);
        }

        byte[] result = Nibbles.BytesToNibbleBytes(input);

        Assert.That(result.Length, Is.EqualTo(160));

        // Verify each byte was correctly split into two nibbles
        for (int i = 0; i < input.Length; i++)
        {
            byte expectedHigh = (byte)(input[i] >> 4);
            byte expectedLow = (byte)(input[i] & 0x0F);

            Assert.That(result[i * 2], Is.EqualTo(expectedHigh), $"high nibble at position {i} (byte value 0x{input[i]:X2}, expected from input[{i}])");
            Assert.That(result[i * 2 + 1], Is.EqualTo(expectedLow), $"low nibble at position {i} (byte value 0x{input[i]:X2}, expected from input[{i}])");
        }
    }

    [Test]
    public void BytesToNibbleBytes_MultipleVector128Iterations_ProducesCorrectOutput()
    {
        // 48 bytes: on the 128-bit path, blocks at 0, 16 and 32
        byte[] input = new byte[48];
        for (int i = 0; i < input.Length; i++)
        {
            // Each 16-byte chunk has distinct values
            input[i] = (byte)((i / 16) * 64 + (i % 16));
        }

        byte[] result = Nibbles.BytesToNibbleBytes(input);

        Assert.That(result.Length, Is.EqualTo(96));

        // Verify each byte was correctly split
        for (int i = 0; i < input.Length; i++)
        {
            byte expectedHigh = (byte)(input[i] >> 4);
            byte expectedLow = (byte)(input[i] & 0x0F);

            Assert.That(result[i * 2], Is.EqualTo(expectedHigh), $"high nibble at position {i} (byte value 0x{input[i]:X2})");
            Assert.That(result[i * 2 + 1], Is.EqualTo(expectedLow), $"low nibble at position {i} (byte value 0x{input[i]:X2})");
        }
    }

    [Test]
    public void BytesToNibbleBytes_LargeInput_ProducesCorrectOutput()
    {
        // Test with 128 bytes - ensures multiple iterations of vector code
        // and tests boundary conditions
        byte[] input = new byte[128];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = (byte)(i % 256);
        }

        byte[] result = Nibbles.BytesToNibbleBytes(input);

        Assert.That(result.Length, Is.EqualTo(256));

        // Verify each byte was correctly split into two nibbles
        for (int i = 0; i < input.Length; i++)
        {
            Assert.That(result[i * 2], Is.EqualTo((byte)(input[i] >> 4)), $"high nibble at position {i}");
            Assert.That(result[i * 2 + 1], Is.EqualTo((byte)(input[i] & 0x0F)), $"low nibble at position {i}");
        }
    }

    [Test]
    public void BytesToNibbleBytes_AllByteValues_ProducesCorrectOutput()
    {
        // Test with all possible byte values to ensure correctness
        byte[] input = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            input[i] = (byte)i;
        }

        byte[] result = Nibbles.BytesToNibbleBytes(input);

        Assert.That(result.Length, Is.EqualTo(512));

        // Verify each byte was correctly split into two nibbles
        for (int i = 0; i < input.Length; i++)
        {
            Assert.That(result[i * 2], Is.EqualTo((byte)(input[i] >> 4)), $"high nibble at position {i}");
            Assert.That(result[i * 2 + 1], Is.EqualTo((byte)(input[i] & 0x0F)), $"low nibble at position {i}");
        }
    }
}
