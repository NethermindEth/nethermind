//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
            
            Assert.AreEqual(3, chainSpec.ChainId, $"{nameof(chainSpec.ChainId)}");
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
        }
        
        [Test]
        public void Can_load_goerli()
        {
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains/goerli.json");
            ChainSpec chainSpec = LoadChainSpec(path);
            
            Assert.AreEqual(1.GWei(), chainSpec.Parameters.Eip1559BaseFeeInitialValue, $"fork base fee");
            Assert.AreEqual(5, chainSpec.ChainId, $"{nameof(chainSpec.ChainId)}");
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
        }
        
        [Test]
        public void Can_load_xdai()
        {
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains/xdai.json");
            ChainSpec chainSpec = LoadChainSpec(path);
            
            Assert.AreEqual(1.GWei(), chainSpec.Parameters.Eip1559BaseFeeInitialValue, $"fork base fee");
            Assert.AreEqual(100, chainSpec.ChainId, $"{nameof(chainSpec.ChainId)}");
            Assert.AreEqual("DaiChain", chainSpec.Name, $"{nameof(chainSpec.Name)}");
            Assert.AreEqual(SealEngineType.AuRa, chainSpec.SealEngineType, "engine");

            int berlinXdaiBlockNumber = 16101500;
            chainSpec.Parameters.Eip2565Transition.Should().Be(berlinXdaiBlockNumber);
            chainSpec.Parameters.Eip2929Transition.Should().Be(berlinXdaiBlockNumber);
            chainSpec.Parameters.Eip2930Transition.Should().Be(berlinXdaiBlockNumber);
        }
        
        [Test]
        public void Can_load_rinkeby()
        {
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains/rinkeby.json");
            ChainSpec chainSpec = LoadChainSpec(path);
            
            Assert.AreEqual(1.GWei(), chainSpec.Parameters.Eip1559BaseFeeInitialValue, $"fork base fee");
            Assert.AreEqual(4, chainSpec.ChainId, $"{nameof(chainSpec.ChainId)}");
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
        }
        
        [Test]
        public void Can_load_mainnet()
        {
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains/foundation.json");
            ChainSpec chainSpec = LoadChainSpec(path);
            
            Assert.AreEqual(1.GWei(), chainSpec.Parameters.Eip1559BaseFeeInitialValue, $"fork base fee");
            Assert.AreEqual(1, chainSpec.ChainId, $"{nameof(chainSpec.ChainId)}");
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
        }
        
        [Test]
        public void Can_load_sokol()
        {
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains/sokol.json");
            ChainSpec chainSpec = LoadChainSpec(path);
            
            Assert.AreEqual(1.GWei(), chainSpec.Parameters.Eip1559BaseFeeInitialValue, $"fork base fee");
            Assert.AreEqual(0x4d, chainSpec.ChainId, $"{nameof(chainSpec.ChainId)}");
            Assert.AreEqual("Sokol", chainSpec.Name, $"{nameof(chainSpec.Name)}");
            Assert.AreEqual(SealEngineType.AuRa, chainSpec.SealEngineType, "engine");
            Assert.NotNull(chainSpec.AuRa, "AuRa");
            Assert.AreEqual(0, chainSpec.AuRa.MaximumUncleCount, "maximum uncle count");
            Assert.AreEqual(0L, chainSpec.AuRa.MaximumUncleCountTransition, "maximum uncle count tr");
            Assert.AreEqual(5L, chainSpec.AuRa.StepDuration[0], "step duration");
            Assert.AreEqual(UInt256.Parse("1000000000000000000"), chainSpec.AuRa.BlockReward[0], "rew");
            Assert.AreEqual(4639000, chainSpec.AuRa.BlockRewardContractTransition, "rew tr");
            Assert.AreEqual(new Address("0x3145197AD50D7083D0222DE4fCCf67d9BD05C30D"), chainSpec.AuRa.BlockRewardContractAddress, "rew add");
            
            Assert.AreEqual(new Address("0x8bf38d4764929064f2d4d3a56520a76ab3df415b"), chainSpec.AuRa.Validators.Validators[0].Addresses.First(), "val 0");
            Assert.AreEqual(new Address("0xf5cE3f5D0366D6ec551C74CCb1F67e91c56F2e34"), chainSpec.AuRa.Validators.Validators[362296].Addresses.First(), "val 362296");
            Assert.AreEqual(new Address("0x03048F666359CFD3C74a1A5b9a97848BF71d5038"), chainSpec.AuRa.Validators.Validators[509355].Addresses.First(), "val 509355");
            Assert.AreEqual(new Address("0x4c6a159659CCcb033F4b2e2Be0C16ACC62b89DDB"), chainSpec.AuRa.Validators.Validators[4622420].Addresses.First(), "val 4622420");
            
            Assert.AreEqual(0, chainSpec.HomesteadBlockNumber, "homestead no");
            Assert.AreEqual(null, chainSpec.DaoForkBlockNumber, "dao no");
            
            Assert.AreEqual((long?)0, chainSpec.Parameters.Eip140Transition, "eip140");
            
            Assert.AreEqual((long?)0, chainSpec.Parameters.Eip150Transition, "eip150");
            Assert.AreEqual((long?)0, chainSpec.Parameters.Eip160Transition, "eip160");
            Assert.AreEqual((long?)0, chainSpec.Parameters.Eip161abcTransition, "eip161abc");
            Assert.AreEqual((long?)0, chainSpec.Parameters.Eip161dTransition, "eip161d");
            
            Assert.AreEqual((long?)0, chainSpec.TangerineWhistleBlockNumber, "tw no");
            Assert.AreEqual((long?)0, chainSpec.SpuriousDragonBlockNumber, "sd no");

            Assert.AreEqual((long?)0, chainSpec.ByzantiumBlockNumber, "byzantium no");
            Assert.AreEqual((long?)6464300, chainSpec.ConstantinopleBlockNumber, "constantinople no");
            Assert.AreEqual((long?)7026400, chainSpec.ConstantinopleFixBlockNumber, "constantinople fix no");
            Assert.AreEqual((long?)12095200, chainSpec.Parameters.Eip1706Transition, "eip2200");
            Assert.AreEqual((long?)12095200, chainSpec.Parameters.Eip1283ReenableTransition, "eip2200");
            
            Assert.AreEqual("0x606060405260008060006101000a81548160ff0219169083151502179055506000600460006101000a81548160ff0219169083151502179055506000600460016101000a81548160ff02191690831515021790555073fffffffffffffffffffffffffffffffffffffffe600560006101000a81548173ffffffffffffffffffffffffffffffffffffffff021916908373ffffffffffffffffffffffffffffffffffffffff1602179055503415620000b557600080fd5b604051602080620018c98339810160405280805190602001909190505060008073ffffffffffffffffffffffffffffffffffffffff168273ffffffffffffffffffffffffffffffffffffffff16141515156200011057600080fd5b81600460026101000a81548173ffffffffffffffffffffffffffffffffffffffff021916908373ffffffffffffffffffffffffffffffffffffffff160217905550602060405190810160405280600460029054906101000a900473ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff168152506006906001620001c1929190620002cb565b50600090505b600680549050811015620002a257604080519081016040528060011515815260200182815250600960006006848154811015156200020157fe5b906000526020600020900160009054906101000a900473ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff16815260200190815260200160002060008201518160000160006101000a81548160ff021916908315150217905550602082015181600101559050508080600101915050620001c7565b60068054905060088190555060066007908054620002c29291906200035a565b505050620003f7565b82805482825590600052602060002090810192821562000347579160200282015b82811115620003465782518260006101000a81548173ffffffffffffffffffffffffffffffffffffffff021916908373ffffffffffffffffffffffffffffffffffffffff16021790555091602001919060010190620002ec565b5b509050620003569190620003b1565b5090565b8280548282559060005260206000209081019282156200039e5760005260206000209182015b828111156200039d57825482559160010191906001019062000380565b5b509050620003ad9190620003b1565b5090565b620003f491905b80821115620003f057600081816101000a81549073ffffffffffffffffffffffffffffffffffffffff021916905550600101620003b8565b5090565b90565b6114c280620004076000396000f3006060604052600436106100fc576000357c0100000000000000000000000000000000000000000000000000000000900463ffffffff16806303aca79214610101578063108552691461016457806340a141ff1461019d57806340c9cdeb146101d65780634110a489146101ff57806345199e0a1461025757806349285b58146102c15780634d238c8e14610316578063752862111461034f578063900eb5a8146103645780639a573786146103c7578063a26a47d21461041c578063ae4b1b5b14610449578063b3f05b971461049e578063b7ab4db5146104cb578063d3e848f114610535578063fa81b2001461058a578063facd743b146105df575b600080fd5b341561010c57600080fd5b6101226004808035906020019091905050610630565b604051808273ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff16815260200191505060405180910390f35b341561016f57600080fd5b61019b600480803573ffffffffffffffffffffffffffffffffffffffff1690602001909190505061066f565b005b34156101a857600080fd5b6101d4600480803573ffffffffffffffffffffffffffffffffffffffff16906020019091905050610807565b005b34156101e157600080fd5b6101e9610bb7565b6040518082815260200191505060405180910390f35b341561020a57600080fd5b610236600480803573ffffffffffffffffffffffffffffffffffffffff16906020019091905050610bbd565b60405180831515151581526020018281526020019250505060405180910390f35b341561026257600080fd5b61026a610bee565b6040518080602001828103825283818151815260200191508051906020019060200280838360005b838110156102ad578082015181840152602081019050610292565b505050509050019250505060405180910390f35b34156102cc57600080fd5b6102d4610c82565b604051808273ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff16815260200191505060405180910390f35b341561032157600080fd5b61034d600480803573ffffffffffffffffffffffffffffffffffffffff16906020019091905050610d32565b005b341561035a57600080fd5b610362610fcc565b005b341561036f57600080fd5b61038560048080359060200190919050506110fc565b604051808273ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff16815260200191505060405180910390f35b34156103d257600080fd5b6103da61113b565b604051808273ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff16815260200191505060405180910390f35b341561042757600080fd5b61042f6111eb565b604051808215151515815260200191505060405180910390f35b341561045457600080fd5b61045c6111fe565b604051808273ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff16815260200191505060405180910390f35b34156104a957600080fd5b6104b1611224565b604051808215151515815260200191505060405180910390f35b34156104d657600080fd5b6104de611237565b6040518080602001828103825283818151815260200191508051906020019060200280838360005b83811015610521578082015181840152602081019050610506565b505050509050019250505060405180910390f35b341561054057600080fd5b6105486112cb565b604051808273ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff16815260200191505060405180910390f35b341561059557600080fd5b61059d6112f1565b604051808273ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff16815260200191505060405180910390f35b34156105ea57600080fd5b610616600480803573ffffffffffffffffffffffffffffffffffffffff16906020019091905050611317565b604051808215151515815260200191505060405180910390f35b60078181548110151561063f57fe5b90600052602060002090016000915054906101000a900473ffffffffffffffffffffffffffffffffffffffff1681565b600460029054906101000a900473ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff163373ffffffffffffffffffffffffffffffffffffffff161415156106cb57600080fd5b600460019054906101000a900460ff161515156106e757600080fd5b600073ffffffffffffffffffffffffffffffffffffffff168173ffffffffffffffffffffffffffffffffffffffff161415151561072357600080fd5b80600a60006101000a81548173ffffffffffffffffffffffffffffffffffffffff021916908373ffffffffffffffffffffffffffffffffffffffff1602179055506001600460016101000a81548160ff0219169083151502179055507f600bcf04a13e752d1e3670a5a9f1c21177ca2a93c6f5391d4f1298d098097c22600a60009054906101000a900473ffffffffffffffffffffffffffffffffffffffff16604051808273ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff16815260200191505060405180910390a150565b600080600061081461113b565b73ffffffffffffffffffffffffffffffffffffffff163373ffffffffffffffffffffffffffffffffffffffff1614151561084d57600080fd5b83600960008273ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff16815260200190815260200160002060000160009054906101000a900460ff1615156108a957600080fd5b600960008673ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff168152602001908152602001600020600101549350600160078054905003925060078381548110151561090857fe5b906000526020600020900160009054906101000a900473ffffffffffffffffffffffffffffffffffffffff1691508160078581548110151561094657fe5b906000526020600020900160006101000a81548173ffffffffffffffffffffffffffffffffffffffff021916908373ffffffffffffffffffffffffffffffffffffffff16021790555083600960008473ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff168152602001908152602001600020600101819055506007838154811015156109e557fe5b906000526020600020900160006101000a81549073ffffffffffffffffffffffffffffffffffffffff02191690556000600780549050111515610a2757600080fd5b6007805480919060019003610a3c9190611370565b506000600960008773ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff168152602001908152602001600020600101819055506000600960008773ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff16815260200190815260200160002060000160006101000a81548160ff0219169083151502179055506000600460006101000a81548160ff0219169083151502179055506001430340600019167f55252fa6eee4741b4e24a74a70e9c11fd2c2281df8d6ea13126ff845f7825c89600760405180806020018281038252838181548152602001915080548015610ba257602002820191906000526020600020905b8160009054906101000a900473ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff1681526020019060010190808311610b58575b50509250505060405180910390a25050505050565b60085481565b60096020528060005260406000206000915090508060000160009054906101000a900460ff16908060010154905082565b610bf661139c565b6007805480602002602001604051908101604052809291908181526020018280548015610c7857602002820191906000526020600020905b8160009054906101000a900473ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff1681526020019060010190808311610c2e575b5050505050905090565b6000600a60009054906101000a900473ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff166349285b586000604051602001526040518163ffffffff167c0100000000000000000000000000000000000000000000000000000000028152600401602060405180830381600087803b1515610d1257600080fd5b6102c65a03f11515610d2357600080fd5b50505060405180519050905090565b610d3a61113b565b73ffffffffffffffffffffffffffffffffffffffff163373ffffffffffffffffffffffffffffffffffffffff16141515610d7357600080fd5b80600960008273ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff16815260200190815260200160002060000160009054906101000a900460ff16151515610dd057600080fd5b600073ffffffffffffffffffffffffffffffffffffffff168273ffffffffffffffffffffffffffffffffffffffff1614151515610e0c57600080fd5b6040805190810160405280600115158152602001600780549050815250600960008473ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff16815260200190815260200160002060008201518160000160006101000a81548160ff0219169083151502179055506020820151816001015590505060078054806001018281610ea991906113b0565b9160005260206000209001600084909190916101000a81548173ffffffffffffffffffffffffffffffffffffffff021916908373ffffffffffffffffffffffffffffffffffffffff160217905550506000600460006101000a81548160ff0219169083151502179055506001430340600019167f55252fa6eee4741b4e24a74a70e9c11fd2c2281df8d6ea13126ff845f7825c89600760405180806020018281038252838181548152602001915080548015610fba57602002820191906000526020600020905b8160009054906101000a900473ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff1681526020019060010190808311610f70575b50509250505060405180910390a25050565b600560009054906101000a900473ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff163373ffffffffffffffffffffffffffffffffffffffff161480156110365750600460009054906101000a900460ff16155b151561104157600080fd5b6001600460006101000a81548160ff0219169083151502179055506007600690805461106e9291906113dc565b506006805490506008819055507f8564cd629b15f47dc310d45bcbfc9bcf5420b0d51bf0659a16c67f91d27632536110a4611237565b6040518080602001828103825283818151815260200191508051906020019060200280838360005b838110156110e75780820151818401526020810190506110cc565b505050509050019250505060405180910390a1565b60068181548110151561110b57fe5b90600052602060002090016000915054906101000a900473ffffffffffffffffffffffffffffffffffffffff1681565b6000600a60009054906101000a900473ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff16639a5737866000604051602001526040518163ffffffff167c0100000000000000000000000000000000000000000000000000000000028152600401602060405180830381600087803b15156111cb57600080fd5b6102c65a03f115156111dc57600080fd5b50505060405180519050905090565b600460019054906101000a900460ff1681565b600a60009054906101000a900473ffffffffffffffffffffffffffffffffffffffff1681565b600460009054906101000a900460ff1681565b61123f61139c565b60068054806020026020016040519081016040528092919081815260200182805480156112c157602002820191906000526020600020905b8160009054906101000a900473ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff1681526020019060010190808311611277575b5050505050905090565b600560009054906101000a900473ffffffffffffffffffffffffffffffffffffffff1681565b600460029054906101000a900473ffffffffffffffffffffffffffffffffffffffff1681565b6000600960008373ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff16815260200190815260200160002060000160009054906101000a900460ff169050919050565b81548183558181151161139757818360005260206000209182019101611396919061142e565b5b505050565b602060405190810160405280600081525090565b8154818355818115116113d7578183600052602060002091820191016113d6919061142e565b5b505050565b82805482825590600052602060002090810192821561141d5760005260206000209182015b8281111561141c578254825591600101919060010190611401565b5b50905061142a9190611453565b5090565b61145091905b8082111561144c576000816000905550600101611434565b5090565b90565b61149391905b8082111561148f57600081816101000a81549073ffffffffffffffffffffffffffffffffffffffff021916905550600101611459565b5090565b905600a165627a7a7230582036ea35935c8246b68074adece2eab70c40e69a0193c08a6277ce06e5b25188510029000000000000000000000000e8ddc5c7a2d2f0d7a9798459c0104fdf5e987aca", chainSpec.Allocations[new Address("0x8bf38d4764929064f2d4d3a56520a76ab3df415b")].Constructor?.ToHexString(true), "constantinople no");
        }
        
        [Test]
        public void Can_load_spaceneth()
        {
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains/spaceneth.json");
            ChainSpec chainSpec = LoadChainSpec(path);
            
            Assert.AreEqual(99, chainSpec.ChainId, $"{nameof(chainSpec.ChainId)}");
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
        }
        
        [Test]
        public void Can_load_catalyst()
        {
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains/catalyst.json");
            ChainSpec chainSpec = LoadChainSpec(path);
            
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
            chainSpec.SealEngineType.Should().Be("Eth2Merge");
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
}
