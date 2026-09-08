// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc.Modules.Subscribe;
using Nethermind.JsonRpc.Test;
using Nethermind.Serialization.Json;
using Nethermind.Xdc.RPC;
using Nethermind.Xdc.Test.Helpers;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test.ModuleTests;

[TestFixture]
public class XdcBlockForRpcTests
{
    private static readonly Address[] Masternodes = [TestItem.AddressA, TestItem.AddressB];
    private static readonly Address[] NextMasternodes = [TestItem.AddressC];
    private static readonly Address[] Penalised = [TestItem.AddressD];

    private readonly EthereumJsonSerializer _serializer = new();
    private readonly XdcBlockForRpcFactory _factory = new();

    [Test]
    public void Mainnet_header_packs_validators_and_penalties_into_byte_strings()
    {
        XdcBlockHeader header = Build.A.XdcBlockHeader()
            .WithValidator(Seal())
            .WithValidators(Masternodes)
            .WithPenalties(Penalised)
            .TestObject;

        foreach (JsonElement json in Serialize(header))
        {
            Assert.That(json.GetProperty("validator").GetString(), Is.EqualTo(Hex(Seal())));
            Assert.That(json.GetProperty("validators").GetString(), Is.EqualTo(Hex(Pack(Masternodes))));
            Assert.That(json.GetProperty("penalties").GetString(), Is.EqualTo(Hex(Pack(Penalised))));
            Assert.That(json.TryGetProperty("nextValidators", out _), Is.False, "mainnet headers have no next-epoch list");
        }
    }

    [Test]
    public void Subnet_header_spells_out_validators_and_penalties_as_addresses()
    {
        XdcSubnetBlockHeaderBuilder builder = Build.A.XdcSubnetBlockHeader().WithNextValidators(NextMasternodes);
        builder.WithValidator(Seal()).WithValidators(Masternodes).WithPenalties(Penalised);

        foreach (JsonElement json in Serialize(builder.TestObject))
        {
            Assert.That(json.GetProperty("validator").GetString(), Is.EqualTo(Hex(Seal())));
            Assert.That(Addresses(json, "validators"), Is.EqualTo(Masternodes));
            Assert.That(Addresses(json, "nextValidators"), Is.EqualTo(NextMasternodes));
            Assert.That(Addresses(json, "penalties"), Is.EqualTo(Penalised));
        }
    }

    [Test]
    public void Absent_lists_are_emitted_empty_rather_than_omitted()
    {
        XdcBlockHeader mainnet = Build.A.XdcBlockHeader().WithValidators(Array.Empty<byte>()).WithPenalties(Array.Empty<byte>()).TestObject;
        mainnet.Validator = null;

        foreach (JsonElement json in Serialize(mainnet))
        {
            Assert.That(json.GetProperty("validator").GetString(), Is.EqualTo("0x"));
            Assert.That(json.GetProperty("validators").GetString(), Is.EqualTo("0x"));
            Assert.That(json.GetProperty("penalties").GetString(), Is.EqualTo("0x"));
        }

        XdcSubnetBlockHeader subnet = Build.A.XdcSubnetBlockHeader().WithNextValidators(Array.Empty<byte>()).TestObject;
        subnet.Validators = null;
        subnet.Penalties = null;

        foreach (JsonElement json in Serialize(subnet))
        {
            Assert.That(Addresses(json, "validators"), Is.Empty);
            Assert.That(Addresses(json, "nextValidators"), Is.Empty);
            Assert.That(Addresses(json, "penalties"), Is.Empty);
        }
    }

    [Test]
    public void Non_xdc_header_keeps_the_base_shape()
    {
        BlockHeader header = Build.A.BlockHeader.TestObject;
        Block block = Build.A.Block.WithHeader(header).TestObject;

        Assert.That(_factory.Create(block, false, SpecProvider()), Is.TypeOf<BlockForRpc>());
        Assert.That(_factory.CreateHeader(header), Is.TypeOf<BlockHeaderForRpc>());
    }

    /// <remarks>
    /// <c>newHeads</c> declares its payload as <see cref="BlockForRpc"/>, so this covers the declared-type
    /// serialization the model-shape tests above (which serialize at the runtime type) cannot see.
    /// </remarks>
    [Test]
    public void Subscription_payload_keeps_the_xdpos_fields()
    {
        XdcSubnetBlockHeaderBuilder builder = Build.A.XdcSubnetBlockHeader().WithNextValidators(NextMasternodes);
        builder.WithValidator(Seal()).WithValidators(Masternodes).WithPenalties(Penalised);
        Block block = Build.A.Block.WithHeader(builder.TestObject).TestObject;

        JsonRpcSubscriptionResponse<BlockForRpc> response = new()
        {
            MethodName = SubscriptionMethodName.EthSubscription,
            Params = new JsonRpcSubscriptionResult<BlockForRpc>
            {
                Result = _factory.Create(block, false, SpecProvider()),
                Subscription = "0x1"
            }
        };

        string serialized = RpcTest.SerializeResponse(response);

        Assert.That(serialized, Does.Contain($"\"validator\":\"{Hex(Seal())}\""));
        Assert.That(serialized, Does.Contain("\"validators\":["));
        Assert.That(serialized, Does.Contain("\"nextValidators\":["));
        Assert.That(serialized, Does.Contain("\"penalties\":["));
    }

    [Test]
    public async Task Xdc_chain_resolves_the_xdc_models_over_the_default_ones()
    {
        using XdcTestBlockchain chain = await XdcTestBlockchain.Create(blocksToAdd: 1);
        IBlockForRpcFactory factory = chain.Container.Resolve<IBlockForRpcFactory>();

        Assert.That(factory, Is.TypeOf<XdcBlockForRpcFactory>());
        Assert.That(factory.Create(chain.BlockTree.Head!, false, chain.SpecProvider), Is.TypeOf<XdcBlockForRpc>());
    }

    /// <summary>The block and standalone-header models, which must agree on the XDPoS fields.</summary>
    private JsonElement[] Serialize(XdcBlockHeader header)
    {
        ISpecProvider specProvider = SpecProvider();
        Block block = Build.A.Block.WithHeader(header).TestObject;
        return
        [
            Parse(_factory.Create(block, false, specProvider)),
            Parse(_factory.CreateHeader(header, specProvider))
        ];
    }

    private JsonElement Parse(object model) => JsonDocument.Parse(_serializer.Serialize(model)).RootElement;

    private static Address[] Addresses(JsonElement json, string property)
    {
        JsonElement array = json.GetProperty(property);
        Address[] addresses = new Address[array.GetArrayLength()];
        for (int i = 0; i < addresses.Length; i++)
        {
            addresses[i] = new Address(array[i].GetString()!);
        }
        return addresses;
    }

    private static ISpecProvider SpecProvider()
    {
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(Substitute.For<IReleaseSpec>());
        return specProvider;
    }

    private static byte[] Seal()
    {
        byte[] seal = new byte[65];
        seal[0] = 0xab;
        seal[64] = 0xcd;
        return seal;
    }

    private static byte[] Pack(Address[] addresses)
    {
        byte[] packed = new byte[addresses.Length * Address.Size];
        for (int i = 0; i < addresses.Length; i++)
        {
            addresses[i].Bytes.CopyTo(packed.AsSpan(i * Address.Size));
        }
        return packed;
    }

    private static string Hex(byte[] bytes) => bytes.ToHexString(true);
}
