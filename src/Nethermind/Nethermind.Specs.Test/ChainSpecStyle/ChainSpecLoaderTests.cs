// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using FluentAssertions;
using Nethermind.Consensus.Ethash;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using NUnit.Framework;

namespace Nethermind.Specs.Test.ChainSpecStyle;

[Parallelizable(ParallelScope.All)]
public class ChainSpecLoaderTests
{
    private static ChainSpec LoadChainSpec(string path)
    {
        var loader = new ChainSpecFileLoader(new EthereumJsonSerializer(), LimboTraceLogger.Instance);
        var chainSpec = loader.LoadEmbeddedOrFromFile(path);
        return chainSpec;
    }

    [Test]
    public void Can_load_mainnet()
    {
        new EthashChainSpecEngineParameters();
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains/foundation.json");
        ChainSpec chainSpec = LoadChainSpec(path);

        Assert.That(chainSpec.Parameters.Eip1559BaseFeeInitialValue, Is.EqualTo(1.GWei()), $"fork base fee");
        Assert.That(chainSpec.NetworkId, Is.EqualTo(1), $"{nameof(chainSpec.NetworkId)}");
        Assert.That(chainSpec.Name, Is.EqualTo("Ethereum"), $"{nameof(chainSpec.Name)}");
        Assert.That(chainSpec.DataDir, Is.EqualTo("ethereum"), $"{nameof(chainSpec.Name)}");
        Assert.That(chainSpec.SealEngineType, Is.EqualTo(SealEngineType.Ethash), "engine");

        chainSpec.HomesteadBlockNumber.Should().Be(MainnetSpecProvider.HomesteadBlockNumber);
        chainSpec.DaoForkBlockNumber.Should().Be(1920000);
        chainSpec.TangerineWhistleBlockNumber.Should().Be(MainnetSpecProvider.TangerineWhistleBlockNumber);
        chainSpec.SpuriousDragonBlockNumber.Should().Be(MainnetSpecProvider.SpuriousDragonBlockNumber);
        chainSpec.ByzantiumBlockNumber.Should().Be(MainnetSpecProvider.ByzantiumBlockNumber);
        chainSpec.ConstantinopleBlockNumber.Should().Be(null);
        chainSpec.ConstantinopleFixBlockNumber.Should().Be(MainnetSpecProvider.ConstantinopleFixBlockNumber);
        chainSpec.IstanbulBlockNumber.Should().Be(MainnetSpecProvider.IstanbulBlockNumber);
        chainSpec.MuirGlacierNumber.Should().Be(MainnetSpecProvider.MuirGlacierBlockNumber);
        chainSpec.BerlinBlockNumber.Should().Be(MainnetSpecProvider.BerlinBlockNumber);
        chainSpec.LondonBlockNumber.Should().Be(MainnetSpecProvider.LondonBlockNumber);
        chainSpec.ArrowGlacierBlockNumber.Should().Be(MainnetSpecProvider.ArrowGlacierBlockNumber);
        chainSpec.GrayGlacierBlockNumber.Should().Be(MainnetSpecProvider.GrayGlacierBlockNumber);
        chainSpec.ShanghaiTimestamp.Should().Be(MainnetSpecProvider.ShanghaiBlockTimestamp);
        chainSpec.ShanghaiTimestamp.Should().Be(MainnetSpecProvider.Instance.TimestampFork);
    }

    [Test]
    public void Can_load_spaceneth()
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains/spaceneth.json");
        ChainSpec chainSpec = LoadChainSpec(path);

        Assert.That(chainSpec.NetworkId, Is.EqualTo(99), $"{nameof(chainSpec.NetworkId)}");
        Assert.That(chainSpec.Name, Is.EqualTo("Spaceneth"), $"{nameof(chainSpec.Name)}");
        Assert.That(chainSpec.DataDir, Is.EqualTo("spaceneth"), $"{nameof(chainSpec.Name)}");
        Assert.That(chainSpec.SealEngineType, Is.EqualTo(SealEngineType.NethDev), "engine");

