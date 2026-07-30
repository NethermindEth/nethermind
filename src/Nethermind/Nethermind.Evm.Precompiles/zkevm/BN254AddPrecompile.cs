// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Zkvm.Abstractions;

namespace Nethermind.Evm.Precompiles;

public partial class BN254AddPrecompile
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Add(ReadOnlySpan<byte> input, byte[] output) =>
        Accelerators.BN254G1Add(
            input[..(InputLength / 2)],
            input[(InputLength / 2)..],
            output
        ) == Accelerators.Status.OK;
}
