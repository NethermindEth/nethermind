// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.Configuration;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.LightClient.Test;

public class EezLightClientOptionsTests
{
    private const string Bootnode = "enode://81863f47e9bd652585d3f78b4b2ee07b93dad603fd9bc3c293e1244250725998adc88da0cef48f1de89b15ab92b15db8f43dc2b6fb8fbd86a6f217a1dd886701@193.70.55.37:30303";
    private const string GenesisHash = "0xb6bd210b7e824a1aabc4c1ae5b64f5643715441b332c7152e641a36ba01835b2";
    private const string RegistryAddress = "0x4b2760f84b83d6b3829e30a7573b0172b6d6402c";

    [Test]
    public void Absent_genesis_keeps_ethereum_mode() =>
        Assert.That(EezLightClientOptions.Read(Configuration([])), Is.Null);

    [Test]
    public void Complete_options_describe_rollup_network_and_settlement()
    {
        EezLightClientOptions options = EezLightClientOptions.Read(Configuration(Complete()), 9050, 9051, 30307)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(options.Settlement, Is.EqualTo(new EezSettlement(new Address(RegistryAddress), 2, 696_991)));
            Assert.That(options.Network.Name, Is.EqualTo("rollupx"));
            Assert.That(options.Network.GenesisHash, Is.EqualTo(new Hash256(GenesisHash)));
            Assert.That(options.Network.ChainSpecFile, Is.EqualTo(Path.GetFullPath("rollupx.json")));
            Assert.That(options.Network.Bootnodes, Has.Length.EqualTo(1));
            Assert.That(options.Port, Is.EqualTo(30308));
        }
    }

    [TestCase("eez-genesis-hash")]
    [TestCase("eez-registry")]
    [TestCase("eez-rollup-id")]
    [TestCase("eez-chain-id")]
    public void Missing_required_option_is_rejected(string option)
    {
        Dictionary<string, string?> values = Complete();
        values.Remove(option);

        Assert.That(() => EezLightClientOptions.Read(Configuration(values)), Throws.ArgumentException);
    }

    [TestCase("eez-chain-id", "0")]
    [TestCase("eez-port", "0")]
    [TestCase("eez-port", "30307")]
    [TestCase("eez-bootnodes", " ")]
    public void Invalid_option_is_rejected(string option, string value)
    {
        Dictionary<string, string?> values = Complete();
        values[option] = value;

        Assert.That(() => EezLightClientOptions.Read(Configuration(values), 9050, 9051, 30307), Throws.InstanceOf<ArgumentException>());
    }

    private static Dictionary<string, string?> Complete() => new()
    {
        ["eez-genesis"] = "rollupx.json",
        ["eez-genesis-hash"] = GenesisHash,
        ["eez-registry"] = RegistryAddress,
        ["eez-rollup-id"] = "2",
        ["eez-chain-id"] = "696991",
        ["eez-bootnodes"] = Bootnode,
    };

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
