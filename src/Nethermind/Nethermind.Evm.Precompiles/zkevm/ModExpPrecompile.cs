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

        int modulusHead = modulus.IndexOfAnyExcept((byte)0);
        if (modulusHead < 0)
            return result;

        ReadOnlySpan<byte> @base = inputSpan.SliceWithZeroPaddingEmptyOnError(96U, baseLength);
        ReadOnlySpan<byte> exp = inputSpan.SliceWithZeroPaddingEmptyOnError(expStart, expLength);

        // An empty base or exponent, or a base equal to the modulus, gives 0 or 1 (EIP-198: 0^0 = 1), where ZisK's routine
        // runs the whole ladder. Empty operands are tested by length only: a scan for zero bytes costs more than it saves on
        // the common inputs.
        if (baseLength == 0 || expLength == 0 || IsModulus(@base, modulus[modulusHead..]))
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

    /// <summary>Whether the base equals the modulus, so that it is 0 modulo it as an empty base is.</summary>
    /// <remarks>
    /// ZisK's routine does not reduce the base before its ladder, so it squares the modulus through every bit of the
    /// exponent. The test stops at the first byte that rules it out.
    /// </remarks>
    /// <param name="modulus">The modulus without its leading zeros.</param>
    internal static bool IsModulus(ReadOnlySpan<byte> @base, ReadOnlySpan<byte> modulus)
    {
        int padding = @base.Length - modulus.Length;
        return padding >= 0 &&
            @base[0] == (padding == 0 ? modulus[0] : 0) &&
            @base[^1] == modulus[^1] &&
            @base[..padding].IndexOfAnyExcept((byte)0) < 0 &&
            @base.EndsWith(modulus);
    }
}