        chainSpec.HomesteadBlockNumber.Should().Be(0L);
        chainSpec.DaoForkBlockNumber.Should().Be(null);
        chainSpec.TangerineWhistleBlockNumber.Should().Be(0L);
        chainSpec.SpuriousDragonBlockNumber.Should().Be(0L);
        chainSpec.ByzantiumBlockNumber.Should().Be(0L);
        chainSpec.ConstantinopleBlockNumber.Should().Be(0L);
        chainSpec.ConstantinopleFixBlockNumber.Should().Be(0L);
        chainSpec.IstanbulBlockNumber.Should().Be(0L);
        chainSpec.MuirGlacierNumber.Should().Be(null);
        chainSpec.BerlinBlockNumber.Should().Be(0L);
        chainSpec.LondonBlockNumber.Should().Be(0L);
        chainSpec.ArrowGlacierBlockNumber.Should().Be(null);
        chainSpec.GrayGlacierBlockNumber.Should().Be(null);
    }

    [Test]
    public void Can_load_sepolia()
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains/sepolia.json");
        ChainSpec chainSpec = LoadChainSpec(path);

        Assert.That(chainSpec.NetworkId, Is.EqualTo(11155111), $"{nameof(chainSpec.NetworkId)}");
        Assert.That(chainSpec.Name, Is.EqualTo("Sepolia Testnet"), $"{nameof(chainSpec.Name)}");
        Assert.That(chainSpec.DataDir, Is.EqualTo("sepolia"), $"{nameof(chainSpec.Name)}");
        Assert.That(chainSpec.SealEngineType, Is.EqualTo(SealEngineType.Ethash), "engine");

        chainSpec.LondonBlockNumber.Should().Be(0L);
        chainSpec.ShanghaiTimestamp.Should().Be(1677557088);
    }

    [Test]
    public void Can_load_holesky()
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains/holesky.json");
        ChainSpec chainSpec = LoadChainSpec(path);

        Assert.That(chainSpec.NetworkId, Is.EqualTo(17000), $"{nameof(chainSpec.NetworkId)}");
        Assert.That(chainSpec.Name, Is.EqualTo("Holesky Testnet"), $"{nameof(chainSpec.Name)}");
        Assert.That(chainSpec.DataDir, Is.EqualTo("holesky"), $"{nameof(chainSpec.DataDir)}");
        Assert.That(chainSpec.SealEngineType, Is.EqualTo(SealEngineType.Ethash), "engine");

        chainSpec.DaoForkBlockNumber.Should().Be(null);
        chainSpec.TangerineWhistleBlockNumber.Should().Be(0);
        chainSpec.SpuriousDragonBlockNumber.Should().Be(0);
        chainSpec.ByzantiumBlockNumber.Should().Be(0);
        chainSpec.ConstantinopleBlockNumber.Should().Be(0);
        chainSpec.ConstantinopleFixBlockNumber.Should().Be(0);
        chainSpec.IstanbulBlockNumber.Should().Be(0);
        chainSpec.BerlinBlockNumber.Should().Be(0);
        chainSpec.LondonBlockNumber.Should().Be(0);
        chainSpec.ShanghaiTimestamp.Should().Be(HoleskySpecProvider.ShanghaiTimestamp);
        chainSpec.ShanghaiTimestamp.Should().Be(HoleskySpecProvider.Instance.TimestampFork);
        // chainSpec.CancunTimestamp.Should().Be(HoleskySpecProvider.CancunTimestamp);
    }


    [Test]
    public void Can_load_hoodi()
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains/hoodi.json");
        ChainSpec chainSpec = LoadChainSpec(path);

        Assert.That(chainSpec.NetworkId, Is.EqualTo(560048), $"{nameof(chainSpec.NetworkId)}");
        Assert.That(chainSpec.Name, Is.EqualTo("Hoodi Testnet"), $"{nameof(chainSpec.Name)}");
        Assert.That(chainSpec.DataDir, Is.EqualTo("hoodi"), $"{nameof(chainSpec.DataDir)}");
        Assert.That(chainSpec.SealEngineType, Is.EqualTo(SealEngineType.Ethash), "engine");

        chainSpec.DaoForkBlockNumber.Should().Be(null);
        chainSpec.TangerineWhistleBlockNumber.Should().Be(0);
        chainSpec.SpuriousDragonBlockNumber.Should().Be(0);
        chainSpec.ByzantiumBlockNumber.Should().Be(0);
        chainSpec.ConstantinopleBlockNumber.Should().Be(0);
        chainSpec.ConstantinopleFixBlockNumber.Should().Be(0);
        chainSpec.IstanbulBlockNumber.Should().Be(0);
        chainSpec.BerlinBlockNumber.Should().Be(0);
        chainSpec.LondonBlockNumber.Should().Be(0);
        chainSpec.ShanghaiTimestamp.Should().Be(0);
        chainSpec.CancunTimestamp.Should().Be(0);
        chainSpec.PragueTimestamp.Should().Be(HoodiSpecProvider.PragueTimestamp);
    }

    [Test]
    public void Can_load_posdao_with_openethereum_pricing_transitions()
    {
        // TODO: modexp 2565
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "Specs/posdao.json");
        ChainSpec chainSpec = LoadChainSpec(path);
        chainSpec.Parameters.Eip152Transition.Should().Be(15);
        chainSpec.Parameters.Eip1108Transition.Should().Be(10);
    }
}
