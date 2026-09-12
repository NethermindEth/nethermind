// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Evm.CodeAnalysis;

/// <summary>
/// Matches a minimal proxy: a fixed-length runtime that forwards the whole calldata to a hard-coded
/// address by DELEGATECALL with all available gas, then returns or bubbles the revert verbatim.
/// </summary>
/// <remarks>
/// <para>
/// Two runtimes are recognized, both built only from opcodes available since Byzantium: the canonical
/// EIP-1167 clone, and the shorter "0age" variant. They differ in how many words the preamble leaves
/// beneath the call's operands and in what the wrapper opcodes cost, so each carries its own descriptor.
/// </para>
/// <para>
/// The PUSH0-based variants (ERC-7511, Solady) are deliberately not matched: PUSH0 only exists from
/// Shanghai, and a template is recognized per code hash without reference to a fork, so accepting one
/// would require fork-aware recognition rather than the fork check the caller applies.
/// </para>
/// </remarks>
internal sealed class MinimalProxy
{
    private readonly byte[] _prefix;
    private readonly byte[] _suffix;

    private MinimalProxy(byte[] prefix, byte[] suffix, ulong gasBeforeCall, ulong gasAfterCall, int leadingWords)
    {
        _prefix = prefix;
        _suffix = suffix;
        GasBeforeCall = gasBeforeCall;
        GasAfterCall = gasAfterCall;
        LeadingWords = leadingWords;
    }

    /// <summary>
    /// Gas consumed by the wrapper opcodes that run before DELEGATECALL, excluding CALLDATACOPY.
    /// </summary>
    /// <remarks>
    /// CALLDATACOPY is left out entirely — base cost included — because the caller charges it through the
    /// shared data-copy helper, which already covers that base.
    /// </remarks>
    public ulong GasBeforeCall { get; }

    /// <summary>
    /// Gas consumed by the wrapper opcodes that run after DELEGATECALL, excluding RETURNDATACOPY.
    /// </summary>
    /// <remarks>
    /// RETURNDATACOPY is left out on the same terms as CALLDATACOPY in <see cref="GasBeforeCall"/>; the
    /// trailing JUMPDEST/RETURN and REVERT are charged by <see cref="GasOnSuccess"/> and
    /// <see cref="GasOnFailure"/>.
    /// </remarks>
    public ulong GasAfterCall { get; }

    /// <summary>
    /// Words the preamble leaves on the stack beneath DELEGATECALL's six operands, which the trailing
    /// RETURN and REVERT read as their offset and size.
    /// </summary>
    public int LeadingWords { get; }

    /// <summary>Offset of the DELEGATECALL, which is always the byte after the 20-byte target.</summary>
    /// <remarks>
    /// The fast path leaves the program counter here so the ordinary opcode handler performs the call with
    /// the fork's own access, delegation and gas-reservation rules.
    /// </remarks>
    public int DelegateCallProgramCounter => _prefix.Length + Address.Size + 1;

    /// <summary>JUMPDEST 1, then RETURN, which is free beyond the memory expansion already charged.</summary>
    public const ulong GasOnSuccess = GasCostOf.JumpDest;

    /// <summary>REVERT is free beyond the memory expansion already charged.</summary>
    public const ulong GasOnFailure = GasCostOf.Free;

    /// <summary>
    /// EIP-1167, 45 bytes: CALLDATASIZE RETURNDATASIZE RETURNDATASIZE CALLDATACOPY, three RETURNDATASIZE,
    /// CALLDATASIZE RETURNDATASIZE PUSH20 &lt;target&gt; GAS DELEGATECALL, then RETURNDATASIZE DUP3 DUP1
    /// RETURNDATACOPY SWAP1 RETURNDATASIZE SWAP2 PUSH1 0x2b JUMPI REVERT JUMPDEST RETURN.
    /// </summary>
    private static readonly MinimalProxy Eip1167 = new(
        prefix: [0x36, 0x3d, 0x3d, 0x37, 0x3d, 0x3d, 0x3d, 0x36, 0x3d, 0x73],
        suffix: [0x5a, 0xf4, 0x3d, 0x82, 0x80, 0x3e, 0x90, 0x3d, 0x91, 0x60, 0x2b, 0x57, 0xfd, 0x5b, 0xf3],
        // Nine Base opcodes (CALLDATASIZE x2, RETURNDATASIZE x6, GAS) and the PUSH20.
        gasBeforeCall: GasCostOf.Base * 9 + GasCostOf.VeryLow,
        // RETURNDATASIZE x2, then DUP3, DUP1, SWAP1, SWAP2, PUSH1, and the JUMPI.
        gasAfterCall: GasCostOf.Base * 2 + GasCostOf.VeryLow * 5 + GasCostOf.JumpI,
        leadingWords: 1);

    /// <summary>
    /// The "0age" variant, 44 bytes: four RETURNDATASIZE, CALLDATASIZE RETURNDATASIZE RETURNDATASIZE
    /// CALLDATACOPY, CALLDATASIZE RETURNDATASIZE PUSH20 &lt;target&gt; GAS DELEGATECALL, then
    /// RETURNDATASIZE RETURNDATASIZE SWAP4 DUP1 RETURNDATACOPY PUSH1 0x2a JUMPI REVERT JUMPDEST RETURN.
    /// </summary>
    private static readonly MinimalProxy Age = new(
        prefix: [0x3d, 0x3d, 0x3d, 0x3d, 0x36, 0x3d, 0x3d, 0x37, 0x36, 0x3d, 0x73],
        suffix: [0x5a, 0xf4, 0x3d, 0x3d, 0x93, 0x80, 0x3e, 0x60, 0x2a, 0x57, 0xfd, 0x5b, 0xf3],
        // Ten Base opcodes (RETURNDATASIZE x8, CALLDATASIZE x2) and the PUSH20.
        gasBeforeCall: GasCostOf.Base * 10 + GasCostOf.VeryLow,
        // RETURNDATASIZE x2, then SWAP4, DUP1, PUSH1, and the JUMPI.
        gasAfterCall: GasCostOf.Base * 2 + GasCostOf.VeryLow * 3 + GasCostOf.JumpI,
        leadingWords: 2);

    private static readonly MinimalProxy[] Variants = [Eip1167, Age];

    /// <summary>Returns the matched variant and its delegation target, or <see langword="false"/> when the code is not a proxy.</summary>
    public static bool TryMatch(ReadOnlySpan<byte> code, out MinimalProxy? variant, out Address? target)
    {
        foreach (MinimalProxy candidate in Variants)
        {
            int targetOffset = candidate._prefix.Length;
            if (code.Length != targetOffset + Address.Size + candidate._suffix.Length) continue;
            if (!code[..targetOffset].SequenceEqual(candidate._prefix)) continue;
            if (!code[(targetOffset + Address.Size)..].SequenceEqual(candidate._suffix)) continue;

            variant = candidate;
            target = new Address(code.Slice(targetOffset, Address.Size));
            return true;
        }

        variant = null;
        target = null;
        return false;
    }
}
