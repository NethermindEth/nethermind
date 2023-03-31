// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using NUnit.Framework;

namespace Nethermind.Specs.Test.ChainSpecStyle
{
    [Parallelizable(ParallelScope.All)]
    [TestFixture]
    public class ChainSpecLoaderTests
    {
        [Test]
        public void Can_load_hive()
        {
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "Specs/hive.json");
            ChainSpec chainSpec = LoadChainSpec(path);

            Assert.AreEqual("Foundation", chainSpec.Name, $"{nameof(chainSpec.Name)}");
            Assert.AreEqual("ethereum", chainSpec.DataDir, $"{nameof(chainSpec.Name)}");

            Assert.AreEqual((UInt256)0x020000, chainSpec.Ethash.MinimumDifficulty, $"{nameof(chainSpec.Ethash.MinimumDifficulty)}");
            Assert.AreEqual((long)0x0800, chainSpec.Ethash.DifficultyBoundDivisor, $"{nameof(chainSpec.Ethash.DifficultyBoundDivisor)}");
            Assert.AreEqual(0xdL, chainSpec.Ethash.DurationLimit, $"{nameof(chainSpec.Ethash.DurationLimit)}");

            Assert.AreEqual(3, chainSpec.Ethash.BlockRewards.Count, $"{nameof(chainSpec.Ethash.BlockRewards.Count)}");
            Assert.AreEqual((UInt256)5000000000000000000, chainSpec.Ethash.BlockRewards[0L]);
            Assert.AreEqual((UInt256)3000000000000000000, chainSpec.Ethash.BlockRewards[4370000L]);
            Assert.AreEqual((UInt256)2000000000000000000, chainSpec.Ethash.BlockRewards[7080000L]);

            Assert.AreEqual(2, chainSpec.Ethash.DifficultyBombDelays.Count, $"{nameof(chainSpec.Ethash.DifficultyBombDelays.Count)}");
            Assert.AreEqual(3000000L, chainSpec.Ethash.DifficultyBombDelays[4370000]);
            Assert.AreEqual(2000000L, chainSpec.Ethash.DifficultyBombDelays[7080000L]);

            Assert.AreEqual(0L, chainSpec.Ethash.HomesteadTransition);
            Assert.AreEqual(1920000L, chainSpec.Ethash.DaoHardforkTransition);
            Assert.AreEqual(new Address("0xbf4ed7b27f1d666546e30d74d50d173d20bca754"), chainSpec.Ethash.DaoHardforkBeneficiary);
            Assert.AreEqual(0, chainSpec.Ethash.DaoHardforkAccounts.Length);
            Assert.AreEqual(0L, chainSpec.Ethash.Eip100bTransition);

            Assert.AreEqual(1, chainSpec.ChainId, $"{nameof(chainSpec.ChainId)}");
            Assert.AreEqual(1, chainSpec.NetworkId, $"{nameof(chainSpec.NetworkId)}");
            Assert.NotNull(chainSpec.Genesis, $"{nameof(ChainSpec.Genesis)}");

            Assert.AreEqual(1.GWei(), chainSpec.Parameters.Eip1559BaseFeeInitialValue, $"initial base fee value");
            Assert.AreEqual((long)1, chainSpec.Parameters.Eip1559ElasticityMultiplier, $"elasticity multiplier");
            Assert.AreEqual((UInt256)7, chainSpec.Parameters.Eip1559BaseFeeMaxChangeDenominator, $"base fee max change denominator");
            Assert.AreEqual((UInt256)11, chainSpec.Genesis.BaseFeePerGas, $"genesis base fee");

