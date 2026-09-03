// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Evm;

/// <summary>An opcode handler's outcome: the program counter it selected, alongside its halting status.</summary>
/// <remarks>
/// Taking the counter by reference instead forces it into an address-taken stack slot for as long as the
/// body runs; returning it keeps it in a register. Two 4-byte fields rather than one packed <see cref="ulong"/>
/// so that the JIT tracks them separately: a handler that reports no counter stores a constant, and the
/// dispatch's test against it folds away. Eight bytes wide, so it still returns in a register.
/// </remarks>
[method: MethodImpl(MethodImplOptions.AggressiveInlining)]
public readonly struct OpcodeResult(nint programCounter, EvmExceptionType exception)
{
    /// <summary>The <see cref="ProgramCounter"/> of a handler that leaves the counter to the caller.</summary>
    /// <remarks>
    /// Most handlers only step over their own opcode, so they return a bare <see cref="EvmExceptionType"/>
    /// and the dispatch keeps the counter it already holds. Negative because a counter never is, which
    /// lets the dispatch test the sign rather than compare against a constant.
    /// </remarks>
    internal const int NoProgramCounter = -1;

    private readonly int _programCounter = (int)programCounter;
    private readonly EvmExceptionType _exception = exception;

    /// <summary>The counter the handler selected, or <see cref="NoProgramCounter"/> if it selected none.</summary>
    public nint ProgramCounter
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _programCounter;
    }

    /// <summary>Why the handler halted, or <see cref="EvmExceptionType.None"/> if it did not.</summary>
    public EvmExceptionType Exception
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _exception;
    }

    /// <summary>Wraps a status from a handler that leaves the program counter to the caller.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator OpcodeResult(EvmExceptionType exception)
        => new(NoProgramCounter, exception);

    /// <summary>Wraps a counter from a handler that moved it and did not halt.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator OpcodeResult(nint programCounter)
        => new(programCounter, EvmExceptionType.None);
}
