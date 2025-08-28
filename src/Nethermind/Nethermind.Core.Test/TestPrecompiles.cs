// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.Precompiles;

namespace Nethermind.Core.Test;

/// <summary>
/// Test utilities for precompile checkers.
/// Provides shared instances to avoid unnecessary object creation in tests.
/// </summary>
public static class TestPrecompiles
{
    /// <summary>
    /// Shared EthereumPrecompileChecker instance for use in tests.
    /// Safe to share since EthereumPrecompileChecker is stateless.
    /// </summary>
    public static readonly IPrecompileChecker Ethereum = new EthereumPrecompileChecker();
}
