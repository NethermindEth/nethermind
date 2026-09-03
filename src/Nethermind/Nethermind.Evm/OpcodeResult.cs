// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Evm;

/// <summary>An opcode handler's outcome: the program counter it selected, packed with its halting status.</summary>
/// <remarks>
/// The counter occupies the low 32 bits and the <see cref="EvmExceptionType"/> the high 32. Taking the
/// counter by reference instead forces it into an address-taken stack slot for as long as the body runs;
/// returning it keeps it in a register. One <see cref="ulong"/> wide because a 16-byte struct returns
/// through memory on the Windows x64 ABI.
/// </remarks>
public readonly struct OpcodeResult
{
    /// <summary>The <see cref="ProgramCounter"/> of a handler that leaves the counter to the caller.</summary>
    /// <remarks>
    /// Most handlers only step over their own opcode, so they return a bare <see cref="EvmExceptionType"/>
    /// and the dispatch keeps the counter it already holds. EIP-170 caps code at 24 KiB and EIP-3860 caps
    /// init code at 48 KiB, so no counter a handler can select reaches this value.
    /// </remarks>
    internal const uint NoProgramCounter = 0x8000_0000;

    private readonly ulong _packed;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public OpcodeResult(nint programCounter, EvmExceptionType exception)
        => _packed = ((ulong)(uint)exception << 32) | (uint)programCounter;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private OpcodeResult(ulong packed) => _packed = packed;

    /// <summary>The counter the handler selected, or <see cref="NoProgramCounter"/> if it selected none.</summary>
    public nint ProgramCounter
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (nint)(uint)_packed;
    }

    /// <summary>Why the handler halted, or <see cref="EvmExceptionType.None"/> if it did not.</summary>
    public EvmExceptionType Exception
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (EvmExceptionType)(uint)(_packed >> 32);
    }

    /// <summary>Wraps a status from a handler that leaves the program counter to the caller.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator OpcodeResult(EvmExceptionType exception)
        => new(((ulong)(uint)exception << 32) | NoProgramCounter);

    /// <summary>Wraps a counter from a handler that moved it and did not halt.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator OpcodeResult(nint programCounter)
        => new(((ulong)(uint)EvmExceptionType.None << 32) | (uint)programCounter);
}
