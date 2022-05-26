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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using NUnit.Framework;
using NUnit.Framework.Internal.Commands;

namespace Nethermind.Specs.Test.ChainSpecStyle
{
    [Parallelizable(ParallelScope.All)]
    [TestFixture]
    public class ChainSpecBasedSpecProviderTests
    {
        [Test]
        public void Sepolia_loads_properly()
        {
            ChainSpecLoader loader = new(new EthereumJsonSerializer());
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../Chains/sepolia.json");
            ChainSpec chainSpec = loader.Load(File.ReadAllText(path));
            chainSpec.Parameters.Eip2537Transition.Should().BeNull();

            ChainSpecBasedSpecProvider provider = new(chainSpec);
            SepoliaSpecProvider sepolia = SepoliaSpecProvider.Instance;

            List<long> blockNumbersToTest = new()
            {
                120_000_000, // far in the future
            };

            CompareSpecProviders(sepolia, provider, blockNumbersToTest);
            Assert.AreEqual(0, provider.GenesisSpec.Eip1559TransitionBlock);
            Assert.AreEqual(long.MaxValue, provider.GenesisSpec.DifficultyBombDelay);
        }

        [Test]
        public void Rinkeby_loads_properly()
        {
            ChainSpecLoader loader = new(new EthereumJsonSerializer());
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../Chains/rinkeby.json");
            ChainSpec chainSpec = loader.Load(File.ReadAllText(path));
            chainSpec.Parameters.Eip2537Transition.Should().BeNull();

            ChainSpecBasedSpecProvider provider = new(chainSpec);
            RinkebySpecProvider rinkeby = RinkebySpecProvider.Instance;

            List<long> blockNumbersToTest = new()
            {
                RinkebySpecProvider.ByzantiumBlockNumber,
                RinkebySpecProvider.ConstantinopleFixBlockNumber - 1,
                RinkebySpecProvider.ConstantinopleFixBlockNumber,
                RinkebySpecProvider.IstanbulBlockNumber - 1,
                RinkebySpecProvider.IstanbulBlockNumber,
                RinkebySpecProvider.BerlinBlockNumber - 1,
                RinkebySpecProvider.BerlinBlockNumber,
                RinkebySpecProvider.LondonBlockNumber - 1,
                RinkebySpecProvider.LondonBlockNumber,
                120_000_000, // far in the future
            };

            CompareSpecProviders(rinkeby, provider, blockNumbersToTest);
            Assert.AreEqual(RinkebySpecProvider.LondonBlockNumber, provider.GenesisSpec.Eip1559TransitionBlock);
        }

        [Test]
        public void Goerli_loads_properly()
        {
            ChainSpecLoader loader = new(new EthereumJsonSerializer());
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../Chains/goerli.json");
            ChainSpec chainSpec = loader.Load(File.ReadAllText(path));
            chainSpec.Parameters.Eip2537Transition.Should().BeNull();

            ChainSpecBasedSpecProvider provider = new(chainSpec);
            GoerliSpecProvider goerli = GoerliSpecProvider.Instance;

            List<long> blockNumbersToTest = new()
            {
                0,
                1,
                GoerliSpecProvider.IstanbulBlockNumber - 1,
                GoerliSpecProvider.IstanbulBlockNumber,
                GoerliSpecProvider.BerlinBlockNumber - 1,
                GoerliSpecProvider.BerlinBlockNumber,
                GoerliSpecProvider.LondonBlockNumber - 1,
                GoerliSpecProvider.LondonBlockNumber,
                100000000, // far in the future
            };

            CompareSpecProviders(goerli, provider, blockNumbersToTest);
            Assert.AreEqual(GoerliSpecProvider.LondonBlockNumber, provider.GenesisSpec.Eip1559TransitionBlock);
        }

