// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using NUnit.Framework;

namespace Nethermind.AuRa.Test;

public class ChainSpecLoaderTest
{
    private static ChainSpec LoadChainSpec(string path)
    {
        var loader = new ChainSpecFileLoader(new EthereumJsonSerializer(), LimboTraceLogger.Instance);
        var chainSpec = loader.LoadEmbeddedOrFromFile(path);
        return chainSpec;
    }

    [Test]
    public void Can_load_gnosis()
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains/gnosis.json");
        ChainSpec chainSpec = LoadChainSpec(path);

        Assert.That(chainSpec.Parameters.Eip1559BaseFeeInitialValue, Is.EqualTo(1.GWei()), $"fork base fee");
        Assert.That(chainSpec.NetworkId, Is.EqualTo(100), $"{nameof(chainSpec.NetworkId)}");
        Assert.That(chainSpec.Name, Is.EqualTo("GnosisChain"), $"{nameof(chainSpec.Name)}");
        Assert.That(chainSpec.SealEngineType, Is.EqualTo(SealEngineType.AuRa), "engine");

        int berlinGnosisBlockNumber = 16101500;
        chainSpec.Parameters.Eip2565Transition.Should().Be(berlinGnosisBlockNumber);
        chainSpec.Parameters.Eip2929Transition.Should().Be(berlinGnosisBlockNumber);
        chainSpec.Parameters.Eip2930Transition.Should().Be(berlinGnosisBlockNumber);

        chainSpec.Parameters.TerminalTotalDifficulty.ToString()
            .Should().Be("8626000000000000000000058750000000000000000000");

        var auraParams = chainSpec.EngineChainSpecParametersProvider.GetChainSpecParameters<AuRaChainSpecEngineParameters>();

