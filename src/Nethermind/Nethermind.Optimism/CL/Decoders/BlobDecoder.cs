// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Optimism.CL;

public class BlobDecoder
{
    static public byte[] DecodeBlob(BlobSidecar blobSidecar)
    {
        int MaxBlobDataSize = (4 * 31 + 3) * 1024 - 4;
        int BlobSize = 4096 * 32;
        int length = ((int)blobSidecar.Blob[2] << 16) | ((int)blobSidecar.Blob[3] << 8) |
                     ((int)blobSidecar.Blob[4]);
        if (length > MaxBlobDataSize)
        {
            throw new Exception("Blob size is too big");
        }

        byte[] output = new byte[MaxBlobDataSize];
        for (int i = 0; i < 27; ++i)
        {
            output[i] = blobSidecar.Blob[i + 5];
        }

        byte[] encodedByte = new byte[4];
        int blobPos = 32;
        int outputPos = 28;

        encodedByte[0] = blobSidecar.Blob[0];
        for (int i = 1; i < 4; ++i)
        {
            (encodedByte[i], outputPos, blobPos) = DecodeFieldElement(blobSidecar.Blob, outputPos, blobPos, output);
        }

        outputPos = ReassembleBytes(outputPos, encodedByte, output);

        for (int i = 1; i < 1024 && outputPos < length; i++)
        {
            for (int j = 0; j < 4; j++)
            {
                (encodedByte[j], outputPos, blobPos) =
                    DecodeFieldElement(blobSidecar.Blob, outputPos, blobPos, output);
            }

            outputPos = ReassembleBytes(outputPos, encodedByte, output);
        }

        for (int i = length; i < MaxBlobDataSize; i++)
        {
            if (output[i] != 0)
            {
                throw new Exception("Wrong output");
            }
        }

        output = output[..length];
        for (; blobPos < BlobSize; blobPos++)
        {
            if (blobSidecar.Blob[blobPos] != 0)
            {
                throw new Exception("Blob excess data");
            }
        }

        return output;
    }

    static private (byte, int, int) DecodeFieldElement(byte[] blob, int outPos, int blobPos, byte[] output) {
        // two highest order bits of the first byte of each field element should always be 0
        if ((blob[blobPos] & 0b1100_0000) != 0) {
            // TODO: remove exception
            throw new Exception("Invalid field element");
        }

        for (int i = 0; i < 31; i++)
        {
            output[outPos + i] = blob[blobPos + i + 1];
        }

        return (blob[blobPos], outPos + 32, blobPos + 32);
    }

    static int ReassembleBytes(int outPos, byte[] encodedByte, byte[] output)
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
