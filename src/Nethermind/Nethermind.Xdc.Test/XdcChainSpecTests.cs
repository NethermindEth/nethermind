// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Xdc.Spec;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

/// <summary>
/// Pins the system contract addresses our chain specs deserialize into <see cref="XdcChainSpecEngineParameters"/>.
/// </summary>
/// <remarks>
/// A key that does not match its property name deserializes to <c>null</c> instead of failing, and the affected
/// contract then never matches a transaction recipient - the DEX/lending addresses silently lose their
/// special-transaction handling, which diverges from the reference client.
/// </remarks>
[TestFixture, Parallelizable(ParallelScope.All)]
public class XdcChainSpecTests
{
    [TestCase("xdc.json", TestName = "mainnet")]
    [TestCase("xdc-testnet.json", TestName = "apothem")]
    public void System_contract_addresses_are_deserialized(string chainSpecFile)
    {
        XdcChainSpecEngineParameters engineParameters = LoadEngineParameters(chainSpecFile);

        Assert.Multiple(() =>
        {
            Assert.That(engineParameters.MasternodeVotingContract, Is.EqualTo(new Address("0x0000000000000000000000000000000000000088")));
            Assert.That(engineParameters.BlockSignerContract, Is.EqualTo(new Address("0x0000000000000000000000000000000000000089")));
            Assert.That(engineParameters.RandomizeSMCBinary, Is.EqualTo(new Address("0x0000000000000000000000000000000000000090")));
            Assert.That(engineParameters.XDCXAddressBinary, Is.EqualTo(new Address("0x0000000000000000000000000000000000000091")));
            Assert.That(engineParameters.TradingStateAddressBinary, Is.EqualTo(new Address("0x0000000000000000000000000000000000000092")));
            Assert.That(engineParameters.XDCXLendingAddressBinary, Is.EqualTo(new Address("0x0000000000000000000000000000000000000093")));
            Assert.That(engineParameters.XDCXLendingFinalizedTradeAddressBinary, Is.EqualTo(new Address("0x0000000000000000000000000000000000000094")));
        });
    }

    private static XdcChainSpecEngineParameters LoadEngineParameters(string chainSpecFile)
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains", chainSpecFile);
        ChainSpec chainSpec = new ChainSpecFileLoader(new EthereumJsonSerializer(), LimboLogs.Instance).LoadEmbeddedOrFromFile(path);

        return chainSpec.EngineChainSpecParametersProvider.GetChainSpecParameters<XdcChainSpecEngineParameters>();
    }
}
