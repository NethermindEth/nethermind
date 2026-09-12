// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.CodeAnalysis;

/// <inheritdoc cref="CodeAnalysisFlags"/>
internal static partial class CodeAnalysisFlags
{
    /// <summary>
    /// The guest analyses nothing it will reuse, so the scan for fusable runs is compiled out entirely
    /// rather than left to a runtime check.
    /// </summary>
    public const bool Fusion = false;
}