            Assert.AreEqual(0xdeadbeefdeadbeef, chainSpec.Genesis.Header.Nonce, $"genesis {nameof(BlockHeader.Nonce)}");
            Assert.AreEqual(Keccak.Zero, chainSpec.Genesis.Header.MixHash, $"genesis {nameof(BlockHeader.MixHash)}");
            Assert.AreEqual(0x10, (long)chainSpec.Genesis.Header.Difficulty, $"genesis {nameof(BlockHeader.Difficulty)}");
            Assert.AreEqual(Address.Zero, chainSpec.Genesis.Header.Beneficiary, $"genesis {nameof(BlockHeader.Beneficiary)}");
            Assert.AreEqual(0x00L, (long)chainSpec.Genesis.Header.Timestamp, $"genesis {nameof(BlockHeader.Timestamp)}");
            Assert.AreEqual(Keccak.Zero, chainSpec.Genesis.Header.ParentHash, $"genesis {nameof(BlockHeader.ParentHash)}");
            Assert.AreEqual(
                Bytes.FromHexString("0x0000000000000000000000000000000000000000000000000000000000000000"),
                chainSpec.Genesis.Header.ExtraData,
                $"genesis {nameof(BlockHeader.ExtraData)}");
            Assert.AreEqual(0x8000000L, chainSpec.Genesis.Header.GasLimit, $"genesis {nameof(BlockHeader.GasLimit)}");

            Assert.NotNull(chainSpec.Allocations, $"{nameof(ChainSpec.Allocations)}");
            Assert.AreEqual(1, chainSpec.Allocations.Count, $"allocations count");
            Assert.AreEqual(
                new UInt256(0xf4240),
                chainSpec.Allocations[new Address("0x71562b71999873db5b286df957af199ec94617f7")].Balance,
                "account 0x71562b71999873db5b286df957af199ec94617f7 - balance");

            Assert.AreEqual(
                Bytes.FromHexString("0xabcd"),
                chainSpec.Allocations[new Address("0x71562b71999873db5b286df957af199ec94617f7")].Code,
                "account 0x71562b71999873db5b286df957af199ec94617f7 - code");

            Assert.AreEqual(SealEngineType.Ethash, chainSpec.SealEngineType, "engine");

            Assert.AreEqual((long?)0, chainSpec.HomesteadBlockNumber, "homestead transition");
            Assert.AreEqual((long?)0, chainSpec.TangerineWhistleBlockNumber, "tangerine whistle transition");
            Assert.AreEqual((long?)0, chainSpec.SpuriousDragonBlockNumber, "spurious dragon transition");
            Assert.AreEqual((long?)0, chainSpec.ByzantiumBlockNumber, "byzantium transition");
            Assert.AreEqual((long?)1920000, chainSpec.DaoForkBlockNumber, "dao transition");
            Assert.AreEqual((long?)7080000, chainSpec.ConstantinopleFixBlockNumber, "constantinople transition");

            Assert.AreEqual((long?)24576L, chainSpec.Parameters.MaxCodeSize, "max code size");
            Assert.AreEqual((long?)0L, chainSpec.Parameters.MaxCodeSizeTransition, "max code size transition");
            Assert.AreEqual((long?)0x1388L, chainSpec.Parameters.MinGasLimit, "min gas limit");
            Assert.AreEqual(new Address("0xe3389675d0338462dC76C6f9A3e432550c36A142"), chainSpec.Parameters.Registrar, "registrar");
            Assert.AreEqual((long?)0x1d4c00L, chainSpec.Parameters.ForkBlock, "fork block");
            Assert.AreEqual(new Keccak("0x4985f5ca3d2afbec36529aa96f74de3cc10a2a4a6c44f2157a57d2c6059a11bb"), chainSpec.Parameters.ForkCanonHash, "fork block");

