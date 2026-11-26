// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>Whether the guest runs on OpenVM's RV32 line.</summary>
/// <remarks>
/// <see langword="false"/> as written; the OpenVM RV32 guest's <c>substitutions.xml</c> stubs <see cref="IsActive"/> to
/// <see langword="true"/>, beside <see cref="OpenVmFlag"/>. A flag of its own rather than a pointer-size test, so ILC
/// folds every check on it on RV64 too, and the JIT never binds the imports of a branch it guards.
/// There, as on Airbender, word loads and stores must be aligned: OpenVM's RV32 memory computes a misaligned one wrong
/// rather than trapping, so the JIT expands byte by byte every access it cannot prove aligned, and a path that knows
/// the alignment reads whole words.
/// </remarks>
public readonly struct OpenVm32Flag : IFlag
{
    /// <inheritdoc />
    public static bool IsActive => false;
}
