// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Extensions;

namespace Nethermind.Optimism.CL.Decoding;

/// <summary>
/// https://specs.optimism.io/protocol/ecotone/derivation.html#blob-encoding
/// </summary>
public static class BlobDecoder
{
    public const int MaxBlobDataSize = (4 * 31 + 3) * 1024 - 4;

    private const int BlobSize = 4096 * 32;
    private const int EncodingVersion = 0;

    public static int DecodeBlob(ReadOnlySpan<byte> blob, Span<byte> output)
    {
        if (output.Length < MaxBlobDataSize)
        {
            throw new ArgumentException($"Output buffer is too small. Expected a buffer of at least {MaxBlobDataSize} but got {output.Length}");
        }

        if (blob[1] != EncodingVersion)
        {
            throw new FormatException($"Expected version {EncodingVersion}, got {blob[1]}");
        }

        int length = (blob[2] << 16) | (blob[3] << 8) | blob[4];
        if (length > MaxBlobDataSize)
        {
            throw new FormatException("Blob size is too big");
        }

        for (int i = 0; i < 27; ++i)
        {
            output[i] = blob[i + 5];
        }

        Span<byte> encodedByte = stackalloc byte[4];
        int blobPos = 32;
        int outputPos = 28;

        encodedByte[0] = blob[0];
        for (int i = 1; i < 4; ++i)
        {
            (encodedByte[i], outputPos, blobPos) = DecodeFieldElement(blob, outputPos, blobPos, output);
        }

        outputPos = ReassembleBytes(outputPos, encodedByte, output);

        for (int i = 1; i < 1024 && outputPos < length; i++)
        {
            for (int j = 0; j < 4; j++)
            {
                (encodedByte[j], outputPos, blobPos) = DecodeFieldElement(blob, outputPos, blobPos, output);
            }

            outputPos = ReassembleBytes(outputPos, encodedByte, output);
        }

        if (!output[length..].IsZero())
        {
            throw new FormatException("Wrong blob output");
        }

        if (!blob[blobPos..].IsZero())
        {
            throw new FormatException("Blob excess data");
        }

        return length;
    }

    private static (byte, int, int) DecodeFieldElement(ReadOnlySpan<byte> blob, int outPos, int blobPos, Span<byte> output)
    {
        // two highest order bits of the first byte of each field element should always be 0
        if ((blob[blobPos] & 0b1100_0000) != 0)
        {
            throw new FormatException("Invalid field element");
        }

        for (int i = 0; i < 31; i++)
        {
            output[outPos + i] = blob[blobPos + i + 1];
        }

        return (blob[blobPos], outPos + 32, blobPos + 32);
    }

    private static int ReassembleBytes(int outPos, ReadOnlySpan<byte> encodedByte, Span<byte> output)
    {
        outPos--; // account for fact that we don't output a 128th byte
        byte x = (byte)((encodedByte[0] & 0b0011_1111) | ((encodedByte[1] & 0b0011_0000) << 2));
        byte y = (byte)((encodedByte[1] & 0b0000_1111) | ((encodedByte[3] & 0b0000_1111) << 4));
        byte z = (byte)((encodedByte[2] & 0b0011_1111) | ((encodedByte[3] & 0b0011_0000) << 2));
        // put the re-assembled bytes in their appropriate output locations
        output[outPos - 32] = z;
        output[outPos - 32 * 2] = y;
        output[outPos - 32 * 3] = x;
        return outPos;
    }
}
