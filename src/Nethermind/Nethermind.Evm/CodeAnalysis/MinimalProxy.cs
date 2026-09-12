// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.CodeAnalysis;

/// <summary>
/// Matches a minimal proxy: a fixed-length runtime that forwards the whole calldata to a hard-coded
/// address by DELEGATECALL with all available gas, then returns or bubbles the revert verbatim.
/// </summary>
/// <remarks>
/// <para>
/// Variants differ in how many words the preamble leaves beneath the call's operands, in what the wrapper
/// opcodes cost, and in what their return and revert tails run, so each carries its own descriptor.
/// </para>
/// <para>
/// Four runtimes are recognized: the canonical EIP-1167 clone, the shorter "0age" variant, and the
/// PUSH0-based ERC-7511 and Solady forwarders. Recognition itself stays fork-independent — the bytes are
/// the bytes — and each variant instead declares, through <see cref="IsEnabled"/>, the fork its skipped
/// opcodes need before the caller may use it.
/// </para>
/// </remarks>
internal sealed class MinimalProxy
{
    private readonly byte[] _prefix;
    private readonly byte[] _suffix;

    private MinimalProxy(
        byte[] prefix,
        byte[] suffix,
        ulong gasBeforeCall,
        ulong gasAfterCall,
        ulong gasOnSuccess,
        ulong gasOnFailure,
        int leadingWords,
        bool usesPush0)
    {
        _prefix = prefix;
        _suffix = suffix;
        GasBeforeCall = gasBeforeCall;
        GasAfterCall = gasAfterCall;
        GasOnSuccess = gasOnSuccess;
        GasOnFailure = gasOnFailure;
        LeadingWords = leadingWords;
        _usesPush0 = usesPush0;
    }

    private readonly bool _usesPush0;

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

    /// <summary>Gas for the tail the call's success path runs, beyond the memory expansion already charged.</summary>
    public ulong GasOnSuccess { get; }

    /// <summary>Gas for the tail the call's failure path runs, beyond the memory expansion already charged.</summary>
    public ulong GasOnFailure { get; }

    /// <summary>
    /// Whether the fork in force defines every opcode this variant's preamble skips.
    /// </summary>
    /// <remarks>
    /// All variants are built on RETURNDATASIZE, and some on PUSH0. Skipping an opcode a fork has yet to
    /// define would run code the dispatch loop must instead halt on as <see cref="EvmExceptionType.BadInstruction"/>.
    /// </remarks>
    public bool IsEnabled(IReleaseSpec spec) =>
        spec.ReturnDataOpcodesEnabled && (!_usesPush0 || spec.IncludePush0Instruction);

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
        // JUMPDEST, then a RETURN whose operands are already on the stack.
        gasOnSuccess: GasCostOf.JumpDest,
        gasOnFailure: GasCostOf.Free,
        leadingWords: 1,
        usesPush0: false);

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
        gasOnSuccess: GasCostOf.JumpDest,
        gasOnFailure: GasCostOf.Free,
        leadingWords: 2,
        usesPush0: false);

    /// <summary>
    /// ERC-7511, 44 bytes: CALLDATASIZE PUSH0 PUSH0 CALLDATACOPY, PUSH0 PUSH0 CALLDATASIZE PUSH0
    /// PUSH20 &lt;target&gt; GAS DELEGATECALL, then RETURNDATASIZE PUSH0 PUSH0 RETURNDATACOPY PUSH0
    /// RETURNDATASIZE SWAP2 PUSH1 0x2a JUMPI REVERT JUMPDEST RETURN.
    /// </summary>
    private static readonly MinimalProxy Erc7511 = new(
        prefix: [0x36, 0x5f, 0x5f, 0x37, 0x5f, 0x5f, 0x36, 0x5f, 0x73],
        suffix: [0x5a, 0xf4, 0x3d, 0x5f, 0x5f, 0x3e, 0x5f, 0x3d, 0x91, 0x60, 0x2a, 0x57, 0xfd, 0x5b, 0xf3],
        // Eight Base opcodes (CALLDATASIZE x2, PUSH0 x5, GAS) and the PUSH20.
        gasBeforeCall: GasCostOf.Base * 8 + GasCostOf.VeryLow,
        // RETURNDATASIZE x2 and PUSH0 x3, then SWAP2, PUSH1, and the JUMPI.
        gasAfterCall: GasCostOf.Base * 5 + GasCostOf.VeryLow * 2 + GasCostOf.JumpI,
        gasOnSuccess: GasCostOf.JumpDest,
        gasOnFailure: GasCostOf.Free,
        leadingWords: 0,
        usesPush0: true);

    /// <summary>
    /// The Solady PUSH0 forwarder, 45 bytes: PUSH0 PUSH0 CALLDATASIZE PUSH0 PUSH0 CALLDATACOPY,
    /// CALLDATASIZE PUSH0 PUSH20 &lt;target&gt; GAS DELEGATECALL, then RETURNDATASIZE PUSH0 PUSH0
    /// RETURNDATACOPY PUSH1 0x29 JUMPI, with RETURNDATASIZE PUSH0 on each of the revert and return tails.
    /// </summary>
    private static readonly MinimalProxy Solady = new(
        prefix: [0x5f, 0x5f, 0x36, 0x5f, 0x5f, 0x37, 0x36, 0x5f, 0x73],
        suffix: [0x5a, 0xf4, 0x3d, 0x5f, 0x5f, 0x3e, 0x60, 0x29, 0x57, 0x3d, 0x5f, 0xfd, 0x5b, 0x3d, 0x5f, 0xf3],
        // Eight Base opcodes (PUSH0 x5, CALLDATASIZE x2, GAS) and the PUSH20.
        gasBeforeCall: GasCostOf.Base * 8 + GasCostOf.VeryLow,
        // RETURNDATASIZE and PUSH0 x2, then the PUSH1 and the JUMPI.
        gasAfterCall: GasCostOf.Base * 3 + GasCostOf.VeryLow + GasCostOf.JumpI,
        // Unlike the others, both tails rebuild the RETURN and REVERT operands: JUMPDEST RETURNDATASIZE PUSH0.
        gasOnSuccess: GasCostOf.JumpDest + GasCostOf.Base * 2,
        gasOnFailure: GasCostOf.Base * 2,
        leadingWords: 0,
        usesPush0: true);

    private static readonly MinimalProxy[] Variants = [Eip1167, Age, Erc7511, Solady];

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
