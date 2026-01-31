// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

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
            Nibbles.CompactToHexEncode(encoded).Should().BeEquivalentTo(_hexEncoding[i]);
        }
    }

    [Test]
    public void BytesToNibbleBytes_SmallInput_ProducesCorrectOutput()
    {
        // Test with small input that doesn't trigger vector paths
        byte[] input = [0x12, 0x34, 0x56, 0x78];
        byte[] expected = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];

        byte[] result = Nibbles.BytesToNibbleBytes(input);

        result.Should().BeEquivalentTo(expected);
    }

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
            result[i * 2].Should().Be((byte)(input[i] >> 4), $"high nibble at position {i}");
            result[i * 2 + 1].Should().Be((byte)(input[i] & 0x0F), $"low nibble at position {i}");
        }
    }

    [Test]
    public void BytesToNibbleBytes_LargerThanVector128_ProducesCorrectOutput()
    {
        // Test with 33 bytes - triggers multiple Vector128 iterations
        // This would have caught the out-of-bounds bug where second iteration
        // read from bytes[0] instead of bytes[processed]
        // Use distinct values in each chunk to detect re-reading
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

        result.Length.Should().Be(66);

        // Verify each byte was correctly split into two nibbles
        // With the bug, bytes 16-31 would have nibbles from bytes 0-15 instead
        for (int i = 0; i < input.Length; i++)
        {
            byte expectedHigh = (byte)(input[i] >> 4);
            byte expectedLow = (byte)(input[i] & 0x0F);

            result[i * 2].Should().Be(expectedHigh, $"high nibble at position {i} (byte value 0x{input[i]:X2})");
            result[i * 2 + 1].Should().Be(expectedLow, $"low nibble at position {i} (byte value 0x{input[i]:X2})");
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

        result.Length.Should().Be(64);

        // Verify each byte was correctly split into two nibbles
        for (int i = 0; i < input.Length; i++)
        {
            result[i * 2].Should().Be((byte)(input[i] >> 4), $"high nibble at position {i}");
            result[i * 2 + 1].Should().Be((byte)(input[i] & 0x0F), $"low nibble at position {i}");
        }
    }

    [Test]
    public void BytesToNibbleBytes_LargerThanVector256_ProducesCorrectOutput()
    {
        // Test with 65 bytes - triggers multiple Vector256 iterations on AVX2 systems
        // This would have caught the out-of-bounds bug where second iteration
        // read from bytes[0] instead of bytes[processed]
        // Use distinct values in each chunk to detect re-reading
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

        result.Length.Should().Be(130);

        // Verify each byte was correctly split into two nibbles
        // With the bug, bytes 32-63 would have nibbles from bytes 0-31 instead
        for (int i = 0; i < input.Length; i++)
        {
            byte expectedHigh = (byte)(input[i] >> 4);
            byte expectedLow = (byte)(input[i] & 0x0F);

            result[i * 2].Should().Be(expectedHigh, $"high nibble at position {i} (byte value 0x{input[i]:X2})");
            result[i * 2 + 1].Should().Be(expectedLow, $"low nibble at position {i} (byte value 0x{input[i]:X2})");
        }
    }

    [Test]
    public void BytesToNibbleBytes_Vector256ThenVector128_ProducesCorrectOutput()
    {
        // Test with 80 bytes - on AVX2 systems this triggers:
        // - Vector256 processes 64 bytes (2 iterations of 32 bytes)
        // - Vector128 processes remaining 16 bytes
        // With the bug, Vector128 would read from bytes[0] instead of bytes[64]
        byte[] input = new byte[80];
        for (int i = 0; i < input.Length; i++)
        {
            // Each byte has a unique pattern so we can detect misread data
            input[i] = (byte)(i % 256);
        }

        byte[] result = Nibbles.BytesToNibbleBytes(input);

        result.Length.Should().Be(160);

        // Verify each byte was correctly split into two nibbles
        // With the bug, bytes 64-79 would show nibbles from bytes 0-15
        for (int i = 0; i < input.Length; i++)
        {
            byte expectedHigh = (byte)(input[i] >> 4);
            byte expectedLow = (byte)(input[i] & 0x0F);

            result[i * 2].Should().Be(expectedHigh,
                $"high nibble at position {i} (byte value 0x{input[i]:X2}, expected from input[{i}])");
            result[i * 2 + 1].Should().Be(expectedLow,
                $"low nibble at position {i} (byte value 0x{input[i]:X2}, expected from input[{i}])");
        }
    }

    [Test]
    public void BytesToNibbleBytes_MultipleVector128Iterations_ProducesCorrectOutput()
    {
        // Test with 48 bytes - triggers 3 Vector128 iterations (16 bytes each)
        // With the bug, second and third iterations would read from wrong offset
        byte[] input = new byte[48];
        for (int i = 0; i < input.Length; i++)
        {
            // Each 16-byte chunk has distinct values
            input[i] = (byte)((i / 16) * 64 + (i % 16));
        }

        byte[] result = Nibbles.BytesToNibbleBytes(input);

        result.Length.Should().Be(96);

        // Verify each byte was correctly split
        for (int i = 0; i < input.Length; i++)
        {
            byte expectedHigh = (byte)(input[i] >> 4);
            byte expectedLow = (byte)(input[i] & 0x0F);

            result[i * 2].Should().Be(expectedHigh,
                $"high nibble at position {i} (byte value 0x{input[i]:X2})");
            result[i * 2 + 1].Should().Be(expectedLow,
                $"low nibble at position {i} (byte value 0x{input[i]:X2})");
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

        result.Length.Should().Be(256);

        // Verify each byte was correctly split into two nibbles
        for (int i = 0; i < input.Length; i++)
        {
            result[i * 2].Should().Be((byte)(input[i] >> 4), $"high nibble at position {i}");
            result[i * 2 + 1].Should().Be((byte)(input[i] & 0x0F), $"low nibble at position {i}");
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

        result.Length.Should().Be(512);

        // Verify each byte was correctly split into two nibbles
        for (int i = 0; i < input.Length; i++)
        {
            result[i * 2].Should().Be((byte)(input[i] >> 4), $"high nibble at position {i}");
            result[i * 2 + 1].Should().Be((byte)(input[i] & 0x0F), $"low nibble at position {i}");
        }
    }
}
