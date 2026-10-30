// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;

namespace Nethermind.Evm;

/// <summary>Where a jump to address 0 goes on Airbender once the program runs.</summary>
/// <remarks>
/// Airbender's ROM starts at address 0, so a jump through an empty code pointer does not fault: bflat's start code
/// sends it on to the target set here, with every register as the jump left it. Only code behind <c>AirbenderFlag</c>
/// reaches this.
/// </remarks>
internal static class AirbenderZeroJump
{
    // Named as its entry point in bflat's Airbender start module.
    [DllImport("*")]
    public static extern void airbender_set_zero_jump_target(nint target);
}
