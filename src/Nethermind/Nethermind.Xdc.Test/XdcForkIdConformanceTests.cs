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
/// Pins the fork IDs our chain spec produces to the ones the reference client advertises.
/// </summary>
/// <remarks>
/// Vectors are derived from XDC Apothem (network 51, XDC/v2.8.2-testnet) <c>admin_nodeInfo</c>: its genesis hash
/// and the fork blocks its chain config gathers. A mismatch here means XDC peers will reject our handshake with
/// <c>ErrForkIDRejected</c>, so these cases are the acceptance criterion for xdc/164 and xdc/165.
/// </remarks>
[TestFixture, Parallelizable(ParallelScope.All)]
public class XdcForkIdConformanceTests
{
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
    public void Apothem_fork_ids_match_the_reference_client(ulong head, uint expectedChecksum, ulong expectedNext)
    {
        ForkId forkId = ForkInfo("xdc-testnet.json", TestnetGenesisHash).GetForkId(head, 0);

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
