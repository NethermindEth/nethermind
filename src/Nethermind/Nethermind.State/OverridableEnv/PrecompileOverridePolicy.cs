// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.OverridableEnv;

/// <summary>Controls whether account overrides replace native precompiles with account code.</summary>
/// <remarks>Storage, balance, nonce and code still change in the overridden world state.</remarks>
public sealed class PrecompileOverridePolicy(bool disableOnAccountOverride = true)
{
    /// <summary>Whether an account override disables the native precompile at that address, as in geth.</summary>
    public bool DisableOnAccountOverride { get; } = disableOnAccountOverride;
}
