/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.IO;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Json;
using Nethermind.Core.Specs.ChainSpec;
using Nethermind.Dirichlet.Numerics;
using NUnit.Framework;

namespace Nethermind.Core.Test.Specs
{
    [TestFixture]
    public class ChainSpecLoaderTests
    {
        [Test]
        public void Can_load_hive()
        {
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "Specs\\hive.json");
            ChainSpec chainSpec = LoadChainSpec(path);

            Assert.AreEqual("Foundation", chainSpec.Name, $"{nameof(chainSpec.Name)}");
            Assert.AreEqual("ethereum", chainSpec.DataDir, $"{nameof(chainSpec.Name)}");

            Assert.AreEqual((UInt256)0x020000, chainSpec.Ethash.MinimumDifficulty, $"{nameof(chainSpec.Ethash.MinimumDifficulty)}");
            Assert.AreEqual((UInt256)0x0800, chainSpec.Ethash.DifficultyBoundDivisor, $"{nameof(chainSpec.Ethash.DifficultyBoundDivisor)}");
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
                chainSpec.Allocations[new Address("0x71562b71999873db5b286df957af199ec94617f7")],
                "account 0x71562b71999873db5b286df957af199ec94617f7");

            Assert.AreEqual(SealEngineType.Ethash, chainSpec.SealEngineType, "engine");
            
            Assert.AreEqual((long?)0, chainSpec.HomesteadBlockNumber, "homestead transition");
            Assert.AreEqual((long?)0, chainSpec.TangerineWhistleBlockNumber, "tangerine whistle transition");
            Assert.AreEqual((long?)0, chainSpec.SpuriousDragonBlockNumber, "spurious dragon transition");
            Assert.AreEqual((long?)0, chainSpec.ByzantiumBlockNumber, "byzantium transition");
            Assert.AreEqual((long?)1920000, chainSpec.DaoForkBlockNumber, "dao transition");
            Assert.AreEqual((long?)7080000, chainSpec.ConstantinopleBlockNumber, "constantinople transition");
        }

        private static ChainSpec LoadChainSpec(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            ChainSpecLoader chainSpecLoader = new ChainSpecLoader(new EthereumJsonSerializer());
            ChainSpec chainSpec = chainSpecLoader.Load(data);
            return chainSpec;
        }

        [Test]
        public void Can_load_ropsten()
        {
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains/ropsten.json");
            ChainSpec chainSpec = LoadChainSpec(path);
            
            Assert.AreEqual(3, chainSpec.ChainId, $"{nameof(chainSpec.ChainId)}");
            Assert.AreEqual("Ropsten", chainSpec.Name, $"{nameof(chainSpec.Name)}");
            Assert.NotNull(chainSpec.Genesis, $"{nameof(ChainSpec.Genesis)}");

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
                chainSpec.Allocations[new Address("0000000000000000000000000000000000000018")],
                "account 0000000000000000000000000000000000000018");
            Assert.AreEqual(
                UInt256.One,
                chainSpec.Allocations[new Address("0000000000000000000000000000000000000001")],
                "account 0000000000000000000000000000000000000001");
            
            Assert.AreEqual(
                UInt256.Parse("1000000000000000000000000000000"),
                chainSpec.Allocations[new Address("874b54a8bd152966d63f706bae1ffeb0411921e5")],
                "account 874b54a8bd152966d63f706bae1ffeb0411921e5");
            
            Assert.AreEqual(SealEngineType.Ethash, chainSpec.SealEngineType, "engine");
            
            Assert.AreEqual((long?)0, chainSpec.HomesteadBlockNumber, "homestead no");
            Assert.AreEqual(null, chainSpec.DaoForkBlockNumber, "dao no");
            Assert.AreEqual((long?)0, chainSpec.TangerineWhistleBlockNumber, "tw no");
            Assert.AreEqual((long?)10, chainSpec.SpuriousDragonBlockNumber, "sd no");
            Assert.AreEqual((long?)1700000, chainSpec.ByzantiumBlockNumber, "byzantium no");
            Assert.AreEqual((long?)4230000, chainSpec.ConstantinopleBlockNumber, "constantinople no");
        }
        
        [Test]
        public void Can_load_goerli()
        {
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains/goerli.json");
            ChainSpec chainSpec = LoadChainSpec(path);
            
            Assert.AreEqual(5, chainSpec.ChainId, $"{nameof(chainSpec.ChainId)}");
            Assert.AreEqual("Görli Testnet", chainSpec.Name, $"{nameof(chainSpec.Name)}");
            Assert.AreEqual("goerli", chainSpec.DataDir, $"{nameof(chainSpec.DataDir)}");
            Assert.AreEqual(SealEngineType.Clique, chainSpec.SealEngineType, "engine");
            
            Assert.AreEqual(15UL, chainSpec.Clique.Period);
            Assert.AreEqual(30000UL, chainSpec.Clique.Epoch);
            Assert.AreEqual(UInt256.Zero, chainSpec.Clique.Reward);
            
            Assert.AreEqual(null, chainSpec.HomesteadBlockNumber, "homestead no");
            Assert.AreEqual(null, chainSpec.DaoForkBlockNumber, "dao no");
            Assert.AreEqual((long?)0, chainSpec.TangerineWhistleBlockNumber, "tw no");
            Assert.AreEqual((long?)0, chainSpec.SpuriousDragonBlockNumber, "sd no");
            Assert.AreEqual((long?)0, chainSpec.ByzantiumBlockNumber, "byzantium no");
            Assert.AreEqual((long?)0, chainSpec.ConstantinopleBlockNumber, "constantinople no");
        }
        
        [Test]
        public void Can_load_rinkeby()
        {
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains/rinkeby.json");
            ChainSpec chainSpec = LoadChainSpec(path);

            Assert.AreEqual(4, chainSpec.ChainId, $"{nameof(chainSpec.ChainId)}");
            Assert.AreEqual("Rinkeby", chainSpec.Name, $"{nameof(chainSpec.Name)}");
            Assert.AreEqual(SealEngineType.Clique, chainSpec.SealEngineType, "engine");
        }
        
        [Test]
        public void Can_load_mainnet()
        {
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains/foundation.json");
            ChainSpec chainSpec = LoadChainSpec(path);
            
            Assert.AreEqual(1, chainSpec.ChainId, $"{nameof(chainSpec.ChainId)}");
            Assert.AreEqual("Foundation", chainSpec.Name, $"{nameof(chainSpec.Name)}");
            Assert.AreEqual("ethereum", chainSpec.DataDir, $"{nameof(chainSpec.Name)}");
            Assert.AreEqual(SealEngineType.Ethash, chainSpec.SealEngineType, "engine");
            
            Assert.AreEqual((long?)1150000, chainSpec.HomesteadBlockNumber, "homestead no");
            Assert.AreEqual((long?)1920000, chainSpec.DaoForkBlockNumber, "dao no");
            Assert.AreEqual((long?)2463000, chainSpec.TangerineWhistleBlockNumber, "tw no");
            Assert.AreEqual((long?)2675000, chainSpec.SpuriousDragonBlockNumber, "sd no");
            Assert.AreEqual((long?)4370000, chainSpec.ByzantiumBlockNumber, "byzantium no");
            Assert.AreEqual((long?)7080000, chainSpec.ConstantinopleBlockNumber, "constantinople no");
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
        }
    }
}