// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core.Crypto;

namespace Nethermind.Optimism.CL;

/// <summary>
/// Validate L1 configuration parameters against expected values
/// </summary>
public interface IL1ConfigValidator
{
    /// <summary>
    /// Validates the L1 chain configuration against expected parameters
    /// </summary>
    /// <param name="expectedChainId">The expected L1 chain ID</param>
    /// <param name="genesisNumber">The genesis block number in L1</param>
    /// <param name="expectedGenesisHash">The expected block hash of the genesis block in L1</param>
    /// <returns>True if validation passes, false otherwise</returns>
    Task<bool> Validate(ulong expectedChainId, ulong genesisNumber, Hash256 expectedGenesisHash);
}
