// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Zkvm.Abstractions;

/// <summary>Provides Airbender runtime control hooks.</summary>
public static partial class AirbenderRuntime
{
    /// <summary>Sets the destination of jumps to address zero.</summary>
    /// <param name="target">The code address to receive the jump.</param>
    /// <remarks>Airbender's ROM starts at zero. Its entry stub forwards such jumps with registers unchanged;
    /// the caller must supply a valid code address compatible with that register state.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetZeroJumpTarget(nint target) => airbender_set_zero_jump_target(target);

    /// <summary>Reads the guest's input as <see cref="IO.ReadInput"/> does, and has libairbender hash the witness
    /// nodes and the codes of a trusted input while it reads them, and take the hints that follow it.</summary>
    /// <returns>The data read.</returns>
    /// <remarks>libairbender's <c>read_input</c> only reads: what <see cref="Accelerators"/> offers over the
    /// input (the tries over the witness, the code hashes, the division hints) needs this entry point.</remarks>
    public static unsafe ReadOnlySpan<byte> ReadPrehashedInput()
    {
        byte* ptr = null;

        airbender_read_prehashed_input(&ptr, out nuint size);

        return size == nuint.Zero ? [] : new ReadOnlySpan<byte>(ptr, checked((int)size));
    }
}