            Assert.AreEqual((long?)0L, chainSpec.Parameters.Eip150Transition, "eip150");
            Assert.AreEqual((long?)0L, chainSpec.Parameters.Eip160Transition, "eip160");
            Assert.AreEqual((long?)0L, chainSpec.Parameters.Eip161abcTransition, "eip161abc");
            Assert.AreEqual((long?)0L, chainSpec.Parameters.Eip161dTransition, "eip161d");
            Assert.AreEqual((long?)0L, chainSpec.Parameters.Eip155Transition, "eip155");
            Assert.AreEqual((long?)0L, chainSpec.Parameters.Eip140Transition, "eip140");
            Assert.AreEqual((long?)0L, chainSpec.Parameters.Eip211Transition, "eip211");
            Assert.AreEqual((long?)0L, chainSpec.Parameters.Eip214Transition, "eip214");
            Assert.AreEqual((long?)0L, chainSpec.Parameters.Eip658Transition, "eip658");
            Assert.AreEqual((long?)7080000L, chainSpec.Parameters.Eip145Transition, "eip145");
            Assert.AreEqual((long?)7080000L, chainSpec.Parameters.Eip1014Transition, "eip1014");
            Assert.AreEqual((long?)7080000L, chainSpec.Parameters.Eip1052Transition, "eip1052");
            Assert.AreEqual((long?)7080000L, chainSpec.Parameters.Eip1283Transition, "eip1283");

            Assert.AreEqual((long)32, chainSpec.Parameters.MaximumExtraDataSize, "extra data");
            Assert.AreEqual((long)0x0400, chainSpec.Parameters.GasLimitBoundDivisor, "gas limit bound divisor");
            Assert.AreEqual((UInt256)0x0, chainSpec.Parameters.AccountStartNonce, "account start nonce");

        }

        private static ChainSpec LoadChainSpec(string path)
        {
            var data = File.ReadAllText(path);
            ChainSpecLoader chainSpecLoader = new(new EthereumJsonSerializer());
            ChainSpec chainSpec = chainSpecLoader.Load(data);
            return chainSpec;
        }