        auraParams.WithdrawalContractAddress.ToString(true)
            .Should().Be("0x0B98057eA310F4d31F2a452B414647007d1645d9");
    }

    [Test]
    public void Can_load_chiado()
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains/chiado.json");
        ChainSpec chainSpec = LoadChainSpec(path);

        Assert.That(chainSpec.Parameters.Eip1559BaseFeeInitialValue, Is.EqualTo(1.GWei()), $"fork base fee");
        Assert.That(chainSpec.NetworkId, Is.EqualTo(10200), $"{nameof(chainSpec.NetworkId)}");
        Assert.That(chainSpec.Name, Is.EqualTo("chiado"), $"{nameof(chainSpec.Name)}");
        Assert.That(chainSpec.SealEngineType, Is.EqualTo(SealEngineType.AuRa), "engine");

        chainSpec.Parameters.TerminalTotalDifficulty.ToString()
            .Should().Be("231707791542740786049188744689299064356246512");

        var auraParams = chainSpec.EngineChainSpecParametersProvider.GetChainSpecParameters<AuRaChainSpecEngineParameters>();

        auraParams.WithdrawalContractAddress.ToString(true)
            .Should().Be("0xb97036A26259B7147018913bD58a774cf91acf25");

        chainSpec.ShanghaiTimestamp.Should().Be(ChiadoSpecProvider.ShanghaiTimestamp);
        chainSpec.ShanghaiTimestamp.Should().Be(ChiadoSpecProvider.Instance.TimestampFork);
    }

    [Test]
    public void Can_load_posdao_with_rewriteBytecode()
    {
        // TODO: modexp 2565
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "Specs/posdao.json");
        ChainSpec chainSpec = LoadChainSpec(path);
        IDictionary<long, IDictionary<Address, byte[]>> expected = new Dictionary<long, IDictionary<Address, byte[]>>
        {
            {
                21300000, new Dictionary<Address, byte[]>()
                {
                    {new Address("0x1234000000000000000000000000000000000001"), Bytes.FromHexString("0x111")},
                    {new Address("0x1234000000000000000000000000000000000002"), Bytes.FromHexString("0x222")},
                }
            }
        };

        AuRaChainSpecEngineParameters auraParams = chainSpec.EngineChainSpecParametersProvider.GetChainSpecParameters<AuRaChainSpecEngineParameters>();
        auraParams.RewriteBytecode.Should().BeEquivalentTo(expected);
    }

    [Test]
    public void Can_load_gnosis_with_rewriteBytecodeGnosis()
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains/gnosis.json");
        ChainSpec chainSpec = LoadChainSpec(path);
        IDictionary<ulong, IDictionary<Address, byte[]>> expected = new Dictionary<ulong, IDictionary<Address, byte[]>>
        {
            {
                GnosisSpecProvider.BalancerTimestamp, new Dictionary<Address, byte[]>()
                {
                    {new Address("0x506d1f9efe24f0d47853adca907eb8d89ae03207"), Bytes.FromHexString("0x60806040526004361061002c575f3560e01c80638da5cb5b14610037578063b61d27f61461006157610033565b3661003357005b5f5ffd5b348015610042575f5ffd5b5061004b610091565b604051610058919061030a565b60405180910390f35b61007b600480360381019061007691906103e9565b6100a9565b60405161008891906104ca565b60405180910390f35b737be579238a6a621601eae2c346cda54d68f7dfee81565b60606100b3610247565b5f73ffffffffffffffffffffffffffffffffffffffff168573ffffffffffffffffffffffffffffffffffffffff1603610121576040517f08c379a000000000000000000000000000000000000000000000000000000000815260040161011890610544565b60405180910390fd5b5f8573ffffffffffffffffffffffffffffffffffffffff163b1161017a576040517f08c379a0000000000000000000000000000000000000000000000000000000008152600401610171906105ac565b60405180910390fd5b5f5f8673ffffffffffffffffffffffffffffffffffffffff168686866040516101a4929190610606565b5f6040518083038185875af1925050503d805f81146101de576040519150601f19603f3d011682016040523d82523d5f602084013e6101e3565b606091505b50915091508161023a575f815111156101ff5780518060208301fd5b6040517f08c379a000000000000000000000000000000000000000000000000000000000815260040161023190610668565b60405180910390fd5b8092505050949350505050565b737be579238a6a621601eae2c346cda54d68f7dfee73ffffffffffffffffffffffffffffffffffffffff163373ffffffffffffffffffffffffffffffffffffffff16146102c9576040517f08c379a00000000000000000000000000000000000000000000000000000000081526004016102c0906106d0565b60405180910390fd5b565b5f73ffffffffffffffffffffffffffffffffffffffff82169050919050565b5f6102f4826102cb565b9050919050565b610304816102ea565b82525050565b5f60208201905061031d5f8301846102fb565b92915050565b5f5ffd5b5f5ffd5b610334816102ea565b811461033e575f5ffd5b50565b5f8135905061034f8161032b565b92915050565b5f819050919050565b61036781610355565b8114610371575f5ffd5b50565b5f813590506103828161035e565b92915050565b5f5ffd5b5f5ffd5b5f5ffd5b5f5f83601f8401126103a9576103a8610388565b5b8235905067ffffffffffffffff8111156103c6576103c561038c565b5b6020830191508360018202830111156103e2576103e1610390565b5b9250929050565b5f5f5f5f6060858703121561040157610400610323565b5b5f61040e87828801610341565b945050602061041f87828801610374565b935050604085013567ffffffffffffffff8111156104405761043f610327565b5b61044c87828801610394565b925092505092959194509250565b5f81519050919050565b5f82825260208201905092915050565b8281835e5f83830152505050565b5f601f19601f8301169050919050565b5f61049c8261045a565b6104a68185610464565b93506104b6818560208601610474565b6104bf81610482565b840191505092915050565b5f6020820190508181035f8301526104e28184610492565b905092915050565b5f82825260208201905092915050565b7f7a65726f207461726765740000000000000000000000000000000000000000005f82015250565b5f61052e600b836104ea565b9150610539826104fa565b602082019050919050565b5f6020820190508181035f83015261055b81610522565b9050919050565b7f6e6f74206120636f6e74726163740000000000000000000000000000000000005f82015250565b5f610596600e836104ea565b91506105a182610562565b602082019050919050565b5f6020820190508181035f8301526105c38161058a565b9050919050565b5f81905092915050565b828183375f83830152505050565b5f6105ed83856105ca565b93506105fa8385846105d4565b82840190509392505050565b5f6106128284866105e2565b91508190509392505050565b7f63616c6c206661696c65640000000000000000000000000000000000000000005f82015250565b5f610652600b836104ea565b915061065d8261061e565b602082019050919050565b5f6020820190508181035f83015261067f81610646565b9050919050565b7f6e6f74206f776e657200000000000000000000000000000000000000000000005f82015250565b5f6106ba6009836104ea565b91506106c582610686565b602082019050919050565b5f6020820190508181035f8301526106e7816106ae565b905091905056fea2646970667358221220a8334a26f31db2a806db6c1bcc4107caa8ec5cbdc7b742cfec99b4f0cca066a364736f6c634300081e0033")},
                }
            }
        };

        AuRaChainSpecEngineParameters auraParams = chainSpec.EngineChainSpecParametersProvider.GetChainSpecParameters<AuRaChainSpecEngineParameters>();

        auraParams.RewriteBytecodeTimestamp.Should().BeEquivalentTo(expected);
    }
}
