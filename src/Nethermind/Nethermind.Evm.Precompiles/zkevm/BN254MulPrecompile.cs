// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Zkvm.Abstractions;

namespace Nethermind.Evm.Precompiles;

public partial class BN254MulPrecompile
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Mul(ReadOnlySpan<byte> input, byte[] output) =>
        Accelerators.BN254G1Mul(
            input[..(InputLength - 32)],
            input[(InputLength - 32)..],
            output
        ) == Accelerators.Status.OK;
}