        [Test]
        public void Can_load_ropsten()
        {
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains/ropsten.json");
            ChainSpec chainSpec = LoadChainSpec(path);

            Assert.AreEqual(3, chainSpec.NetworkId, $"{nameof(chainSpec.NetworkId)}");
            Assert.AreEqual("Ropsten Testnet", chainSpec.Name, $"{nameof(chainSpec.Name)}");
            Assert.NotNull(chainSpec.Genesis, $"{nameof(ChainSpec.Genesis)}");

            Assert.AreEqual(1.GWei(), chainSpec.Parameters.Eip1559BaseFeeInitialValue, $"fork base fee");
            Assert.AreEqual(0x0000000000000042UL, chainSpec.Genesis.Header.Nonce, $"genesis {nameof(BlockHeader.Nonce)}");
            Assert.AreEqual(Keccak.Zero, chainSpec.Genesis.Header.MixHash, $"genesis {nameof(BlockHeader.MixHash)}");
            Assert.AreEqual(0x100000L, (long)chainSpec.Genesis.Header.Difficulty, $"genesis {nameof(BlockHeader.Difficulty)}");
            Assert.AreEqual(Address.Zero, chainSpec.Genesis.Header.Beneficiary, $"genesis {nameof(BlockHeader.Beneficiary)}");
            Assert.AreEqual(0x00L, (long)chainSpec.Genesis.Header.Timestamp, $"genesis {nameof(BlockHeader.Timestamp)}");
            Assert.AreEqual(Keccak.Zero, chainSpec.Genesis.Header.ParentHash, $"genesis {nameof(BlockHeader.ParentHash)}");
            Assert.AreEqual(
                Bytes.FromHexString("0x3535353535353535353535353535353535353535353535353535353535353535"),
                chainSpec.Genesis.Header.ExtraData,
                $"genesis {nameof(BlockHeader.ExtraData)}");
            Assert.AreEqual(0x1000000L, chainSpec.Genesis.Header.GasLimit, $"genesis {nameof(BlockHeader.GasLimit)}");

            Assert.NotNull(chainSpec.Allocations, $"{nameof(ChainSpec.Allocations)}");
            Assert.AreEqual(257, chainSpec.Allocations.Count, $"allocations count");
            Assert.AreEqual(
                UInt256.Zero,
                chainSpec.Allocations[new Address("0000000000000000000000000000000000000018")].Balance,
                "account 0000000000000000000000000000000000000018");
            Assert.AreEqual(
                UInt256.One,
                chainSpec.Allocations[new Address("0000000000000000000000000000000000000001")].Balance,
                "account 0000000000000000000000000000000000000001");

            Assert.AreEqual(
                UInt256.Parse("1000000000000000000000000000000"),
                chainSpec.Allocations[new Address("874b54a8bd152966d63f706bae1ffeb0411921e5")].Balance,
                "account 874b54a8bd152966d63f706bae1ffeb0411921e5");

            Assert.AreEqual(SealEngineType.Ethash, chainSpec.SealEngineType, "engine");

            Assert.AreEqual((long?)0, chainSpec.HomesteadBlockNumber, "homestead no");
            Assert.AreEqual(null, chainSpec.DaoForkBlockNumber, "dao no");
            Assert.AreEqual((long?)0, chainSpec.TangerineWhistleBlockNumber, "tw no");
            Assert.AreEqual((long?)10, chainSpec.SpuriousDragonBlockNumber, "sd no");
            Assert.AreEqual((long?)1700000, chainSpec.ByzantiumBlockNumber, "byzantium no");
            Assert.AreEqual((long?)4230000, chainSpec.ConstantinopleBlockNumber, "constantinople no");
            Assert.AreEqual((long?)0x4b5e82, chainSpec.ConstantinopleFixBlockNumber, "constantinople fix no");
            Assert.AreEqual((long?)0x62F756, chainSpec.IstanbulBlockNumber, "istanbul no");

            chainSpec.HomesteadBlockNumber.Should().Be(0L);
            chainSpec.DaoForkBlockNumber.Should().Be(null);
            chainSpec.TangerineWhistleBlockNumber.Should().Be(0L);
            chainSpec.SpuriousDragonBlockNumber.Should().Be(RopstenSpecProvider.SpuriousDragonBlockNumber);
            chainSpec.ByzantiumBlockNumber.Should().Be(RopstenSpecProvider.ByzantiumBlockNumber);
            chainSpec.ConstantinopleBlockNumber.Should().Be(RopstenSpecProvider.ConstantinopleBlockNumber);
            chainSpec.ConstantinopleFixBlockNumber.Should().Be(RopstenSpecProvider.ConstantinopleFixBlockNumber);
            chainSpec.IstanbulBlockNumber.Should().Be(RopstenSpecProvider.IstanbulBlockNumber);
            chainSpec.MuirGlacierNumber.Should().Be(RopstenSpecProvider.MuirGlacierBlockNumber);
            chainSpec.BerlinBlockNumber.Should().Be(RopstenSpecProvider.BerlinBlockNumber);
            chainSpec.LondonBlockNumber.Should().Be(RopstenSpecProvider.LondonBlockNumber);
        }

        [Test]
        public void Can_load_goerli()
        {
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains/goerli.json");
            ChainSpec chainSpec = LoadChainSpec(path);

            Assert.AreEqual(1.GWei(), chainSpec.Parameters.Eip1559BaseFeeInitialValue, $"fork base fee");
            Assert.AreEqual(5, chainSpec.NetworkId, $"{nameof(chainSpec.NetworkId)}");
            Assert.AreEqual("Görli Testnet", chainSpec.Name, $"{nameof(chainSpec.Name)}");
            Assert.AreEqual("goerli", chainSpec.DataDir, $"{nameof(chainSpec.DataDir)}");
            Assert.AreEqual(SealEngineType.Clique, chainSpec.SealEngineType, "engine");

            Assert.AreEqual(15UL, chainSpec.Clique.Period);
            Assert.AreEqual(30000UL, chainSpec.Clique.Epoch);
            Assert.AreEqual(UInt256.Zero, chainSpec.Clique.Reward);

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
        }

