// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Zkvm.Abstractions;

namespace Nethermind.Evm.Precompiles;

public partial class SecP256r1Precompile
{
    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec _)
    {
        ReadOnlySpan<byte> input = inputData.Span;

        return input.Length == 160 && AreScalarsInRange(input.Slice(32, 64)) && Accelerators.SecP256r1Verify(
            input[..32], input[32..96], input[96..]
            ) ? _successResult : [];
    }

    /// <summary>
    /// Validates that the signature scalars <c>r</c> and <c>s</c> lie in <c>[1, n-1]</c> as required by EIP-7951.
    /// </summary>
    /// <remarks>
    /// Out-of-range scalars must yield an invalid result; additionally the zkVM secp256r1 accelerator computes a
    /// modular inverse over <c>n</c> that is undefined for such values and would abort the guest. The standard
    /// build performs the same range check inside the underlying verifier.
    /// </remarks>
    private static bool AreScalarsInRange(ReadOnlySpan<byte> signature)
    {
        UInt256 r = new(signature[..32], isBigEndian: true);
        UInt256 s = new(signature.Slice(32, 32), isBigEndian: true);
        return !r.IsZero && r < SecP256r1Curve.N && !s.IsZero && s < SecP256r1Curve.N;
    }
}
