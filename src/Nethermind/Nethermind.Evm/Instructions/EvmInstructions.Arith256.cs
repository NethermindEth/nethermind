// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm;

using Int256;

/// <remarks>
/// The 256-bit division and modular arithmetic behind DIV, MOD, ADDMOD and MULMOD, split per target.
/// The standard build is the <see cref="UInt256"/> routine each opcode always called. The zkEVM build
/// hands the operation to ZisK's native 256-bit arithmetic when the ZisK guest switches on
/// <c>ZiskArith256Flag</c>, because in software a MULMOD alone runs about 1,800 guest steps.
/// Every caller has already taken the opcode's zero-divisor or zero-modulus branch, so none of these
/// sees a zero <c>b</c> or <c>m</c>. See <c>EvmInstructions.Arith256.std.cs</c> and <c>.zkevm.cs</c>.
/// </remarks>
public static partial class EvmInstructions
{
    /// <summary>Computes <c>(a + b) mod m</c> over the full 257-bit sum, for a non-zero <paramref name="m"/>.</summary>
    private static partial void AddMod256(in UInt256 a, in UInt256 b, in UInt256 m, out UInt256 result);

    /// <summary>Computes <c>(a * b) mod m</c> over the full 512-bit product, for a non-zero <paramref name="m"/>.</summary>
    private static partial void MulMod256(in UInt256 a, in UInt256 b, in UInt256 m, out UInt256 result);

    /// <summary>Computes <c>a / b</c>, for a non-zero <paramref name="b"/>.</summary>
    private static partial void Divide256(in UInt256 a, in UInt256 b, out UInt256 result);

    /// <summary>Computes <c>a mod b</c>, for a <paramref name="b"/> above one.</summary>
    private static partial void Mod256(in UInt256 a, in UInt256 b, out UInt256 result);
}
