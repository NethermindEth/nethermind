// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Evm.Precompiles;

/// <summary>
/// Thread-local ambient context for passing remaining gas to precompile DataGasCost().
/// Set by VirtualMachine.RunPrecompile() before calling DataGasCost().
/// Follows the same pattern as <see cref="Nethermind.Serialization.Json.ForcedNumberConversion"/>.
/// </summary>
public static class PrecompileGasContext
{
    [ThreadStatic]
    public static long AvailableGas;
}