        [Test]
        public void Mainnet_loads_properly()
        {
            ChainSpecLoader loader = new(new EthereumJsonSerializer());
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../Chains/foundation.json");
            ChainSpec chainSpec = loader.Load(File.ReadAllText(path));
            chainSpec.Parameters.Eip2537Transition.Should().BeNull();

            ChainSpecBasedSpecProvider provider = new(chainSpec);
            MainnetSpecProvider mainnet = MainnetSpecProvider.Instance;

            List<long> blockNumbersToTest = new()
            {
                0,
                1,
                MainnetSpecProvider.HomesteadBlockNumber - 1,
                MainnetSpecProvider.HomesteadBlockNumber,
                MainnetSpecProvider.TangerineWhistleBlockNumber - 1,
                MainnetSpecProvider.TangerineWhistleBlockNumber,
                MainnetSpecProvider.SpuriousDragonBlockNumber - 1,
                MainnetSpecProvider.SpuriousDragonBlockNumber,
                MainnetSpecProvider.ByzantiumBlockNumber - 1,
                MainnetSpecProvider.ByzantiumBlockNumber,
                MainnetSpecProvider.ConstantinopleFixBlockNumber - 1,
                MainnetSpecProvider.ConstantinopleFixBlockNumber,
                MainnetSpecProvider.IstanbulBlockNumber - 1,
                MainnetSpecProvider.IstanbulBlockNumber,
                MainnetSpecProvider.MuirGlacierBlockNumber - 1,
                MainnetSpecProvider.MuirGlacierBlockNumber,
                MainnetSpecProvider.BerlinBlockNumber - 1,
                MainnetSpecProvider.BerlinBlockNumber,
                MainnetSpecProvider.LondonBlockNumber - 1,
                MainnetSpecProvider.LondonBlockNumber,
                MainnetSpecProvider.ArrowGlacierBlockNumber - 1,
                MainnetSpecProvider.ArrowGlacierBlockNumber,
                99_000_000, // far in the future
            };

            CompareSpecProviders(mainnet, provider, blockNumbersToTest);

            Assert.AreEqual(MainnetSpecProvider.LondonBlockNumber, provider.GenesisSpec.Eip1559TransitionBlock);
            Assert.AreEqual(0_000_000, provider.GetSpec(4_369_999).DifficultyBombDelay);
            Assert.AreEqual(3_000_000, provider.GetSpec(4_370_000).DifficultyBombDelay);
            Assert.AreEqual(3_000_000, provider.GetSpec(7_279_999).DifficultyBombDelay);
            Assert.AreEqual(3_000_000, provider.GetSpec(7_279_999).DifficultyBombDelay);
            Assert.AreEqual(5_000_000, provider.GetSpec(7_280_000).DifficultyBombDelay);
            Assert.AreEqual(5_000_000, provider.GetSpec(9_199_999).DifficultyBombDelay);
            Assert.AreEqual(9_000_000, provider.GetSpec(9_200_000).DifficultyBombDelay);
            Assert.AreEqual(9_000_000, provider.GetSpec(12_000_000).DifficultyBombDelay);
            Assert.AreEqual(9_000_000, provider.GetSpec(12_964_999).DifficultyBombDelay);
            Assert.AreEqual(9_700_000, provider.GetSpec(12_965_000).DifficultyBombDelay);
            Assert.AreEqual(9_700_000, provider.GetSpec(13_772_999).DifficultyBombDelay);
            Assert.AreEqual(10_700_000, provider.GetSpec(13_773_000).DifficultyBombDelay);
            Assert.AreEqual(10_700_000, provider.GetSpec(99_414_000).DifficultyBombDelay);
        }

        private static void CompareSpecProviders(
            ISpecProvider oldSpecProvider,
            ISpecProvider newSpecProvider,
            IEnumerable<long> blockNumbers,
            bool checkDifficultyBomb = false)
        {
            foreach (long blockNumber in blockNumbers)
            {
                IReleaseSpec oldSpec = oldSpecProvider.GetSpec(blockNumber);
                IReleaseSpec newSpec = newSpecProvider.GetSpec(blockNumber);
                long? daoBlockNumber = newSpecProvider.DaoBlockNumber;
                bool isMainnet = daoBlockNumber != null;

                CompareSpecs(oldSpec, newSpec, blockNumber, isMainnet, checkDifficultyBomb);
            }
        }

        private static void CompareSpecs(IReleaseSpec oldSpec, IReleaseSpec newSpec, long blockNumber, bool isMainnet,
            bool checkDifficultyBomb = false)
        {
            PropertyInfo[] propertyInfos =
                typeof(IReleaseSpec).GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (PropertyInfo propertyInfo in propertyInfos
                         .Where(p => p.Name != nameof(IReleaseSpec.Name))
                         .Where(p => isMainnet || p.Name != nameof(IReleaseSpec.MaximumExtraDataSize))
                         .Where(p => isMainnet || p.Name != nameof(IReleaseSpec.BlockReward))
                         .Where(p => isMainnet || checkDifficultyBomb ||
                                     p.Name != nameof(IReleaseSpec.DifficultyBombDelay))
                         .Where(p => isMainnet || checkDifficultyBomb ||
                                     p.Name != nameof(IReleaseSpec.DifficultyBoundDivisor))
                         .Where(p => p.Name != nameof(IReleaseSpec.Eip1559TransitionBlock)))
            {
                Assert.AreEqual(propertyInfo.GetValue(oldSpec), propertyInfo.GetValue(newSpec),
                    blockNumber + "." + propertyInfo.Name);
            }
        }

