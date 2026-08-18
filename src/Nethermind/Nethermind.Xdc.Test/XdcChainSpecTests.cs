// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Xdc.Spec;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

/// <summary>
/// Pins what our chain specs produce for the XDCX special-transaction path on both networks.
/// </summary>
/// <remarks>
/// Both halves of that path fail silently rather than loudly. A key that does not match its property name
/// deserializes to <c>null</c>, and the contract then never matches a transaction recipient; a fork block that is not
/// registered in <see cref="XdcChainSpecEngineParameters.AddTransitions"/> gets no release spec boundary of its own,
/// so its flag only flips on whichever unrelated transition encloses it. Either way transactions take the wrong path
/// and diverge from the reference client, with nothing in the logs to say so.
/// </remarks>
[TestFixture, Parallelizable(ParallelScope.All)]
public class XdcChainSpecTests
{
    [TestCase("xdc.json", TestName = "mainnet")]
    [TestCase("xdc-testnet.json", TestName = "apothem")]
    public void System_contract_addresses_are_deserialized(string chainSpecFile)
    {
        XdcChainSpecEngineParameters engineParameters = EngineParameters(LoadChainSpec(chainSpecFile));

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

    [TestCase("xdc.json", TestName = "mainnet")]
    [TestCase("xdc-testnet.json", TestName = "apothem")]
    public void XDCX_flags_flip_on_their_own_blocks(string chainSpecFile)
    {
        ChainSpec chainSpec = LoadChainSpec(chainSpecFile);
        XdcChainSpecEngineParameters engineParameters = EngineParameters(chainSpec);
        XdcChainSpecBasedSpecProvider specProvider = new(chainSpec, engineParameters, LimboLogs.Instance);

        ulong activation = engineParameters.TipXDCX!.Value;
        ulong minerDisable = engineParameters.TIPXDCXMinerDisable!.Value;
        ulong receiverDisable = engineParameters.TIPXDCXReceiverDisable!.Value;

        Assert.Multiple(() =>
        {
            Assert.That(specProvider.GetXdcSpec(activation - 1).IsTIPXDCXMiner, Is.False);
            Assert.That(specProvider.GetXdcSpec(activation).IsTIPXDCXMiner, Is.True);
            Assert.That(specProvider.GetXdcSpec(minerDisable - 1).IsTIPXDCXMiner, Is.True);
            Assert.That(specProvider.GetXdcSpec(minerDisable).IsTIPXDCXMiner, Is.False);

            Assert.That(specProvider.GetXdcSpec(activation - 1).IsTIPXDCXReceiver, Is.False);
            Assert.That(specProvider.GetXdcSpec(activation).IsTIPXDCXReceiver, Is.True);
            Assert.That(specProvider.GetXdcSpec(receiverDisable - 1).IsTIPXDCXReceiver, Is.True);
            Assert.That(specProvider.GetXdcSpec(receiverDisable).IsTIPXDCXReceiver, Is.False);
        });
    }

    // Loaded by name so it resolves to the copy Nethermind.Config embeds, which is what a released node reads, and
    // which needs no assumption about where the test binaries sit relative to the source tree.
    private static ChainSpec LoadChainSpec(string chainSpecFile) =>
        new ChainSpecFileLoader(new EthereumJsonSerializer(), LimboLogs.Instance).LoadEmbeddedOrFromFile(chainSpecFile);

    private static XdcChainSpecEngineParameters EngineParameters(ChainSpec chainSpec) =>
        chainSpec.EngineChainSpecParametersProvider.GetChainSpecParameters<XdcChainSpecEngineParameters>();
}
