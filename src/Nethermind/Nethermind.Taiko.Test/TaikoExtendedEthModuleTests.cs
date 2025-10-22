// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using FluentAssertions;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Int256;
using Nethermind.Taiko.Rpc;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Taiko.Test;

public class TaikoExtendedEthModuleTests
{
    [Test]
    public void TestCanResolve()
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddModule(new TaikoModule())
            .Build();

        var act = () => container.Resolve<ITaikoExtendedEthRpcModule>();
        act.Should().NotThrow();
    }

    [TestCase(true, "snap")]
    [TestCase(false, "full")]
    public void TestSyncMode(bool snapEnabled, string result)
    {
        TaikoExtendedEthModule rpc = new TaikoExtendedEthModule(new SyncConfig()
        {
            SnapSync = snapEnabled
        }, Substitute.For<IL1OriginStore>());

        rpc.taiko_getSyncMode().Result.Data.Should().Be(result);
    }

    [Test]
    public void TestHeadL1Origin()
    {
        IL1OriginStore originStore = Substitute.For<IL1OriginStore>();
        TaikoExtendedEthModule rpc = new TaikoExtendedEthModule(new SyncConfig(), originStore);

        L1Origin origin = new L1Origin(0, TestItem.KeccakA, 1, Hash256.Zero, null);
        originStore.ReadHeadL1Origin().Returns((UInt256)1);
        originStore.ReadL1Origin((UInt256)1).Returns(origin);

        rpc.taiko_headL1Origin().Result.Data.Should().Be(origin);
    }

    [Test]
    public void TestL1OriginById()
    {
        IL1OriginStore originStore = Substitute.For<IL1OriginStore>();
        TaikoExtendedEthModule rpc = new TaikoExtendedEthModule(new SyncConfig(), originStore);

        L1Origin origin = new L1Origin(0, TestItem.KeccakA, 1, Hash256.Zero, null);
        originStore.ReadL1Origin((UInt256)0).Returns(origin);

        rpc.taiko_l1OriginByID(0).Result.Data.Should().Be(origin);
    }

    [Test]
    public void TestL1OriginById_WithBuildPayloadArgsId()
    {
        IL1OriginStore originStore = Substitute.For<IL1OriginStore>();
        TaikoExtendedEthModule rpc = new TaikoExtendedEthModule(new SyncConfig(), originStore);

        var buildPayloadArgsId = new int[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 };
        L1Origin origin = new L1Origin(0, TestItem.KeccakA, 1, Hash256.Zero, buildPayloadArgsId);
        originStore.ReadL1Origin((UInt256)0).Returns(origin);

        rpc.taiko_l1OriginByID(0).Result.Data.Should().Be(origin);
    }

    [Test]
    public void TestL1OriginById_ValueHash256_EvenLengthHex()
    {
        IL1OriginStore originStore = Substitute.For<IL1OriginStore>();
        TaikoExtendedEthModule rpc = new TaikoExtendedEthModule(new SyncConfig(), originStore);
        int expectedLengthInChars = ValueHash256.Length * 2 + 2;

        // Create odd length hash values
        var l2BlockHash = new ValueHash256("0x35a48c5b3ee5b1b2a365fcd1aa68c738d1c06474578087a78fa79dd45de6214");
        var l1BlockHash = new Hash256("0x2daf7e4b06ca2d3a82c775d9e9ad0c973545a608684146cda0df5f7d71188a5");

        L1Origin origin = new L1Origin(0, l2BlockHash, 1, l1BlockHash, null);
        originStore.ReadL1Origin((UInt256)0).Returns(origin);

        var result = rpc.taiko_l1OriginByID(0).Result.Data;
        result.Should().Be(origin);

        // Serialize the RPC result and verify hash values have even-length hex string
        var serializer = new Serialization.Json.EthereumJsonSerializer();
        var json = serializer.Serialize(result);

        // Parse the JSON to extract the hash values
        var jsonDoc = System.Text.Json.JsonDocument.Parse(json);
        var l2BlockHashString = jsonDoc.RootElement.GetProperty("l2BlockHash").GetString();
        var l1BlockHashString = jsonDoc.RootElement.GetProperty("l1BlockHash").GetString();

        l2BlockHashString.Should().NotBeNull();
        l2BlockHashString!.Length.Should().Be(expectedLengthInChars);

        l1BlockHashString.Should().NotBeNull();
        l1BlockHashString!.Length.Should().Be(expectedLengthInChars);
    }
}
