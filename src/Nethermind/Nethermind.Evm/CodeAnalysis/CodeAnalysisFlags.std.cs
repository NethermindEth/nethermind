// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.CodeAnalysis;

/// <summary>
/// Selects which ahead-of-execution code analyses this build performs.
/// </summary>
/// <remarks>
/// Recognizing a template costs a scan of the code and pays for it over every later call into that
/// code. A node keeps analysed code across blocks, so the scan is amortized and the saved dispatch is
/// profit. The zkEVM guest proves one block and then exits: nothing it analyses is ever reused, the
/// scan is charged against the single block that triggered it, and it counts instructions rather than
/// wall time, so there is no branch prediction or cache to make the saved dispatch worth more than the
/// scan. Measured on the guest fixtures, recognition cost roughly 2-3% more steps than it saved.
/// See <c>CodeAnalysisFlags.zkevm.cs</c>.
/// </remarks>
internal static partial class CodeAnalysisFlags
{
    /// <summary>Whether bytecode templates are recognized and their frames entered past the opcodes they cover.</summary>
    public const bool Templates = true;
}
