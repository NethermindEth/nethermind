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

using System.Globalization;
using System.IO;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Json;
using Nethermind.Core.Specs.ChainSpecStyle;
using Nethermind.Core.Specs.GenesisFileStyle;
using Nethermind.Dirichlet.Numerics;
using NUnit.Framework;

namespace Nethermind.Core.Test.Specs.GenesisFileStyle
{
    [TestFixture]
    public class GenesisFileLoaderTests
    {
        [Test]
        public void Can_load_a_private_network_file()
        {
            byte[] data = File.ReadAllBytes(Path.Combine(TestContext.CurrentContext.WorkDirectory, "Specs/genesis_test.json"));
            GenesisFileLoader loader = new GenesisFileLoader(new EthereumJsonSerializer());
            ChainSpec chainSpec = loader.Load(data);
            Assert.AreEqual(22082, chainSpec.ChainId, $"{nameof(chainSpec.ChainId)}");
            Assert.AreEqual(null, chainSpec.Name, $"{nameof(chainSpec.Name)}");
            Assert.NotNull(chainSpec.Genesis, $"{nameof(Core.Specs.ChainSpecStyle.ChainSpec.Genesis)}");

            Assert.AreEqual(0x0000000000000000UL, chainSpec.Genesis.Header.Nonce, $"genesis {nameof(BlockHeader.Nonce)}");
            Assert.AreEqual(Keccak.Zero, chainSpec.Genesis.Header.MixHash, $"genesis {nameof(BlockHeader.MixHash)}");
            Assert.AreEqual(1L, (long)chainSpec.Genesis.Header.Difficulty, $"genesis {nameof(BlockHeader.Difficulty)}");
            Assert.AreEqual(Address.Zero, chainSpec.Genesis.Header.Beneficiary, $"genesis {nameof(BlockHeader.Beneficiary)}");
            Assert.AreEqual(0x5c28d2a1L, (long)chainSpec.Genesis.Header.Timestamp, $"genesis {nameof(BlockHeader.Timestamp)}");
            Assert.AreEqual(Keccak.Zero, chainSpec.Genesis.Header.ParentHash, $"genesis {nameof(BlockHeader.ParentHash)}");
            Assert.AreEqual(
                Bytes.FromHexString("0x000000000000000000000000000000000000000000000000000000000000000020b2e4bb8688a44729780d15dc64adb42f9f5a0a746526c3a59db995b914a319306cd7ae35dc50c5aa42104423e00a862b616f2f712a1b17d308bbc90000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"),
                chainSpec.Genesis.Header.ExtraData,
                $"genesis {nameof(BlockHeader.ExtraData)}");
            Assert.AreEqual(4700000L, chainSpec.Genesis.Header.GasLimit, $"genesis {nameof(BlockHeader.GasLimit)}");
            
            Assert.NotNull(chainSpec.Allocations, $"{nameof(Core.Specs.ChainSpecStyle.ChainSpec.Allocations)}");
            Assert.AreEqual(16, chainSpec.Allocations.Count, $"allocations count");
            Assert.AreEqual(
                UInt256.Parse("200000000000000000000000000000000000000000000000000000000000000", NumberStyles.HexNumber),
                chainSpec.Allocations[new Address("20b2e4bb8688a44729780d15dc64adb42f9f5a0a")].Balance,
                "account 20b2e4bb8688a44729780d15dc64adb42f9f5a0a");
            
            Assert.AreEqual(SealEngineType.Clique, chainSpec.SealEngineType, "engine");
            Assert.AreEqual(15UL, chainSpec.Clique.Period, "Clique.period");
            Assert.AreEqual(30000UL, chainSpec.Clique.Epoch, "Clique.epoch");
            Assert.AreEqual((UInt256?)UInt256.Zero, chainSpec.Clique.Reward, "Clique.reward");
            
            Assert.AreEqual((long?)1, chainSpec.HomesteadBlockNumber, "homestead no");
            Assert.AreEqual((long?)2, chainSpec.TangerineWhistleBlockNumber, "tw no");
            Assert.AreEqual((long?)3, chainSpec.SpuriousDragonBlockNumber, "sd no");
            Assert.AreEqual((long?)4, chainSpec.ByzantiumBlockNumber, "byzantium no");
            Assert.AreEqual((long?)5, chainSpec.ConstantinopleBlockNumber, "constantinople no");
            Assert.AreEqual(null, chainSpec.DaoForkBlockNumber, "dao no");
        }
    }
}