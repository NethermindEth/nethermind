// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#pragma warning disable NETH003 // Build variant: only one of ModularArithmetic.std.cs / ModularArithmetic.zkevm.cs is compiled per build

using Nethermind.Int256;

namespace Nethermind.Evm;

/// <summary>Modular arithmetic behind the three-parameter EVM instructions.</summary>
/// <remarks>
/// MULMOD is the one EVM operation a 256-bit software path cannot do cheaply: a full 512-bit product
/// followed by a 512-by-256 division. A guest whose zkVM carries it as a single accelerator hands that to
/// <see cref="TryRegisterAccelerator"/> - the same trade the other precompiles already make for keccak and
/// secp256k1. The guest binds it rather than this assembly, which every guest links while only ZisK has
/// the instruction.
/// </remarks>
public static unsafe class ModularArithmetic
{
    private static delegate*<in UInt256, in UInt256, in UInt256, out UInt256, void> _accelerator;

    /// <inheritdoc cref="UInt256.MultiplyMod(in UInt256, in UInt256, in UInt256, out UInt256)"/>
    public static void MultiplyMod(in UInt256 a, in UInt256 b, in UInt256 m, out UInt256 result)
    {
        // The EVM defines a zero modulus as a zero result, where the accelerator has no answer and the
        // software path throws. `Math3ParamCore` never asks - it leaves a zero modulus on the stack
        // untouched - so this only keeps the helper total for callers that do.
        if (m.IsZero)
        {
            result = default;
            return;
        }

        if (_accelerator is null)
        {
            UInt256.MultiplyMod(in a, in b, in m, out result);
            return;
        }

        _accelerator(in a, in b, in m, out result);
    }

    /// <summary>Routes <see cref="MultiplyMod"/> through a zkVM accelerator if it answers a known case correctly.</summary>
    /// <param name="accelerator">Computes <c>(a * b) mod m</c> for a non-zero <c>m</c>.</param>
    /// <returns><see langword="true"/> if the accelerator now serves MULMOD; otherwise the software path keeps it.</returns>
    /// <remarks>
    /// Checks a case whose answer is fixed rather than only checking that the call returns: a binding that
    /// resolved to the wrong routine would otherwise go unnoticed until a block produced a wrong root.
    /// </remarks>
    public static bool TryRegisterAccelerator(delegate*<in UInt256, in UInt256, in UInt256, out UInt256, void> accelerator)
    {
        UInt256 a = 7;
        UInt256 b = 11;
        UInt256 m = 13;
        accelerator(in a, in b, in m, out UInt256 probe);

        // 7 * 11 = 77, and 77 mod 13 is 12.
        if (probe != (UInt256)12)
            return false;

        _accelerator = accelerator;
        return true;
    }
}
