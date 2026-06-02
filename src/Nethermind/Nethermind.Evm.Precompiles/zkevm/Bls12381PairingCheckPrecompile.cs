// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Zkvm.Abstractions;

namespace Nethermind.Evm.Precompiles;

public partial class Bls12381PairingCheckPrecompile
{
    private const int StackallocPairCountThreshold = 4;

    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec _)
    {
        if (!ValidateInputLength(inputData))
            return Errors.InvalidInputLength;

        int pairCount = inputData.Length / PairSize;
        int decodedLen = (Eip2537.LenG1Trimmed + Eip2537.LenG2Trimmed) * pairCount;

        if (pairCount <= StackallocPairCountThreshold)
        {
            Span<byte> decoded = stackalloc byte[decodedLen];

            return RunInternal(inputData.Span, decoded, pairCount);
        }
        else
        {
            byte[] decoded = ArrayPool<byte>.Shared.Rent(decodedLen);

            try // Is this really needed on zkVM?
            {
                return RunInternal(inputData.Span, decoded, pairCount);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(decoded);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Result<byte[]> RunInternal(ReadOnlySpan<byte> input, Span<byte> decoded, int pairCount)
    {
        int srcOffset = 0;
        int dstOffset = 0;

        for (int i = 0; i < pairCount; i++)
        {
            if (!Eip2537.TryDecodeG1(input.Slice(srcOffset, Eip2537.LenG1), decoded.Slice(dstOffset, Eip2537.LenG1Trimmed)))
                return Errors.InvalidFieldElementTopBytes;

            srcOffset += Eip2537.LenG1;
            dstOffset += Eip2537.LenG1Trimmed;

            if (!Eip2537.TryDecodeG2(input.Slice(srcOffset, Eip2537.LenG2), decoded.Slice(dstOffset, Eip2537.LenG2Trimmed)))
                return Errors.InvalidFieldElementTopBytes;

            srcOffset += Eip2537.LenG2;
            dstOffset += Eip2537.LenG2Trimmed;
        }

        Accelerators.Status status = Accelerators.Bls12381Pairing(decoded, (nuint)pairCount, out bool verified);

        if (status == Accelerators.Status.OK)
        {
            byte[] output = new byte[32];

            if (verified)
                output[31] = 1;

            return output;
        }

        return Errors.Failed;
    }
}
