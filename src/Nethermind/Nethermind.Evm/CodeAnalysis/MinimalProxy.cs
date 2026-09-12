// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Evm.CodeAnalysis;

/// <summary>
/// Matches the EIP-1167 minimal proxy: 45 bytes that forward the whole calldata to a fixed address by
/// DELEGATECALL with all available gas, then return or bubble the revert verbatim.
/// </summary>
/// <remarks>
/// Only the canonical EIP-1167 runtime is matched. Other forwarder shapes (the 44-byte "0age" variant,
/// clones-with-immutable-args) differ in stack layout and are left to the dispatch loop.
/// </remarks>
internal static class MinimalProxy
{
    /// <summary>Runtime length: 10-byte prefix, 20-byte target, 15-byte suffix.</summary>
    public const int CodeLength = 45;

    /// <summary>
    /// Offset of the DELEGATECALL, where the fast path leaves the program counter so the ordinary
    /// opcode handler performs the call with the fork's own access, delegation and gas-reservation rules.
    /// </summary>
    public const int DelegateCallProgramCounter = 31;

    private const int TargetOffset = 10;

    /// <summary>CALLDATASIZE, RETURNDATASIZE, RETURNDATASIZE, CALLDATACOPY, 3x RETURNDATASIZE, CALLDATASIZE, RETURNDATASIZE, PUSH20.</summary>
    private static ReadOnlySpan<byte> Prefix => [0x36, 0x3d, 0x3d, 0x37, 0x3d, 0x3d, 0x3d, 0x36, 0x3d, 0x73];

    /// <summary>GAS, DELEGATECALL, RETURNDATASIZE, DUP3, DUP1, RETURNDATACOPY, SWAP1, RETURNDATASIZE, SWAP2, PUSH1 0x2b, JUMPI, REVERT, JUMPDEST, RETURN.</summary>
    private static ReadOnlySpan<byte> Suffix => [0x5a, 0xf4, 0x3d, 0x82, 0x80, 0x3e, 0x90, 0x3d, 0x91, 0x60, 0x2b, 0x57, 0xfd, 0x5b, 0xf3];

    /// <summary>
    /// Gas consumed by the wrapper opcodes that run before DELEGATECALL, excluding CALLDATACOPY.
    /// </summary>
    /// <remarks>
    /// Nine <see cref="GasCostOf.Base"/> opcodes (CALLDATASIZE, RETURNDATASIZE x6, CALLDATASIZE, GAS)
    /// and the PUSH20. CALLDATACOPY is left out entirely — base cost included — because the caller
    /// charges it through the shared data-copy helper, which already covers that base.
    /// </remarks>
    public const ulong GasBeforeCall = GasCostOf.Base * 9 + GasCostOf.VeryLow;

    /// <summary>
    /// Gas consumed by the wrapper opcodes that run after DELEGATECALL, excluding RETURNDATACOPY.
    /// </summary>
    /// <remarks>
    /// Two <see cref="GasCostOf.Base"/> opcodes (RETURNDATASIZE x2), five <see cref="GasCostOf.VeryLow"/>
    /// ones (DUP3, DUP1, SWAP1, SWAP2, PUSH1) and the JUMPI. RETURNDATACOPY is left out on the same terms
    /// as CALLDATACOPY in <see cref="GasBeforeCall"/>; the trailing JUMPDEST/RETURN and REVERT are charged
    /// by <see cref="GasOnSuccess"/> and <see cref="GasOnFailure"/>.
    /// </remarks>
    public const ulong GasAfterCall = GasCostOf.Base * 2 + GasCostOf.VeryLow * 5 + GasCostOf.JumpI;

    /// <summary>JUMPDEST 1, then RETURN, which is free beyond the memory expansion already charged.</summary>
    public const ulong GasOnSuccess = GasCostOf.JumpDest;

    /// <summary>REVERT is free beyond the memory expansion already charged.</summary>
    public const ulong GasOnFailure = GasCostOf.Free;

    /// <summary>Returns the delegation target when <paramref name="code"/> is a canonical minimal proxy; otherwise <see langword="null"/>.</summary>
    public static Address? TryMatch(ReadOnlySpan<byte> code)
    {
        if (code.Length != CodeLength) return null;
        if (!code[..TargetOffset].SequenceEqual(Prefix)) return null;
        if (!code[(TargetOffset + Address.Size)..].SequenceEqual(Suffix)) return null;

        return new Address(code.Slice(TargetOffset, Address.Size));
    }
}
