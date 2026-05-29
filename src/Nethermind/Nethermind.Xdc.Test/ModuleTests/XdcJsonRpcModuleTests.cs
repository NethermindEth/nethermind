// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc.Test;
using Nethermind.Xdc;
using Nethermind.Xdc.RPC;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test.ModuleTests;

[TestFixture]
public class XdcJsonRpcModuleTests
{
    [Test]
    public async Task GetSnapshot_is_exposed_on_json_rpc_wire()
    {
        IXdcRpcModule module = CreateModuleWithSnapshot();
        string json = await RpcTest.TestSerializedRequest(module, "GetSnapshot", new BlockParameter(100L));
        json.Should().Contain("result");
        json.Should().NotContain("\"error\"");
    }

    [Test]
    public async Task NetworkInformation_is_exposed_on_json_rpc_wire()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        XdcBlockHeader header = Build.A.XdcBlockHeader().TestObject;
        blockTree.Head.Returns(Build.A.Block.WithHeader(header).TestObject);

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.NetworkId.Returns(51u);
        IXdcReleaseSpec xdcSpec = new XdcReleaseSpec
        {
            EpochLength = 900,
            Gap = 5,
            Reward = 5000,
            MasternodeVotingContract = TestItem.AddressA,
            RelayerRegistrationSMC = TestItem.AddressB,
            TRC21IssuerSMC = TestItem.AddressC,
            XDCXLendingAddressBinary = TestItem.AddressD,
            XDCXAddressBinary = TestItem.AddressE,
            V2Configs = [new V2ConfigParams { SwitchRound = 0, MaxMasternodes = 108, CertificateThreshold = 0.667, TimeoutSyncThreshold = 3, TimeoutPeriod = 30, MinePeriod = 2 }],
        };
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(xdcSpec);

        IXdcRpcModule module = new XdcRpcModule(
            blockTree,
            Substitute.For<ISnapshotManager>(),
            specProvider,
            Substitute.For<IQuorumCertificateManager>(),
            Substitute.For<IEpochSwitchManager>(),
            Substitute.For<IVotesManager>(),
            Substitute.For<ITimeoutCertificateManager>(),
            Substitute.For<ISyncInfoManager>(),
            Substitute.For<IRewardsStore>());

        string json = await RpcTest.TestSerializedRequest(module, "NetworkInformation");
        json.Should().Contain("result");
        json.Should().Contain(TestItem.AddressB.ToString().ToLowerInvariant());
        json.Should().Contain(TestItem.AddressC.ToString().ToLowerInvariant());
    }

    private static IXdcRpcModule CreateModuleWithSnapshot()
    {
        XdcBlockHeader header = Build.A.XdcBlockHeader().TestObject;
        header.Number = 100;
        header.Hash = Keccak.OfAnEmptySequenceRlp;

        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindHeader(100L).Returns(header);

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        IXdcReleaseSpec xdcSpec = new XdcReleaseSpec
        {
            EpochLength = 900,
            SwitchBlock = 0,
            V2Configs = [new V2ConfigParams { SwitchRound = 0, MaxMasternodes = 108, CertificateThreshold = 0.667, TimeoutSyncThreshold = 3, TimeoutPeriod = 30, MinePeriod = 2 }],
        };
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(xdcSpec);

        Address[] signers = [TestItem.AddressA, TestItem.AddressB];
        Snapshot snapshot = new(header.Number, header.Hash!, signers);

        ISnapshotManager snapshotManager = Substitute.For<ISnapshotManager>();
        snapshotManager.GetSnapshotByBlockNumber(100, xdcSpec).Returns(snapshot);

        return new XdcRpcModule(
            blockTree,
            snapshotManager,
            specProvider,
            Substitute.For<IQuorumCertificateManager>(),
            Substitute.For<IEpochSwitchManager>(),
            Substitute.For<IVotesManager>(),
            Substitute.For<ITimeoutCertificateManager>(),
            Substitute.For<ISyncInfoManager>(),
            Substitute.For<IRewardsStore>());
    }
}
