// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.Stateless.Execution.IO;

public sealed partial class WitnessCode
{
    private static partial ReadOnlyMemory<byte> Hold(ReadOnlySpan<byte> code) => ExecutableCodeMemory.Copy(code);

    /// <remarks>A no-op on the host, which holds every code in memory of its own.</remarks>
    internal static partial int PlaceInConsumedInput(SszWitnessCodes[] codes, ReadOnlySpan<byte> consumed) => 0;
}
