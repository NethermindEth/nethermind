// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using NUnit.Framework;

namespace Nethermind.Specs.Test.ChainSpecStyle;

[Parallelizable(ParallelScope.All)]
[TestFixture]
public class ChainSpecLoaderTests
{
    [Test]
    public void Can_load_hive()
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "Specs/hive.json");
        ChainSpec chainSpec = LoadChainSpec(path);

        Assert.That(chainSpec.Name, Is.EqualTo("Foundation"), $"{nameof(chainSpec.Name)}");
        Assert.That(chainSpec.DataDir, Is.EqualTo("ethereum"), $"{nameof(chainSpec.Name)}");

        Assert.That(chainSpec.Ethash.MinimumDifficulty, Is.EqualTo((UInt256)0x020000), $"{nameof(chainSpec.Ethash.MinimumDifficulty)}");
        Assert.That(chainSpec.Ethash.DifficultyBoundDivisor, Is.EqualTo((long)0x0800), $"{nameof(chainSpec.Ethash.DifficultyBoundDivisor)}");
        Assert.That(chainSpec.Ethash.DurationLimit, Is.EqualTo(0xdL), $"{nameof(chainSpec.Ethash.DurationLimit)}");

        Assert.That(chainSpec.Ethash.BlockRewards.Count, Is.EqualTo(3), $"{nameof(chainSpec.Ethash.BlockRewards.Count)}");
        Assert.That(chainSpec.Ethash.BlockRewards[0L], Is.EqualTo((UInt256)5000000000000000000));
        Assert.That(chainSpec.Ethash.BlockRewards[4370000L], Is.EqualTo((UInt256)3000000000000000000));
        Assert.That(chainSpec.Ethash.BlockRewards[7080000L], Is.EqualTo((UInt256)2000000000000000000));

        Assert.That(chainSpec.Ethash.DifficultyBombDelays.Count, Is.EqualTo(2), $"{nameof(chainSpec.Ethash.DifficultyBombDelays.Count)}");
        Assert.That(chainSpec.Ethash.DifficultyBombDelays[4370000], Is.EqualTo(3000000L));
        Assert.That(chainSpec.Ethash.DifficultyBombDelays[7080000L], Is.EqualTo(2000000L));

        Assert.That(chainSpec.Ethash.HomesteadTransition, Is.EqualTo(0L));
        Assert.That(chainSpec.Ethash.DaoHardforkTransition, Is.EqualTo(1920000L));
        Assert.That(chainSpec.Ethash.DaoHardforkBeneficiary, Is.EqualTo(new Address("0xbf4ed7b27f1d666546e30d74d50d173d20bca754")));
        Assert.That(chainSpec.Ethash.DaoHardforkAccounts.Length, Is.EqualTo(0));
        Assert.That(chainSpec.Ethash.Eip100bTransition, Is.EqualTo(0L));

        Assert.That(chainSpec.ChainId, Is.EqualTo(1), $"{nameof(chainSpec.ChainId)}");
        Assert.That(chainSpec.NetworkId, Is.EqualTo(1), $"{nameof(chainSpec.NetworkId)}");
        Assert.NotNull(chainSpec.Genesis, $"{nameof(ChainSpec.Genesis)}");

        Assert.That(chainSpec.Parameters.Eip1559BaseFeeInitialValue, Is.EqualTo(1.GWei()), $"initial base fee value");
        Assert.That(chainSpec.Parameters.Eip1559ElasticityMultiplier, Is.EqualTo((long)1), $"elasticity multiplier");
        Assert.That(chainSpec.Parameters.Eip1559BaseFeeMaxChangeDenominator, Is.EqualTo((UInt256)7), $"base fee max change denominator");
        Assert.That(chainSpec.Genesis.BaseFeePerGas, Is.EqualTo((UInt256)11), $"genesis base fee");

        Assert.That(chainSpec.Genesis.Header.Nonce, Is.EqualTo(0xdeadbeefdeadbeef), $"genesis {nameof(BlockHeader.Nonce)}");
        Assert.That(chainSpec.Genesis.Header.MixHash, Is.EqualTo(Keccak.Zero), $"genesis {nameof(BlockHeader.MixHash)}");
        Assert.That((long)chainSpec.Genesis.Header.Difficulty, Is.EqualTo(0x10), $"genesis {nameof(BlockHeader.Difficulty)}");
        Assert.That(chainSpec.Genesis.Header.Beneficiary, Is.EqualTo(Address.Zero), $"genesis {nameof(BlockHeader.Beneficiary)}");
        Assert.That((long)chainSpec.Genesis.Header.Timestamp, Is.EqualTo(0x00L), $"genesis {nameof(BlockHeader.Timestamp)}");
        Assert.That(chainSpec.Genesis.Header.ParentHash, Is.EqualTo(Keccak.Zero), $"genesis {nameof(BlockHeader.ParentHash)}");
        Assert.That(
            chainSpec.Genesis.Header.ExtraData, Is.EqualTo(Bytes.FromHexString("0x0000000000000000000000000000000000000000000000000000000000000000")),
            $"genesis {nameof(BlockHeader.ExtraData)}");
        Assert.That(chainSpec.Genesis.Header.GasLimit, Is.EqualTo(0x8000000L), $"genesis {nameof(BlockHeader.GasLimit)}");

        Assert.NotNull(chainSpec.Allocations, $"{nameof(ChainSpec.Allocations)}");
        Assert.That(chainSpec.Allocations.Count, Is.EqualTo(1), $"allocations count");
        Assert.That(
            chainSpec.Allocations[new Address("0x71562b71999873db5b286df957af199ec94617f7")].Balance, Is.EqualTo(new UInt256(0xf4240)),
            "account 0x71562b71999873db5b286df957af199ec94617f7 - balance");

        Assert.That(
            chainSpec.Allocations[new Address("0x71562b71999873db5b286df957af199ec94617f7")].Code, Is.EqualTo(Bytes.FromHexString("0xabcd")),
            "account 0x71562b71999873db5b286df957af199ec94617f7 - code");

        Assert.That(chainSpec.SealEngineType, Is.EqualTo(SealEngineType.Ethash), "engine");

        Assert.That(chainSpec.HomesteadBlockNumber, Is.EqualTo((long?)0), "homestead transition");
        Assert.That(chainSpec.TangerineWhistleBlockNumber, Is.EqualTo((long?)0), "tangerine whistle transition");
        Assert.That(chainSpec.SpuriousDragonBlockNumber, Is.EqualTo((long?)0), "spurious dragon transition");
        Assert.That(chainSpec.ByzantiumBlockNumber, Is.EqualTo((long?)0), "byzantium transition");
        Assert.That(chainSpec.DaoForkBlockNumber, Is.EqualTo((long?)1920000), "dao transition");
        Assert.That(chainSpec.ConstantinopleFixBlockNumber, Is.EqualTo((long?)7080000), "constantinople transition");

        Assert.That(chainSpec.Parameters.MaxCodeSize, Is.EqualTo((long?)24576L), "max code size");
        Assert.That(chainSpec.Parameters.MaxCodeSizeTransition, Is.EqualTo((long?)0L), "max code size transition");
        Assert.That(chainSpec.Parameters.MinGasLimit, Is.EqualTo((long?)0x1388L), "min gas limit");
        Assert.That(chainSpec.Parameters.Registrar, Is.EqualTo(new Address("0xe3389675d0338462dC76C6f9A3e432550c36A142")), "registrar");
        Assert.That(chainSpec.Parameters.ForkBlock, Is.EqualTo((long?)0x1d4c00L), "fork block");
        Assert.That(chainSpec.Parameters.ForkCanonHash, Is.EqualTo(new Keccak("0x4985f5ca3d2afbec36529aa96f74de3cc10a2a4a6c44f2157a57d2c6059a11bb")), "fork block");

        Assert.That(chainSpec.Parameters.Eip150Transition, Is.EqualTo((long?)0L), "eip150");
        Assert.That(chainSpec.Parameters.Eip160Transition, Is.EqualTo((long?)0L), "eip160");
        Assert.That(chainSpec.Parameters.Eip161abcTransition, Is.EqualTo((long?)0L), "eip161abc");
        Assert.That(chainSpec.Parameters.Eip161dTransition, Is.EqualTo((long?)0L), "eip161d");
        Assert.That(chainSpec.Parameters.Eip155Transition, Is.EqualTo((long?)0L), "eip155");
        Assert.That(chainSpec.Parameters.Eip140Transition, Is.EqualTo((long?)0L), "eip140");
        Assert.That(chainSpec.Parameters.Eip211Transition, Is.EqualTo((long?)0L), "eip211");
        Assert.That(chainSpec.Parameters.Eip214Transition, Is.EqualTo((long?)0L), "eip214");
        Assert.That(chainSpec.Parameters.Eip658Transition, Is.EqualTo((long?)0L), "eip658");
        Assert.That(chainSpec.Parameters.Eip145Transition, Is.EqualTo((long?)7080000L), "eip145");
        Assert.That(chainSpec.Parameters.Eip1014Transition, Is.EqualTo((long?)7080000L), "eip1014");
        Assert.That(chainSpec.Parameters.Eip1052Transition, Is.EqualTo((long?)7080000L), "eip1052");
        Assert.That(chainSpec.Parameters.Eip1283Transition, Is.EqualTo((long?)7080000L), "eip1283");

        Assert.That(chainSpec.Parameters.MaximumExtraDataSize, Is.EqualTo((long)32), "extra data");
        Assert.That(chainSpec.Parameters.GasLimitBoundDivisor, Is.EqualTo((long)0x0400), "gas limit bound divisor");
    }

    private static ChainSpec LoadChainSpec(string path)
    {
        var data = File.ReadAllText(path);
        ChainSpecLoader chainSpecLoader = new(new EthereumJsonSerializer());
        ChainSpec chainSpec = chainSpecLoader.Load(data);
        return chainSpec;
    }

    [Test]
    public void Can_load_goerli()
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains/goerli.json");
        ChainSpec chainSpec = LoadChainSpec(path);

        Assert.That(chainSpec.Parameters.Eip1559BaseFeeInitialValue, Is.EqualTo(1.GWei()), $"fork base fee");
        Assert.That(chainSpec.NetworkId, Is.EqualTo(5), $"{nameof(chainSpec.NetworkId)}");
        Assert.That(chainSpec.Name, Is.EqualTo("Görli Testnet"), $"{nameof(chainSpec.Name)}");
        Assert.That(chainSpec.DataDir, Is.EqualTo("goerli"), $"{nameof(chainSpec.DataDir)}");
        Assert.That(chainSpec.SealEngineType, Is.EqualTo(SealEngineType.Clique), "engine");

        Assert.That(chainSpec.Clique.Period, Is.EqualTo(15UL));
        Assert.That(chainSpec.Clique.Epoch, Is.EqualTo(30000UL));
        Assert.That(chainSpec.Clique.Reward, Is.EqualTo(UInt256.Zero));

        chainSpec.HomesteadBlockNumber.Should().Be(0);
        chainSpec.DaoForkBlockNumber.Should().Be(null);
        chainSpec.TangerineWhistleBlockNumber.Should().Be(0);
        chainSpec.SpuriousDragonBlockNumber.Should().Be(0);
        chainSpec.ByzantiumBlockNumber.Should().Be(0);
        chainSpec.ConstantinopleBlockNumber.Should().Be(0);
        chainSpec.ConstantinopleFixBlockNumber.Should().Be(0);
        chainSpec.IstanbulBlockNumber.Should().Be(GoerliSpecProvider.IstanbulBlockNumber);
        chainSpec.MuirGlacierNumber.Should().Be(null);
        chainSpec.BerlinBlockNumber.Should().Be(GoerliSpecProvider.BerlinBlockNumber);
        chainSpec.LondonBlockNumber.Should().Be(GoerliSpecProvider.LondonBlockNumber);
        chainSpec.ShanghaiTimestamp.Should().Be(GoerliSpecProvider.ShanghaiTimestamp);
        chainSpec.ShanghaiTimestamp.Should().Be(GoerliSpecProvider.Instance.TimestampFork);
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

        chainSpec.AuRa.WithdrawalContractAddress.ToString(true)
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

        chainSpec.AuRa.WithdrawalContractAddress.ToString(true)
            .Should().Be("0xb97036A26259B7147018913bD58a774cf91acf25");

        chainSpec.ShanghaiTimestamp.Should().Be(ChiadoSpecProvider.ShanghaiTimestamp);
        chainSpec.ShanghaiTimestamp.Should().Be(ChiadoSpecProvider.Instance.TimestampFork);

    }

    [Test]
    public void Can_load_rinkeby()
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains/rinkeby.json");
        ChainSpec chainSpec = LoadChainSpec(path);

        Assert.That(chainSpec.Parameters.Eip1559BaseFeeInitialValue, Is.EqualTo(1.GWei()), $"fork base fee");
        Assert.That(chainSpec.NetworkId, Is.EqualTo(4), $"{nameof(chainSpec.NetworkId)}");
        Assert.That(chainSpec.Name, Is.EqualTo("Rinkeby"), $"{nameof(chainSpec.Name)}");
        Assert.That(chainSpec.SealEngineType, Is.EqualTo(SealEngineType.Clique), "engine");
        Assert.That(chainSpec.IstanbulBlockNumber, Is.EqualTo((long?)5435345), "istanbul no");

        // chainSpec.HomesteadBlockNumber.Should().Be(RinkebySpecProvider.HomesteadBlockNumber);
        chainSpec.DaoForkBlockNumber.Should().Be(null);
        chainSpec.TangerineWhistleBlockNumber.Should().Be(RinkebySpecProvider.TangerineWhistleBlockNumber);
        chainSpec.SpuriousDragonBlockNumber.Should().Be(RinkebySpecProvider.SpuriousDragonBlockNumber);
        chainSpec.ByzantiumBlockNumber.Should().Be(RinkebySpecProvider.ByzantiumBlockNumber);
        chainSpec.ConstantinopleBlockNumber.Should().Be(RinkebySpecProvider.ConstantinopleBlockNumber);
        chainSpec.ConstantinopleFixBlockNumber.Should().Be(RinkebySpecProvider.ConstantinopleFixBlockNumber);
        chainSpec.IstanbulBlockNumber.Should().Be(RinkebySpecProvider.IstanbulBlockNumber);
        chainSpec.BerlinBlockNumber.Should().Be(RinkebySpecProvider.BerlinBlockNumber);
        chainSpec.LondonBlockNumber.Should().Be(RinkebySpecProvider.LondonBlockNumber);
    }

    [Test]
    public void Can_load_mainnet()
    {
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
        Assert.That(chainSpec.SealEngineType, Is.EqualTo("Ethash"), "engine");

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
        chainSpec.CancunTimestamp.Should().Be(HoleskySpecProvider.CancunTimestamp);
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
        chainSpec.AuRa.RewriteBytecode.Should().BeEquivalentTo(expected);
    }
}
