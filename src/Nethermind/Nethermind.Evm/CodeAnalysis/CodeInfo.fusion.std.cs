// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;

namespace Nethermind.Evm.CodeAnalysis;

/// <summary>
/// The fusable-run scan for <see cref="CodeInfo"/>, which the zkEVM build does not compile.
/// </summary>
public sealed partial class CodeInfo
{
    private MappingSlotFusion? _fusion;
    private bool _fusionResolved;

    /// <summary>The fusable opcode runs this code contains, scanned for on first use.</summary>
    /// <remarks>
    /// Racing callers may each scan the code; the results are equivalent, so the duplicate work is
    /// preferred over synchronising a scan that runs once per distinct code hash. The resolved flag is
    /// published after the result so a reader never sees a resolved-but-absent scan.
    /// </remarks>
    internal MappingSlotFusion? Fusion => ResolveFusion();

    private MappingSlotFusion? ResolveFusion()
    {
        if (Volatile.Read(ref _fusionResolved)) return _fusion;

        MappingSlotFusion? fusion = MappingSlotFusion.Find(CodeSpan);
        _fusion = fusion;
        Volatile.Write(ref _fusionResolved, true);
        return fusion;
    }
}
