// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;

namespace Nethermind.Taiko;

/// <summary>
/// Taiko-specific genesis builder that wraps the standard <see cref="IGenesisBuilder"/> and ensures
/// that fields required by EIP-4844 (<c>BlobGasUsed</c>, <c>ExcessBlobGas</c>) and EIP-4788
/// (<c>ParentBeaconBlockRoot</c>) are populated with their zero values on the genesis block when
/// the corresponding forks are active at genesis timestamp.
///
/// On a standard Ethereum chain these fields are populated by the block processor during normal
/// block production. For a Taiko L2 chain the genesis block is <em>suggested</em> directly from the
/// chainspec and never goes through a production path, so without this decorator the suggested
/// genesis header and the processed genesis header diverge, causing
/// <c>BlockProcessor.ValidateProcessedBlock</c> to reject block 0.
/// </summary>
public class TaikoGenesisBuilder(IGenesisBuilder inner, ISpecProvider specProvider) : IGenesisBuilder
{
    /// <summary>
    /// Builds the genesis block via the wrapped builder and then stamps any zero-valued EIP-4844
    /// and EIP-4788 fields that must be present in the header when those forks are active at genesis.
    /// </summary>
    public Block Build()
    {
        Block genesis = inner.Build();

        IReleaseSpec genesisSpec = specProvider.GenesisSpec;

        if (genesisSpec.IsEip4844Enabled)
        {
            genesis.Header.BlobGasUsed ??= 0;
            genesis.Header.ExcessBlobGas ??= 0;
        }

        if (genesisSpec.IsEip4788Enabled)
        {
            genesis.Header.ParentBeaconBlockRoot ??= Keccak.Zero;
        }

        genesis.Header.Hash = genesis.Header.CalculateHash();

        return genesis;
    }
}
