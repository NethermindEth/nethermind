// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Synchronization;
using Nethermind.Xdc.Spec;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

/// <summary>
/// Pins the fork IDs our chain specs produce to the ones the reference client advertises.
/// </summary>
/// <remarks>
/// Genesis hashes come from <c>admin_nodeInfo</c> on a node of each network; the fork schedules are the non-nil
/// <c>*Block</c> fields of <c>XDCMainnetChainConfig</c> and <c>TestnetChainConfig</c> plus <c>XDPoS.V2.SwitchBlock</c>,
/// which is what <c>ChainConfig.GatherForks</c> feeds into EIP-2124. A mismatch here means XDC peers reject our
/// handshake with <c>ErrForkIDRejected</c>, so these cases are the acceptance criterion for xdc/164 and xdc/165.
/// </remarks>
[TestFixture, Parallelizable(ParallelScope.All)]
public class XdcForkIdConformanceTests
{
    private static readonly Hash256 MainnetGenesisHash = new("0x4a9d748bd78a8d0385b67788c2435dcdb914f98a96250b68863a1f8b7642d6b1");
    private static readonly Hash256 TestnetGenesisHash = new("0xbdea512b4f12ff1135ec92c00dc047ffb93890c2ea1aa0eefe9b013d80640075");

    // head, expected checksum, expected next fork
    [TestCase(0ul, 0x6627b1abu, 1ul, TestName = "genesis")]
    [TestCase(1ul, 0x6fdbe752u, 2ul, TestName = "homestead")]
    [TestCase(4ul, 0x30c010d4u, 3000000ul, TestName = "byzantium")]
    [TestCase(3000000ul, 0x0633fab8u, 3464000ul, TestName = "tipSigning")]
    [TestCase(23779191ul, 0x6c07b5cfu, 56828700ul, TestName = "denylist")]
    [TestCase(56828700ul, 0x07a2b1b8u, 61290000ul, TestName = "XDPoS 2.0 switch")]
    [TestCase(66825000ul, 0x6e0b9072u, 71550000ul, TestName = "tipXDCXReceiverDisable")]
    [TestCase(83600000ul, 0x7fcfc8b0u, 0ul, TestName = "prague")]
    public void Apothem_fork_ids_match_the_reference_client(ulong head, uint expectedChecksum, ulong expectedNext) =>
        AssertForkId("xdc-testnet.json", TestnetGenesisHash, head, expectedChecksum, expectedNext);

    // head, expected checksum, expected next fork
    [TestCase(0ul, 0x0f331419u, 1ul, TestName = "mainnet genesis")]
    [TestCase(4ul, 0x050471fau, 3000000ul, TestName = "mainnet byzantium")]
    [TestCase(5000000ul, 0x1f7d5792u, 38383838ul, TestName = "mainnet tipIncreaseMasternodes")]
    [TestCase(38383838ul, 0xeb158a3fu, 76321000ul, TestName = "mainnet denylist")]
    [TestCase(76321000ul, 0xa16bdb01u, 80370000ul, TestName = "mainnet shanghai")]
    [TestCase(80370000ul, 0x3c70f2dfu, 80370900ul, TestName = "mainnet XDPoS 2.0 switch")]
    [TestCase(98800200ul, 0x6ccdf361u, 98802000ul, TestName = "mainnet eip1559")]
    [TestCase(98802000ul, 0x8ad33642u, 0ul, TestName = "mainnet cancun")]
    public void Mainnet_fork_ids_match_the_reference_client(ulong head, uint expectedChecksum, ulong expectedNext) =>
        AssertForkId("xdc.json", MainnetGenesisHash, head, expectedChecksum, expectedNext);

    private static void AssertForkId(string chainSpecFile, Hash256 genesisHash, ulong head, uint expectedChecksum, ulong expectedNext)
    {
        ForkId forkId = ForkInfo(chainSpecFile, genesisHash).GetForkId(head, 0);

        Assert.That(forkId.ForkHash, Is.EqualTo(expectedChecksum));
        Assert.That(forkId.Next, Is.EqualTo(expectedNext));
    }

    private static XdcForkInfo ForkInfo(string chainSpecFile, Hash256 genesisHash)
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains", chainSpecFile);
        ChainSpec chainSpec = new ChainSpecFileLoader(new EthereumJsonSerializer(), LimboLogs.Instance).LoadEmbeddedOrFromFile(path);
        XdcChainSpecEngineParameters engineParameters =
            chainSpec.EngineChainSpecParametersProvider.GetChainSpecParameters<XdcChainSpecEngineParameters>();

        BlockHeader genesis = Build.A.BlockHeader.WithNumber(0).TestObject;
        genesis.Hash = genesisHash;

        ISyncServer syncServer = Substitute.For<ISyncServer>();
        syncServer.Genesis.Returns(genesis);

        return new XdcForkInfo(new XdcChainSpecBasedSpecProvider(chainSpec, engineParameters, LimboLogs.Instance), syncServer, engineParameters);
    }
}
