// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>Whether ADDMOD and MULMOD use OpenVM's modular extension for the moduli its standard configuration has.</summary>
/// <remarks>
/// <see langword="false"/> as written; an OpenVM guest's <c>substitutions.xml</c> stubs <see cref="IsActive"/> to
/// <see langword="true"/>. Separate from <see cref="OpenVmFlag"/> because the extension is in every OpenVM
/// configuration, while the division hint <see cref="OpenVmFlag"/> also turns on needs Nethermind's own runner.
/// </remarks>
public readonly struct OpenVmModularFlag : IFlag
{
    /// <inheritdoc />
    public static bool IsActive => false;
}
