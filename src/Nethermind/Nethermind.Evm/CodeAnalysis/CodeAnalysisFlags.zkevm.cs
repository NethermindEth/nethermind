// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.CodeAnalysis;

/// <inheritdoc cref="CodeAnalysisFlags"/>
internal static partial class CodeAnalysisFlags
{
    /// <summary>
    /// The guest analyses nothing it will reuse, so template recognition is compiled out entirely
    /// rather than left to a runtime check.
    /// </summary>
    public const bool Templates = false;
}
