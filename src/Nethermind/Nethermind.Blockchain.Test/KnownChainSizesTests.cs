// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

public class KnownChainSizesTests
{
    [Test]
    public void Update_known_chain_sizes()
    {
        // Pruning size have to be updated frequently
        ChainSizes.CreateChainSizeInfo(BlockchainIds.Mainnet).PruningSize.Should().BeLessThan(250.GB());
        ChainSizes.CreateChainSizeInfo(BlockchainIds.Goerli).PruningSize.Should().BeLessThan(70.GB());
        ChainSizes.CreateChainSizeInfo(BlockchainIds.Sepolia).PruningSize.Should().BeLessThan(11.GB());

        ChainSizes.CreateChainSizeInfo(BlockchainIds.Chiado).PruningSize.Should().Be(null);
        ChainSizes.CreateChainSizeInfo(BlockchainIds.Gnosis).PruningSize.Should().Be(null);

        ChainSizes.CreateChainSizeInfo(BlockchainIds.EnergyWeb).PruningSize.Should().Be(null);
        ChainSizes.CreateChainSizeInfo(BlockchainIds.Volta).PruningSize.Should().Be(null);
    }
}