        [Test]
        public void Can_load_gnosis()
        {
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains/gnosis.json");
            ChainSpec chainSpec = LoadChainSpec(path);

            Assert.AreEqual(1.GWei(), chainSpec.Parameters.Eip1559BaseFeeInitialValue, $"fork base fee");
            Assert.AreEqual(100, chainSpec.NetworkId, $"{nameof(chainSpec.NetworkId)}");
            Assert.AreEqual("GnosisChain", chainSpec.Name, $"{nameof(chainSpec.Name)}");
            Assert.AreEqual(SealEngineType.AuRa, chainSpec.SealEngineType, "engine");

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

            Assert.AreEqual(1.GWei(), chainSpec.Parameters.Eip1559BaseFeeInitialValue, $"fork base fee");
            Assert.AreEqual(10200, chainSpec.NetworkId, $"{nameof(chainSpec.NetworkId)}");
            Assert.AreEqual("chiado", chainSpec.Name, $"{nameof(chainSpec.Name)}");
            Assert.AreEqual(SealEngineType.AuRa, chainSpec.SealEngineType, "engine");

            chainSpec.Parameters.TerminalTotalDifficulty.ToString()
                .Should().Be("231707791542740786049188744689299064356246512");

            chainSpec.AuRa.WithdrawalContractAddress.ToString(true)
                .Should().Be("0xb97036A26259B7147018913bD58a774cf91acf25");
        }

        [Test]
        public void Can_load_rinkeby()
        {
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains/rinkeby.json");
            ChainSpec chainSpec = LoadChainSpec(path);

            Assert.AreEqual(1.GWei(), chainSpec.Parameters.Eip1559BaseFeeInitialValue, $"fork base fee");
            Assert.AreEqual(4, chainSpec.NetworkId, $"{nameof(chainSpec.NetworkId)}");
            Assert.AreEqual("Rinkeby", chainSpec.Name, $"{nameof(chainSpec.Name)}");
            Assert.AreEqual(SealEngineType.Clique, chainSpec.SealEngineType, "engine");
            Assert.AreEqual((long?)5435345, chainSpec.IstanbulBlockNumber, "istanbul no");

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

            Assert.AreEqual(1.GWei(), chainSpec.Parameters.Eip1559BaseFeeInitialValue, $"fork base fee");
            Assert.AreEqual(1, chainSpec.NetworkId, $"{nameof(chainSpec.NetworkId)}");
            Assert.AreEqual("Ethereum", chainSpec.Name, $"{nameof(chainSpec.Name)}");
            Assert.AreEqual("ethereum", chainSpec.DataDir, $"{nameof(chainSpec.Name)}");
            Assert.AreEqual(SealEngineType.Ethash, chainSpec.SealEngineType, "engine");

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
        }

        [Test]
        public void Can_load_spaceneth()
        {
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains/spaceneth.json");
            ChainSpec chainSpec = LoadChainSpec(path);

            Assert.AreEqual(99, chainSpec.NetworkId, $"{nameof(chainSpec.NetworkId)}");
            Assert.AreEqual("Spaceneth", chainSpec.Name, $"{nameof(chainSpec.Name)}");
            Assert.AreEqual("spaceneth", chainSpec.DataDir, $"{nameof(chainSpec.Name)}");
            Assert.AreEqual(SealEngineType.NethDev, chainSpec.SealEngineType, "engine");

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

            Assert.AreEqual(11155111, chainSpec.NetworkId, $"{nameof(chainSpec.NetworkId)}");
            Assert.AreEqual("Sepolia Testnet", chainSpec.Name, $"{nameof(chainSpec.Name)}");
            Assert.AreEqual("sepolia", chainSpec.DataDir, $"{nameof(chainSpec.Name)}");
            Assert.AreEqual("Ethash", chainSpec.SealEngineType, "engine");

            chainSpec.LondonBlockNumber.Should().Be(0L);
            chainSpec.ShanghaiTimestamp.Should().Be(1677557088);
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
}
