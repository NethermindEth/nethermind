// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.Evm;

public ref partial struct EvmStack
{
    /// <summary>Reports whether <paramref name="destination"/> is a valid jump destination in <see cref="Code"/>.</summary>
    /// <remarks>
    /// The frame caches the code info's incremental bitmap, which covers only the code scanned so far,
    /// so a clear bit is not yet an answer: it falls through to <see cref="CodeInfo.AnalyzeJump"/>, which
    /// extends the scan to reach the destination. Repeat jumps to an already scanned destination take the
    /// bit test alone. See <c>EvmStack.std.cs</c> for the host form, which analyzes the whole code up front.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool IsJumpDestination(int destination)
    {
        long[] bitmap = _jumpDestinations ??= _codeInfo?.IncrementalJumpBitmap ?? JumpDestinationAnalyzer.EmptyBitmap;
        return JumpDestinationAnalyzer.IsJumpDestination(bitmap, destination) || (_codeInfo?.AnalyzeJump(destination) ?? false);
    }
}
