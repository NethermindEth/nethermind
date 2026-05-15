// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Optimism.CL;
using Nethermind.Optimism.CL.L1Bridge;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Optimism.Test.CL;

public class L1ConfigValidatorTests
{
    private static IEnumerable<TestCaseData> ConfigurationTestCases()
    {
        yield return new TestCaseData(BlockchainIds.Mainnet, 100ul, TestItem.KeccakA, BlockchainIds.Mainnet, TestItem.KeccakA, true)
            .SetName("Matches - Mainnet");
        yield return new TestCaseData(BlockchainIds.Sepolia, 0xAABBul, TestItem.KeccakC, BlockchainIds.Sepolia, TestItem.KeccakC, true)
            .SetName("Matches - Sepolia");

        yield return new TestCaseData(BlockchainIds.Mainnet, 200ul, TestItem.KeccakC, BlockchainIds.Sepolia, TestItem.KeccakC, false)
            .SetName("Mismatch - Mainnet vs Sepolia");
        yield return new TestCaseData(BlockchainIds.Sepolia, 0xBBAAul, TestItem.KeccakC, BlockchainIds.Mainnet, TestItem.KeccakC, false)
            .SetName("Mismatch - Sepolia vs Mainnet");

        yield return new TestCaseData(BlockchainIds.Mainnet, 300ul, TestItem.KeccakA, BlockchainIds.Mainnet, TestItem.KeccakB, false)
            .SetName("Mismatch - Mainnet genesis hash");
        yield return new TestCaseData(BlockchainIds.Sepolia, 0xFFFFul, TestItem.KeccakC, BlockchainIds.Sepolia, TestItem.KeccakD, false)
            .SetName("Mismatch - Sepolia genesis hash");
    }

    [TestCaseSource(nameof(ConfigurationTestCases))]
    public async Task Validate_ConfigurationMatchesExpected(
        ulong expectedChainId,
        ulong genesisNumber,
        Hash256 expectedGenesisHash,
        ulong actualChainId,
        Hash256 actualGenesisHash,
        bool isValid)
    {
        IEthApi ethApi = Substitute.For<IEthApi>();
        ILogManager logManager = NullLogManager.Instance;
        L1ConfigValidator validator = new(ethApi, logManager);

        ethApi.GetChainId().Returns(Task.FromResult(actualChainId));
        ethApi.GetBlockByNumber(genesisNumber, true).Returns(Task.FromResult<L1Block?>(new L1Block { Hash = actualGenesisHash }));

        bool result = await validator.Validate(expectedChainId, genesisNumber, expectedGenesisHash);
        result.Should().Be(isValid);
    }
}
