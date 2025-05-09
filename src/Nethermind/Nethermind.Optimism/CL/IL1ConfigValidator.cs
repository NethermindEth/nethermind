// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core.Crypto;

namespace Nethermind.Optimism.CL;

/// <summary>
/// Interface for validating L1 chain configuration parameters
/// </summary>
public interface IL1ConfigValidator
{
    /// <summary>
    /// Validates the L1 chain configuration against expected parameters
    /// </summary>
    /// <param name="expectedChainId">The expected L1 chain ID</param>
    /// <param name="expectedGenesisNumber">The expected L1 genesis block number</param>
    /// <param name="expectedGenesisHash">The expected L1 genesis block hash</param>
    /// <returns>True if validation passes, false otherwise</returns>
    Task<bool> Validate(ulong expectedChainId, ulong expectedGenesisNumber, Hash256 expectedGenesisHash);
}
