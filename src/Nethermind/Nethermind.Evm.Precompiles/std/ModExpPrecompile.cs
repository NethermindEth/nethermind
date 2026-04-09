// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.GmpBindings;

namespace Nethermind.Evm.Precompiles;

public unsafe partial class ModExpPrecompile
{
    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        Metrics.ModExpPrecompile++;

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

        using var modulusInt = mpz_t.Create();

        ReadOnlySpan<byte> modulusDataSpan = inputSpan.SliceWithZeroPaddingEmptyOnError(96 + (int)baseLength + (int)expLength, (int)modulusLength);

        if (modulusDataSpan.Length > 0)
        {
            fixed (byte* modulusData = &MemoryMarshal.GetReference(modulusDataSpan))
                Gmp.mpz_import(modulusInt, modulusLength, 1, 1, 1, nuint.Zero, (nint)modulusData);
        }

        if (Gmp.mpz_sgn(modulusInt) == 0)
            return new byte[modulusLength];

        using var baseInt = mpz_t.Create();
        using var expInt = mpz_t.Create();
        using var powmResult = mpz_t.Create();

        ReadOnlySpan<byte> baseDataSpan = inputSpan.SliceWithZeroPaddingEmptyOnError(96, (int)baseLength);

        if (baseDataSpan.Length > 0)
        {
            fixed (byte* baseData = &MemoryMarshal.GetReference(baseDataSpan))
                Gmp.mpz_import(baseInt, baseLength, 1, 1, 1, nuint.Zero, (nint)baseData);
        }

        ReadOnlySpan<byte> expDataSpan = inputSpan.SliceWithZeroPaddingEmptyOnError(96 + (int)baseLength, (int)expLength);

        if (expDataSpan.Length > 0)
        {
            fixed (byte* expData = &MemoryMarshal.GetReference(expDataSpan))
                Gmp.mpz_import(expInt, expLength, 1, 1, 1, nuint.Zero, (nint)expData);
        }

        Gmp.mpz_powm(powmResult, baseInt, expInt, modulusInt);

        nint powmResultLen = (nint)(Gmp.mpz_sizeinbase(powmResult, 2) + 7) / 8;
        nint offset = (int)modulusLength - powmResultLen;

        byte[] result = new byte[modulusLength];
        fixed (byte* ptr = &MemoryMarshal.GetArrayDataReference(result))
            Gmp.mpz_export((nint)(ptr + offset), out _, 1, 1, 1, nuint.Zero, powmResult);

        return result;
    }
}
