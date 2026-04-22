// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles;

public partial class Bls12381G2MsmPrecompile
{
    private const int StackallocPairCountThreshold = 4;

    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec _)
    {
        if (!ValidateInputLength(inputData))
            return Errors.InvalidInputLength;

        int pairCount = inputData.Length / ItemSize;

        return pairCount == 1 ? Mul(inputData.Span) : Msm(inputData.Span, pairCount);
    }

    [SkipLocalsInit]
    private static Result<byte[]> Msm(ReadOnlySpan<byte> input, int pairCount)
    {
        int decodedLen = pairCount * (Eip2537.LenG2Trimmed + Eip2537.LenFr);

        if (pairCount <= StackallocPairCountThreshold)
        {
            Span<byte> decodedInput = stackalloc byte[decodedLen];

            return MsmCore(input, decodedInput, pairCount);
        }
        else
        {
            byte[] decodedInput = ArrayPool<byte>.Shared.Rent(decodedLen);

            try
            {
                return MsmCore(input, decodedInput, pairCount);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(decodedInput);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Result<byte[]> MsmCore(ReadOnlySpan<byte> input, Span<byte> decoded, int pairCount)
    {
        if (!TryDecodeInput(input, decoded, pairCount))
            return Errors.InvalidFieldElementTopBytes;

        Span<byte> output = stackalloc byte[Eip2537.LenG2Trimmed];

        byte status = ZiskBindings.Crypto.bls12_381_g2_msm_c(output, decoded, (nuint)pairCount);

        return HandleResult(output, status);
    }

    [SkipLocalsInit]
    private static Result<byte[]> Mul(ReadOnlySpan<byte> input)
    {
        Span<byte> decoded = stackalloc byte[Eip2537.LenG2Trimmed + Eip2537.LenFr];

        if (!Eip2537.TryDecodeG2(input[..Eip2537.LenG2], decoded[..Eip2537.LenG2Trimmed]))
            return Errors.InvalidFieldElementTopBytes;

        input[Eip2537.LenG2..].CopyTo(decoded[Eip2537.LenG2Trimmed..]);

        Span<byte> output = stackalloc byte[Eip2537.LenG2Trimmed];

        byte status = ZiskBindings.Crypto.bls12_381_g2_msm_c(output, decoded, 1);

        return HandleResult(output, status);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryDecodeInput(ReadOnlySpan<byte> input, Span<byte> decoded, int pairCount)
    {
        int srcOffset = 0;
        int dstOffset = 0;

        for (int i = 0; i < pairCount; i++)
        {
            if (!Eip2537.TryDecodeG2(input.Slice(srcOffset, Eip2537.LenG2), decoded.Slice(dstOffset, Eip2537.LenG2Trimmed)))
                return false;

            srcOffset += Eip2537.LenG2;
            dstOffset += Eip2537.LenG2Trimmed;

            input.Slice(srcOffset, Eip2537.LenFr).CopyTo(decoded.Slice(dstOffset, Eip2537.LenFr));

            srcOffset += Eip2537.LenFr;
            dstOffset += Eip2537.LenFr;
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Result<byte[]> HandleResult(Span<byte> output, byte status)
    {
        if (status <= 1)
        {
            byte[] encoded = new byte[Eip2537.LenG2];

            if (status == 0)
                Eip2537.EncodeG2(output, encoded);

            return encoded;
        }

        if (status == 3 && !Eip2537.DisableSubgroupChecks)
            return Errors.G2PointSubgroup;

        return Errors.Failed;
    }
}
