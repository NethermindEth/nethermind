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
}
