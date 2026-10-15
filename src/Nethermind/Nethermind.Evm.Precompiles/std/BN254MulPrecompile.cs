// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;

namespace Nethermind.Evm.Precompiles;

public partial class BN254MulPrecompile
{
    public static Address Address { get; } = Address.FromNumber(7);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string? Mul(ReadOnlySpan<byte> input, byte[] output) => BN254.Mul(output, input);

    partial void CountCall() => Metrics.Bn254MulPrecompile++;
}
