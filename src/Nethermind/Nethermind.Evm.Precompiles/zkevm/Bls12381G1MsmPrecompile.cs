// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles;

public partial class Bls12381G1MsmPrecompile
{
    private const int PairLength = Eip2537.LenG1 + Eip2537.LenFr;
    private const int TrimmedPairLength = Eip2537.LenG1Trimmed + Eip2537.LenFr;
    private const int StackallocPairCountThreshold = 8;

    public Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        if (!ValidateInputLength(inputData))
            return Errors.InvalidInputLength;

        int pairCount = inputData.Length / ItemSize;

        return pairCount == 1 ? Mul(inputData.Span) : Msm(inputData.Span, pairCount);
    }

    [SkipLocalsInit]
    private static Result<byte[]> Msm(ReadOnlySpan<byte> inputData, int pairCount)
    {
        int inputLength = pairCount * TrimmedPairLength;

        if (pairCount <= StackallocPairCountThreshold)
        {
            Span<byte> input = stackalloc byte[inputLength];

            if (!TryDecodeInput(inputData, input, pairCount))
                return Errors.InvalidFieldElementTopBytes;

            Span<byte> output = stackalloc byte[Eip2537.LenG1Trimmed];

            byte status = ZiskBindings.Crypto.bls12_381_g1_msm_c(output, input, (nuint)pairCount);

            return HandleResult(output, status);
        }

        byte[] inputBuffer = ArrayPool<byte>.Shared.Rent(inputLength);

        try
        {
            Span<byte> input = inputBuffer.AsSpan(0, inputLength);

            if (!TryDecodeInput(inputData, input, pairCount))
                return Errors.InvalidFieldElementTopBytes;

            Span<byte> output = stackalloc byte[Eip2537.LenG1Trimmed];

            byte status = ZiskBindings.Crypto.bls12_381_g1_msm_c(output, input, (nuint)pairCount);

            return HandleResult(output, status);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(inputBuffer);
        }
    }

    [SkipLocalsInit]
    private static Result<byte[]> Mul(ReadOnlySpan<byte> inputData)
    {
        Span<byte> input = stackalloc byte[Eip2537.LenG1Trimmed + Eip2537.LenFr];

        if (!Eip2537.TryDecodeG1(inputData[..Eip2537.LenG1], input[..Eip2537.LenG1Trimmed]))
            return Errors.InvalidFieldElementTopBytes;

        inputData[Eip2537.LenG1..].CopyTo(input[Eip2537.LenG1Trimmed..]);

        Span<byte> output = stackalloc byte[Eip2537.LenG1Trimmed];

        byte status = ZiskBindings.Crypto.bls12_381_g1_msm_c(output, input, (nuint)1);

        return HandleResult(output, status);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryDecodeInput(ReadOnlySpan<byte> inputData, Span<byte> packedInput, int pairCount)
    {
        int srcOffset = 0;
        int dstOffset = 0;

        for (int i = 0; i < pairCount; i++)
        {
            if (!Eip2537.TryDecodeG1(inputData.Slice(srcOffset, Eip2537.LenG1), packedInput.Slice(dstOffset, Eip2537.LenG1Trimmed)))
                return false;

            inputData.Slice(srcOffset + Eip2537.LenG1, Eip2537.LenFr)
                .CopyTo(packedInput.Slice(dstOffset + Eip2537.LenG1Trimmed, Eip2537.LenFr));

            srcOffset += PairLength;
            dstOffset += TrimmedPairLength;
        }

        return true;
    }

    private static Result<byte[]> HandleResult(Span<byte> output, byte status)
    {
        if (status <= 1)
        {
            byte[] outputData = new byte[Eip2537.LenG1];

            if (status == 0)
                Eip2537.EncodeG1(output, outputData);

            return outputData;
        }

        if (status == 3 && !Eip2537.DisableSubgroupChecks)
            return Errors.G1PointSubgroup;

        return Errors.Failed;
    }
}
