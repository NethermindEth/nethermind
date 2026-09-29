// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.Evm;

public ref partial struct EvmStack
{
    /// <summary>Reports whether <paramref name="destination"/> is a valid jump destination in <see cref="Code"/>.</summary>
    /// <remarks>
    /// The bitmap is resolved on the first in-range jump and kept in the frame, so a jump validates
    /// against the frame it is executing without walking <c>vm.VmState.Env.CodeInfo</c>, and a frame
    /// that never jumps never pays for the analysis. Only a stack built over no code may omit the code
    /// info; the empty bitmap then rejects every destination. See <c>EvmStack.zkevm.cs</c> for the guest
    /// form, which analyzes the code only as far as it jumps into it.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool IsJumpDestination(int destination)
    {
        long[]? bitmap = _jumpDestinations;
        if (bitmap is null)
        {
            if ((uint)destination >= (uint)CodeLength) return false;
            Debug.Assert(_codeInfo is not null || CodeLength == 0, "A stack that executes code must carry that code's CodeInfo.");
            _jumpDestinations = bitmap = _codeInfo?.JumpDestinationBitmap ?? JumpDestinationAnalyzer.EmptyBitmap;
        }

        return JumpDestinationAnalyzer.IsJumpDestination(bitmap, destination);
    }
}
