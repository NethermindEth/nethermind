// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Evm;

/// <summary>A jump handler's outcome: the program counter it selected, packed with its halting status.</summary>
/// <remarks>
/// The counter occupies the low 32 bits and the <see cref="EvmExceptionType"/> the high 32. Handlers that
/// move the counter would otherwise take it by reference, which forces it into an address-taken stack slot
/// for as long as the body runs; the jump handlers pay that because they call a helper that does not inline,
/// and <c>JUMPI</c> does not inline into the dispatch loop at all. Returning it keeps it in a register.
/// One <see cref="ulong"/> wide because a 16-byte struct returns through memory on the Windows x64 ABI.
/// </remarks>
[method: MethodImpl(MethodImplOptions.AggressiveInlining)]
public readonly struct OpcodeResult(nint programCounter, EvmExceptionType exception)
{
    private readonly ulong _packed = ((ulong)(uint)exception << 32) | (uint)programCounter;

    /// <summary>The counter the handler selected, whether or not it halted.</summary>
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
}
