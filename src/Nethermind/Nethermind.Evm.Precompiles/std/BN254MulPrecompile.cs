// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;

namespace Nethermind.Evm.Precompiles;

public partial class BN254MulPrecompile
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Mul(ReadOnlySpan<byte> input, byte[] output) => BN254.Mul(output, input);
}
