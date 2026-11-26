// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.Stateless.Execution.IO;

public sealed partial class WitnessCode
{
    /// <remarks>A guest that reuses its input wraps the code where it lies in the input, which outlives the run, and
    /// <see cref="PlaceInConsumedInput"/> moves it once the input is decoded. SP1 charges for every word of memory a run
    /// touches, and the input is touched already, so code kept there is nearly free while a copy in fresh memory is not.
    /// </remarks>
    private static partial ReadOnlyMemory<byte> Hold(ReadOnlySpan<byte> code) =>
        FirstTouchMemoryFlag.IsActive ? ExecutableCodeMemory.Wrap(code) : ExecutableCodeMemory.Copy(code);

    internal static partial int PlaceInConsumedInput(SszWitnessCodes[] codes, ReadOnlySpan<byte> consumed) =>
        FirstTouchMemoryFlag.IsActive ? MoveToFront(codes, consumed) : 0;
}
