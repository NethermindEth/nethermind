// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Zkvm.Abstractions;

namespace Nethermind.Evm.Precompiles;

public partial class ModExpPrecompile
{
    public static Address Address => field ??= Address.FromNumber(5);

    private const uint ZiskModExpMaxOperandLength = 1056;

    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        if (!TryPrepareInput(
            inputData,
            releaseSpec,
            out ReadOnlySpan<byte> inputSpan,
            out uint baseLength,
            out uint expLength,
            out uint modulusLength,
            out Result<byte[]> errorOrEmpty))
        {
            return errorOrEmpty;
        }

        ulong expOffset = 96UL + baseLength;
        ulong modulusOffset = expOffset + expLength;
        uint expStart = expOffset > uint.MaxValue ? uint.MaxValue : (uint)expOffset;
        uint modulusStart = modulusOffset > uint.MaxValue ? uint.MaxValue : (uint)modulusOffset;

        ReadOnlySpan<byte> modulus = inputSpan.SliceWithZeroPaddingEmptyOnError(modulusStart, modulusLength);
        byte[] result = new byte[modulusLength];

        if (modulus.IsEmpty || modulus.IndexOfAnyExcept((byte)0) < 0)
            return result;

        ReadOnlySpan<byte> @base = inputSpan.SliceWithZeroPaddingEmptyOnError(96U, baseLength);
        ReadOnlySpan<byte> exp = inputSpan.SliceWithZeroPaddingEmptyOnError(expStart, expLength);

        // An empty base or exponent gives 0 or 1 (EIP-198: 0^0 = 1), where ZisK's routine runs the whole ladder. Only
        // the lengths are tested: a scan for zero bytes costs more than it saves on the common inputs.
        if (baseLength == 0 || expLength == 0)
        {
            if (exp.IsZero() && (modulus[^1] != 1 || !modulus[..^1].IsZero()))
                result[^1] = 1;

            return result;
        }

        // ZisK's routine takes operands of up to 1056 bytes (ziskasm/zisklib/zkvm/modexp.zisk), which covers
        // every input EIP-7823 allows; the standard accelerator keeps the longer pre-Osaka ones.
        if (ZiskFlag.IsActive && baseLength <= ZiskModExpMaxOperandLength && expLength <= ZiskModExpMaxOperandLength && modulusLength <= ZiskModExpMaxOperandLength)
            Accelerators.ZiskModExp(@base, exp, modulus, result);
        else
            Accelerators.ModExp(@base, exp, modulus, result);

        return result;
    }
}
