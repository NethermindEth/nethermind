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
        Accelerators.ModExp(@base, exp, modulus, result);

        return result;
    }
}
