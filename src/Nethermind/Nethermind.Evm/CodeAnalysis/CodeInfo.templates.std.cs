// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.CodeAnalysis;

/// <summary>
/// Template recognition for <see cref="CodeInfo"/>, which the zkEVM build does not compile.
/// </summary>
public sealed partial class CodeInfo
{
    private CodeTemplate? _template;

    /// <summary>The compiler-emitted template this code matches, recognized ahead of execution where possible.</summary>
    /// <remarks>
    /// Normally resolved on the analysis thread before the code first runs; a caller that arrives first
    /// recognizes it inline instead. Racing callers may each recognize the code, which is why the result
    /// is idempotent: the duplicate work is preferred over synchronising a lookup that runs once per
    /// distinct code hash. Which arm a given call takes is therefore timing-dependent, and can be,
    /// because the two produce identical gas, state and output.
    /// </remarks>
    internal CodeTemplate Template => _template ?? PrepareAnalysis();

    /// <summary>Recognizes the code now, rather than waiting for the analysis thread to reach it.</summary>
    internal CodeTemplate PrepareAnalysis() => _template ??= CodeTemplate.Recognize(CodeSpan, ValidateJump);

    partial void PrepareTemplateAnalysis() => PrepareAnalysis();
}
