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

        ReadOnlySpan<byte> @base = inputSpan.SliceWithZeroPaddingEmptyOnError(96, (int)baseLength);
        ReadOnlySpan<byte> exp = inputSpan.SliceWithZeroPaddingEmptyOnError(96 + (int)baseLength, (int)expLength);
        ReadOnlySpan<byte> modulus = inputSpan
            .SliceWithZeroPaddingEmptyOnError(96 + (int)baseLength + (int)expLength, (int)modulusLength);
        byte[] result = new byte[modulusLength];

        Accelerators.ModExp(@base, exp, modulus, result);

        return result;
    }
}