        [Test]
        public void Ropsten_loads_properly()
        {
            ChainSpecLoader loader = new(new EthereumJsonSerializer());
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../Chains/ropsten.json");
            ChainSpec chainSpec = loader.Load(File.ReadAllText(path));
            chainSpec.Parameters.Eip2537Transition.Should().BeNull();

            ChainSpecBasedSpecProvider provider = new(chainSpec);
            RopstenSpecProvider ropsten = RopstenSpecProvider.Instance;

            List<long> blockNumbersToTest = new()
            {
                0,
                1,
                RopstenSpecProvider.SpuriousDragonBlockNumber - 1,
                RopstenSpecProvider.SpuriousDragonBlockNumber,
                RopstenSpecProvider.ByzantiumBlockNumber - 1,
                RopstenSpecProvider.ByzantiumBlockNumber,
                RopstenSpecProvider.ConstantinopleFixBlockNumber - 1,
                RopstenSpecProvider.ConstantinopleFixBlockNumber,
                RopstenSpecProvider.IstanbulBlockNumber - 1,
                RopstenSpecProvider.IstanbulBlockNumber,
                RopstenSpecProvider.MuirGlacierBlockNumber - 1,
                RopstenSpecProvider.MuirGlacierBlockNumber,
                RopstenSpecProvider.BerlinBlockNumber - 1,
                RopstenSpecProvider.BerlinBlockNumber,
                RopstenSpecProvider.LondonBlockNumber - 1,
                RopstenSpecProvider.LondonBlockNumber,
                999_999_999, // far in the future
            };

            CompareSpecProviders(ropsten, provider, blockNumbersToTest, true);
            Assert.AreEqual(RopstenSpecProvider.LondonBlockNumber, provider.GenesisSpec.Eip1559TransitionBlock);
        }

        [Test]
        public void Chain_id_is_set_correctly()
        {
            ChainSpec chainSpec = new() { Parameters = new ChainParameters(), ChainId = 5 };

            ChainSpecBasedSpecProvider provider = new(chainSpec);
            Assert.AreEqual(5, provider.ChainId);
        }

        [Test]
        public void Dao_block_number_is_set_correctly()
        {
            ChainSpec chainSpec = new();
            chainSpec.Parameters = new ChainParameters();
            chainSpec.DaoForkBlockNumber = 23;

            ChainSpecBasedSpecProvider provider = new(chainSpec);
            Assert.AreEqual(23, provider.DaoBlockNumber);
        }

        [Test]
        public void Bound_divisors_set_correctly()
        {
            ChainSpec chainSpec = new()
            {
                Parameters = new ChainParameters { GasLimitBoundDivisor = 17 },
                Ethash = new EthashParameters { DifficultyBoundDivisor = 19 }
            };

            ChainSpecBasedSpecProvider provider = new(chainSpec);
            Assert.AreEqual(19, provider.GenesisSpec.DifficultyBoundDivisor);
            Assert.AreEqual(17, provider.GenesisSpec.GasLimitBoundDivisor);
        }

        [Test]
        public void Difficulty_bomb_delays_loaded_correctly()
        {
            ChainSpec chainSpec = new()
            {
                Parameters = new ChainParameters(),
                Ethash = new EthashParameters
                {
                    DifficultyBombDelays = new Dictionary<long, long>
                    {
                        { 3, 100 },
                        { 7, 200 },
                        { 13, 300 },
                        { 17, 400 },
                        { 19, 500 },
                    }
                }
            };

            ChainSpecBasedSpecProvider provider = new(chainSpec);
            Assert.AreEqual(100, provider.GetSpec(3).DifficultyBombDelay);
            Assert.AreEqual(300, provider.GetSpec(7).DifficultyBombDelay);
            Assert.AreEqual(600, provider.GetSpec(13).DifficultyBombDelay);
            Assert.AreEqual(1000, provider.GetSpec(17).DifficultyBombDelay);
            Assert.AreEqual(1500, provider.GetSpec(19).DifficultyBombDelay);
        }

        [Test]
        public void Max_code_transition_loaded_correctly()
        {
            const long maxCodeTransition = 13;
            const long maxCodeSize = 100;

            ChainSpec chainSpec = new()
            {
                Parameters = new ChainParameters
                {
                    MaxCodeSizeTransition = maxCodeTransition, MaxCodeSize = maxCodeSize
                }
            };

            ChainSpecBasedSpecProvider provider = new(chainSpec);
            Assert.AreEqual(long.MaxValue, provider.GetSpec(maxCodeTransition - 1).MaxCodeSize, "one before");
            Assert.AreEqual(maxCodeSize, provider.GetSpec(maxCodeTransition).MaxCodeSize, "at transition");
            Assert.AreEqual(maxCodeSize, provider.GetSpec(maxCodeTransition + 1).MaxCodeSize, "one after");
        }

        [Test]
        public void Eip2200_is_set_correctly_directly()
        {
            ChainSpec chainSpec = new() { Parameters = new ChainParameters { Eip2200Transition = 5 } };

            ChainSpecBasedSpecProvider provider = new(chainSpec);
            provider.GetSpec(5).IsEip2200Enabled.Should().BeTrue();
        }

        [Test]
        public void Eip2200_is_set_correctly_indirectly()
        {
            ChainSpec chainSpec =
                new() { Parameters = new ChainParameters { Eip1706Transition = 5, Eip1283Transition = 5 } };

            ChainSpecBasedSpecProvider provider = new(chainSpec);
            provider.GetSpec(5).IsEip2200Enabled.Should().BeTrue();
        }

        [Test]
        public void Eip2200_is_set_correctly_indirectly_after_disabling_eip1283_and_reenabling()
        {
            ChainSpec chainSpec = new()
            {
                Parameters = new ChainParameters
                {
                    Eip1706Transition = 5,
                    Eip1283Transition = 1,
                    Eip1283DisableTransition = 4,
                    Eip1283ReenableTransition = 5
                }
            };

            ChainSpecBasedSpecProvider provider = new(chainSpec);
            provider.GetSpec(5).IsEip2200Enabled.Should().BeTrue();
        }

        [Test]
        public void Eip2200_is_not_set_correctly_indirectly_after_disabling_eip1283()
        {
            ChainSpec chainSpec = new()
            {
                Parameters = new ChainParameters
                {
                    Eip1706Transition = 5, Eip1283Transition = 1, Eip1283DisableTransition = 4
                }
            };

            ChainSpecBasedSpecProvider provider = new(chainSpec);
            provider.GetSpec(5).IsEip2200Enabled.Should().BeFalse();
        }

        [Test]
        public void Eip_transitions_loaded_correctly()
        {
            const long maxCodeTransition = 1;
            const long maxCodeSize = 1;

            ChainSpec chainSpec = new()
            {
                Ethash =
                    new EthashParameters
                    {
                        HomesteadTransition = 70,
                        Eip100bTransition = 1000
                    },
                ByzantiumBlockNumber = 1960,
                ConstantinopleBlockNumber = 6490,
                Parameters = new ChainParameters
                {
                    MaxCodeSizeTransition = maxCodeTransition,
                    MaxCodeSize = maxCodeSize,
                    Registrar = Address.Zero,
                    MinGasLimit = 11,
                    GasLimitBoundDivisor = 13,
                    MaximumExtraDataSize = 17,
                    Eip140Transition = 1400L,
                    Eip145Transition = 1450L,
                    Eip150Transition = 1500L,
                    Eip152Transition = 1520L,
                    Eip155Transition = 1550L,
                    Eip160Transition = 1600L,
                    Eip161abcTransition = 1580L,
                    Eip161dTransition = 1580L,
                    Eip211Transition = 2110L,
                    Eip214Transition = 2140L,
                    Eip658Transition = 6580L,
                    Eip1014Transition = 10140L,
                    Eip1052Transition = 10520L,
                    Eip1108Transition = 11080L,
                    Eip1283Transition = 12830L,
                    Eip1283DisableTransition = 12831L,
                    Eip1344Transition = 13440L,
                    Eip1884Transition = 18840L,
                    Eip2028Transition = 20280L,
                    Eip2200Transition = 22000L,
                    Eip2315Transition = 23150L,
                    Eip2537Transition = 25370L,
                    Eip2565Transition = 25650L,
                    Eip2929Transition = 29290L,
                    Eip2930Transition = 29300L,
                    Eip1559Transition = 15590L,
                    Eip1559FeeCollectorTransition = 15591L,
                    Eip1559FeeCollector = Address.SystemUser,
                    Eip1559BaseFeeMinValueTransition = 15592L,
                    Eip1559BaseFeeMinValue = UInt256.UInt128MaxValue,
                    Eip3198Transition = 31980L,
                    Eip3529Transition = 35290L,
                    Eip3541Transition = 35410L,
                    Eip1283ReenableTransition = 23000L,
                    ValidateChainIdTransition = 24000L,
                    ValidateReceiptsTransition = 24000L,
                }
            };

            ChainSpecBasedSpecProvider provider = new(chainSpec);
            Assert.AreEqual(long.MaxValue, provider.GetSpec(maxCodeTransition - 1).MaxCodeSize, "one before");
            Assert.AreEqual(maxCodeSize, provider.GetSpec(maxCodeTransition).MaxCodeSize, "at transition");
            Assert.AreEqual(maxCodeSize, provider.GetSpec(maxCodeTransition + 1).MaxCodeSize, "one after");

            ReleaseSpec expected = new();

            void TestTransitions(long blockNumber, Action<ReleaseSpec> changes)
            {
                changes(expected);
                IReleaseSpec underTest = provider.GetSpec(blockNumber);
                expected.Should().BeEquivalentTo(underTest);
            }

            TestTransitions(0L, r =>
            {
                r.MinGasLimit = 11L;
                r.GasLimitBoundDivisor = 13L;
                r.MaximumExtraDataSize = 17L;
                r.MaxCodeSize = long.MaxValue;
                r.Eip1559TransitionBlock = 15590L;
                r.IsTimeAdjustmentPostOlympic = true;
                r.MaximumUncleCount = 2;
            });

            TestTransitions(1L, r =>
            {
                r.MaxCodeSize = maxCodeSize;
                r.IsEip170Enabled = true;
            });
            TestTransitions(70L, r => { r.IsEip2Enabled = r.IsEip7Enabled = true; });
            TestTransitions(1000L, r => { r.IsEip100Enabled = true; });
            TestTransitions(1400L, r => { r.IsEip140Enabled = true; });
            TestTransitions(1450L, r => { r.IsEip145Enabled = true; });
            TestTransitions(1500L, r => { r.IsEip150Enabled = true; });
            TestTransitions(1520L, r => { r.IsEip152Enabled = true; });
            TestTransitions(1550L, r => { r.IsEip155Enabled = true; });
            TestTransitions(1580L, r => { r.IsEip158Enabled = true; });
            TestTransitions(1600L, r => { r.IsEip160Enabled = true; });
            TestTransitions(1960L,
                r => { r.IsEip196Enabled = r.IsEip197Enabled = r.IsEip198Enabled = r.IsEip649Enabled = true; });
            TestTransitions(2110L, r => { r.IsEip211Enabled = true; });
            TestTransitions(2140L, r => { r.IsEip214Enabled = true; });
            TestTransitions(6580L, r => { r.IsEip658Enabled = r.IsEip1234Enabled = true; });
            TestTransitions(10140L, r => { r.IsEip1014Enabled = true; });
            TestTransitions(10520L, r => { r.IsEip1052Enabled = true; });
            TestTransitions(11180L, r => { r.IsEip1108Enabled = true; });
            TestTransitions(12830L, r => { r.IsEip1283Enabled = true; });
            TestTransitions(12831L, r => { r.IsEip1283Enabled = false; });
            TestTransitions(13440L, r => { r.IsEip1344Enabled = true; });
            TestTransitions(15590L, r => { r.IsEip1559Enabled = true; });
            TestTransitions(15591L, r => { r.Eip1559FeeCollector = Address.SystemUser; });
            TestTransitions(15592L, r => { r.Eip1559BaseFeeMinValue = UInt256.UInt128MaxValue; });
            TestTransitions(18840L, r => { r.IsEip1884Enabled = true; });
            TestTransitions(20280L, r => { r.IsEip2028Enabled = true; });
            TestTransitions(22000L, r => { r.IsEip2200Enabled = true; });
            TestTransitions(23000L, r => { r.IsEip1283Enabled = r.IsEip1344Enabled = true; });
            TestTransitions(24000L, r => { r.IsEip2315Enabled = r.ValidateChainId = r.ValidateReceipts = true; });
            TestTransitions(29290L, r => { r.IsEip2929Enabled = r.IsEip2537Enabled = r.IsEip2565Enabled = true; });
            TestTransitions(29300L, r => { r.IsEip2930Enabled = true; });
            TestTransitions(31980L, r => { r.IsEip3198Enabled = true; });
            TestTransitions(35290L, r => { r.IsEip3529Enabled = true; });
            TestTransitions(35410L, r => { r.IsEip3541Enabled = true; });
        }
    }
}
