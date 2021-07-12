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

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Specs.Test.ChainSpecStyle
{
    [Parallelizable(ParallelScope.All)]
    [TestFixture]
    public class ChainSpecBasedSpecProviderTests
    {
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
                99000000, // far in the future
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
            Assert.AreEqual(9_700_000, provider.GetSpec(15_000_000).DifficultyBombDelay);
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
                .Where(p => isMainnet || checkDifficultyBomb || p.Name != nameof(IReleaseSpec.DifficultyBombDelay))
                .Where(p => isMainnet || checkDifficultyBomb || p.Name != nameof(IReleaseSpec.DifficultyBoundDivisor))
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
            ChainSpec chainSpec = new() {Parameters = new ChainParameters(), ChainId = 5};

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
                Parameters = new ChainParameters {GasLimitBoundDivisor = 17},
                Ethash = new EthashParameters {DifficultyBoundDivisor = 19}
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
                        {3, 100},
                        {7, 200},
                        {13, 300},
                        {17, 400},
                        {19, 500},
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
            ChainSpec chainSpec = new() {Parameters = new ChainParameters {Eip2200Transition = 5}};

            ChainSpecBasedSpecProvider provider = new(chainSpec);
            provider.GetSpec(5).IsEip2200Enabled.Should().BeTrue();
        }

        [Test]
        public void Eip2200_is_set_correctly_indirectly()
        {
            ChainSpec chainSpec =
                new() {Parameters = new ChainParameters {Eip1706Transition = 5, Eip1283Transition = 5}};

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
                Ethash = new EthashParameters {HomesteadTransition = 70, Eip100bTransition = 1000},
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
                    Eip3198Transition = 31980L,
                    Eip3529Transition = 35290L,
                    Eip3541Transition = 35410L,
                    Eip1283ReenableTransition = 23000L,
                    ValidateChainIdTransition = 24000L,
                    ValidateReceiptsTransition = 24000L
                }
            };

            ChainSpecBasedSpecProvider provider = new(chainSpec);
            Assert.AreEqual(long.MaxValue, provider.GetSpec(maxCodeTransition - 1).MaxCodeSize, "one before");
            Assert.AreEqual(maxCodeSize, provider.GetSpec(maxCodeTransition).MaxCodeSize, "at transition");
            Assert.AreEqual(maxCodeSize, provider.GetSpec(maxCodeTransition + 1).MaxCodeSize, "one after");

            IReleaseSpec underTest = provider.GetSpec(0L);
            Assert.AreEqual(11L, underTest.MinGasLimit);
            Assert.AreEqual(13L, underTest.GasLimitBoundDivisor);
            Assert.AreEqual(17L, underTest.MaximumExtraDataSize);

            Assert.AreEqual(long.MaxValue, underTest.MaxCodeSize);
            Assert.AreEqual(false, underTest.IsEip2Enabled);
            Assert.AreEqual(false, underTest.IsEip7Enabled);
            Assert.AreEqual(false, underTest.IsEip100Enabled);
            Assert.AreEqual(false, underTest.IsEip140Enabled);
            Assert.AreEqual(false, underTest.IsEip145Enabled);
            Assert.AreEqual(false, underTest.IsEip150Enabled);
            Assert.AreEqual(false, underTest.IsEip152Enabled);
            Assert.AreEqual(false, underTest.IsEip155Enabled);
            Assert.AreEqual(false, underTest.IsEip158Enabled);
            Assert.AreEqual(false, underTest.IsEip160Enabled);
            Assert.AreEqual(false, underTest.IsEip170Enabled);
            Assert.AreEqual(false, underTest.IsEip196Enabled);
            Assert.AreEqual(false, underTest.IsEip197Enabled);
            Assert.AreEqual(false, underTest.IsEip198Enabled);
            Assert.AreEqual(false, underTest.IsEip211Enabled);
            Assert.AreEqual(false, underTest.IsEip214Enabled);
            Assert.AreEqual(false, underTest.IsEip649Enabled);
            Assert.AreEqual(false, underTest.IsEip658Enabled);
            Assert.AreEqual(false, underTest.IsEip1014Enabled);
            Assert.AreEqual(false, underTest.IsEip1052Enabled);
            Assert.AreEqual(false, underTest.IsEip1108Enabled);
            Assert.AreEqual(false, underTest.IsEip1234Enabled);
            Assert.AreEqual(false, underTest.IsEip1283Enabled);
            Assert.AreEqual(false, underTest.IsEip1344Enabled);
            Assert.AreEqual(false, underTest.IsEip1559Enabled);
            Assert.AreEqual(false, underTest.IsEip1884Enabled);
            Assert.AreEqual(false, underTest.IsEip2028Enabled);
            Assert.AreEqual(false, underTest.IsEip2200Enabled);
            Assert.AreEqual(false, underTest.IsEip2315Enabled);
            Assert.AreEqual(false, underTest.IsEip2537Enabled);
            Assert.AreEqual(false, underTest.IsEip2565Enabled);
            Assert.AreEqual(false, underTest.IsEip2929Enabled);
            Assert.AreEqual(false, underTest.IsEip2930Enabled);
            Assert.AreEqual(false, underTest.IsEip3198Enabled);
            Assert.AreEqual(false, underTest.IsEip3541Enabled);
            Assert.AreEqual(false, underTest.IsEip3529Enabled);
            Assert.AreEqual(false, underTest.ValidateChainId);
            Assert.AreEqual(false, underTest.ValidateReceipts);

            underTest = provider.GetSpec(1L);
            Assert.AreEqual(maxCodeSize, underTest.MaxCodeSize);
            Assert.AreEqual(false, underTest.IsEip2Enabled);
            Assert.AreEqual(false, underTest.IsEip7Enabled);
            Assert.AreEqual(false, underTest.IsEip100Enabled);
            Assert.AreEqual(false, underTest.IsEip140Enabled);
            Assert.AreEqual(false, underTest.IsEip145Enabled);
            Assert.AreEqual(false, underTest.IsEip150Enabled);
            Assert.AreEqual(false, underTest.IsEip152Enabled);
            Assert.AreEqual(false, underTest.IsEip155Enabled);
            Assert.AreEqual(false, underTest.IsEip158Enabled);
            Assert.AreEqual(false, underTest.IsEip160Enabled);
            Assert.AreEqual(true, underTest.IsEip170Enabled);
            Assert.AreEqual(false, underTest.IsEip196Enabled);
            Assert.AreEqual(false, underTest.IsEip197Enabled);
            Assert.AreEqual(false, underTest.IsEip198Enabled);
            Assert.AreEqual(false, underTest.IsEip211Enabled);
            Assert.AreEqual(false, underTest.IsEip214Enabled);
            Assert.AreEqual(false, underTest.IsEip649Enabled);
            Assert.AreEqual(false, underTest.IsEip658Enabled);
            Assert.AreEqual(false, underTest.IsEip1014Enabled);
            Assert.AreEqual(false, underTest.IsEip1052Enabled);
            Assert.AreEqual(false, underTest.IsEip1108Enabled);
            Assert.AreEqual(false, underTest.IsEip1234Enabled);
            Assert.AreEqual(false, underTest.IsEip1283Enabled);
            Assert.AreEqual(false, underTest.IsEip1559Enabled);
            Assert.AreEqual(false, underTest.IsEip1884Enabled);
            Assert.AreEqual(false, underTest.IsEip2028Enabled);
            Assert.AreEqual(false, underTest.IsEip2200Enabled);
            Assert.AreEqual(false, underTest.IsEip2315Enabled);
            Assert.AreEqual(false, underTest.IsEip2537Enabled);
            Assert.AreEqual(false, underTest.IsEip2565Enabled);
            Assert.AreEqual(false, underTest.IsEip2929Enabled);
            Assert.AreEqual(false, underTest.IsEip2930Enabled);
            Assert.AreEqual(false, underTest.IsEip3198Enabled);
            Assert.AreEqual(false, underTest.IsEip3541Enabled);
            Assert.AreEqual(false, underTest.IsEip3529Enabled);
            Assert.AreEqual(false, underTest.ValidateChainId);
            Assert.AreEqual(false, underTest.ValidateReceipts);

            underTest = provider.GetSpec(70L);
            Assert.AreEqual(underTest.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, underTest.IsEip2Enabled);
            Assert.AreEqual(true, underTest.IsEip7Enabled);
            Assert.AreEqual(false, underTest.IsEip100Enabled);
            Assert.AreEqual(false, underTest.IsEip140Enabled);
            Assert.AreEqual(false, underTest.IsEip145Enabled);
            Assert.AreEqual(false, underTest.IsEip150Enabled);
            Assert.AreEqual(false, underTest.IsEip152Enabled);
            Assert.AreEqual(false, underTest.IsEip155Enabled);
            Assert.AreEqual(false, underTest.IsEip158Enabled);
            Assert.AreEqual(false, underTest.IsEip160Enabled);
            Assert.AreEqual(true, underTest.IsEip170Enabled);
            Assert.AreEqual(false, underTest.IsEip196Enabled);
            Assert.AreEqual(false, underTest.IsEip197Enabled);
            Assert.AreEqual(false, underTest.IsEip198Enabled);
            Assert.AreEqual(false, underTest.IsEip211Enabled);
            Assert.AreEqual(false, underTest.IsEip214Enabled);
            Assert.AreEqual(false, underTest.IsEip649Enabled);
            Assert.AreEqual(false, underTest.IsEip658Enabled);
            Assert.AreEqual(false, underTest.IsEip1014Enabled);
            Assert.AreEqual(false, underTest.IsEip1052Enabled);
            Assert.AreEqual(false, underTest.IsEip1108Enabled);
            Assert.AreEqual(false, underTest.IsEip1234Enabled);
            Assert.AreEqual(false, underTest.IsEip1283Enabled);
            Assert.AreEqual(false, underTest.IsEip1344Enabled);
            Assert.AreEqual(false, underTest.IsEip1559Enabled);
            Assert.AreEqual(false, underTest.IsEip1884Enabled);
            Assert.AreEqual(false, underTest.IsEip2028Enabled);
            Assert.AreEqual(false, underTest.IsEip2200Enabled);
            Assert.AreEqual(false, underTest.IsEip2315Enabled);
            Assert.AreEqual(false, underTest.IsEip2537Enabled);
            Assert.AreEqual(false, underTest.IsEip2565Enabled);
            Assert.AreEqual(false, underTest.IsEip2929Enabled);
            Assert.AreEqual(false, underTest.IsEip2930Enabled);
            Assert.AreEqual(false, underTest.IsEip3198Enabled);
            Assert.AreEqual(false, underTest.IsEip3541Enabled);
            Assert.AreEqual(false, underTest.IsEip3529Enabled);
            Assert.AreEqual(false, underTest.ValidateChainId);
            Assert.AreEqual(false, underTest.ValidateReceipts);

            underTest = provider.GetSpec(1000L);
            Assert.AreEqual(underTest.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, underTest.IsEip2Enabled);
            Assert.AreEqual(true, underTest.IsEip7Enabled);
            Assert.AreEqual(true, underTest.IsEip100Enabled);
            Assert.AreEqual(false, underTest.IsEip140Enabled);
            Assert.AreEqual(false, underTest.IsEip145Enabled);
            Assert.AreEqual(false, underTest.IsEip150Enabled);
            Assert.AreEqual(false, underTest.IsEip152Enabled);
            Assert.AreEqual(false, underTest.IsEip155Enabled);
            Assert.AreEqual(false, underTest.IsEip158Enabled);
            Assert.AreEqual(false, underTest.IsEip160Enabled);
            Assert.AreEqual(true, underTest.IsEip170Enabled);
            Assert.AreEqual(false, underTest.IsEip196Enabled);
            Assert.AreEqual(false, underTest.IsEip197Enabled);
            Assert.AreEqual(false, underTest.IsEip198Enabled);
            Assert.AreEqual(false, underTest.IsEip211Enabled);
            Assert.AreEqual(false, underTest.IsEip214Enabled);
            Assert.AreEqual(false, underTest.IsEip649Enabled);
            Assert.AreEqual(false, underTest.IsEip658Enabled);
            Assert.AreEqual(false, underTest.IsEip1014Enabled);
            Assert.AreEqual(false, underTest.IsEip1052Enabled);
            Assert.AreEqual(false, underTest.IsEip1108Enabled);
            Assert.AreEqual(false, underTest.IsEip1234Enabled);
            Assert.AreEqual(false, underTest.IsEip1283Enabled);
            Assert.AreEqual(false, underTest.IsEip1344Enabled);
            Assert.AreEqual(false, underTest.IsEip1559Enabled);
            Assert.AreEqual(false, underTest.IsEip1884Enabled);
            Assert.AreEqual(false, underTest.IsEip2028Enabled);
            Assert.AreEqual(false, underTest.IsEip2200Enabled);
            Assert.AreEqual(false, underTest.IsEip2315Enabled);
            Assert.AreEqual(false, underTest.IsEip2537Enabled);
            Assert.AreEqual(false, underTest.IsEip2565Enabled);
            Assert.AreEqual(false, underTest.IsEip2929Enabled);
            Assert.AreEqual(false, underTest.IsEip2930Enabled);
            Assert.AreEqual(false, underTest.IsEip3198Enabled);
            Assert.AreEqual(false, underTest.IsEip3541Enabled);
            Assert.AreEqual(false, underTest.IsEip3529Enabled);
            Assert.AreEqual(false, underTest.ValidateChainId);
            Assert.AreEqual(false, underTest.ValidateReceipts);

            underTest = provider.GetSpec(1400L);
            Assert.AreEqual(underTest.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, underTest.IsEip2Enabled);
            Assert.AreEqual(true, underTest.IsEip7Enabled);
            Assert.AreEqual(true, underTest.IsEip100Enabled);
            Assert.AreEqual(true, underTest.IsEip140Enabled);
            Assert.AreEqual(false, underTest.IsEip145Enabled);
            Assert.AreEqual(false, underTest.IsEip150Enabled);
            Assert.AreEqual(false, underTest.IsEip152Enabled);
            Assert.AreEqual(false, underTest.IsEip155Enabled);
            Assert.AreEqual(false, underTest.IsEip158Enabled);
            Assert.AreEqual(false, underTest.IsEip160Enabled);
            Assert.AreEqual(true, underTest.IsEip170Enabled);
            Assert.AreEqual(false, underTest.IsEip196Enabled);
            Assert.AreEqual(false, underTest.IsEip197Enabled);
            Assert.AreEqual(false, underTest.IsEip198Enabled);
            Assert.AreEqual(false, underTest.IsEip211Enabled);
            Assert.AreEqual(false, underTest.IsEip214Enabled);
            Assert.AreEqual(false, underTest.IsEip649Enabled);
            Assert.AreEqual(false, underTest.IsEip658Enabled);
            Assert.AreEqual(false, underTest.IsEip1014Enabled);
            Assert.AreEqual(false, underTest.IsEip1052Enabled);
            Assert.AreEqual(false, underTest.IsEip1108Enabled);
            Assert.AreEqual(false, underTest.IsEip1234Enabled);
            Assert.AreEqual(false, underTest.IsEip1283Enabled);
            Assert.AreEqual(false, underTest.IsEip1344Enabled);
            Assert.AreEqual(false, underTest.IsEip1559Enabled);
            Assert.AreEqual(false, underTest.IsEip1884Enabled);
            Assert.AreEqual(false, underTest.IsEip2028Enabled);
            Assert.AreEqual(false, underTest.IsEip2200Enabled);
            Assert.AreEqual(false, underTest.IsEip2315Enabled);
            Assert.AreEqual(false, underTest.IsEip2537Enabled);
            Assert.AreEqual(false, underTest.IsEip2565Enabled);
            Assert.AreEqual(false, underTest.IsEip2929Enabled);
            Assert.AreEqual(false, underTest.IsEip2930Enabled);
            Assert.AreEqual(false, underTest.IsEip3198Enabled);
            Assert.AreEqual(false, underTest.IsEip3541Enabled);
            Assert.AreEqual(false, underTest.IsEip3529Enabled);
            Assert.AreEqual(false, underTest.ValidateChainId);
            Assert.AreEqual(false, underTest.ValidateReceipts);

            underTest = provider.GetSpec(1450L);
            Assert.AreEqual(underTest.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, underTest.IsEip2Enabled);
            Assert.AreEqual(true, underTest.IsEip7Enabled);
            Assert.AreEqual(true, underTest.IsEip100Enabled);
            Assert.AreEqual(true, underTest.IsEip140Enabled);
            Assert.AreEqual(true, underTest.IsEip145Enabled);
            Assert.AreEqual(false, underTest.IsEip150Enabled);
            Assert.AreEqual(false, underTest.IsEip152Enabled);
            Assert.AreEqual(false, underTest.IsEip155Enabled);
            Assert.AreEqual(false, underTest.IsEip158Enabled);
            Assert.AreEqual(false, underTest.IsEip160Enabled);
            Assert.AreEqual(true, underTest.IsEip170Enabled);
            Assert.AreEqual(false, underTest.IsEip196Enabled);
            Assert.AreEqual(false, underTest.IsEip197Enabled);
            Assert.AreEqual(false, underTest.IsEip198Enabled);
            Assert.AreEqual(false, underTest.IsEip211Enabled);
            Assert.AreEqual(false, underTest.IsEip214Enabled);
            Assert.AreEqual(false, underTest.IsEip649Enabled);
            Assert.AreEqual(false, underTest.IsEip658Enabled);
            Assert.AreEqual(false, underTest.IsEip1014Enabled);
            Assert.AreEqual(false, underTest.IsEip1052Enabled);
            Assert.AreEqual(false, underTest.IsEip1108Enabled);
            Assert.AreEqual(false, underTest.IsEip1234Enabled);
            Assert.AreEqual(false, underTest.IsEip1283Enabled);
            Assert.AreEqual(false, underTest.IsEip1344Enabled);
            Assert.AreEqual(false, underTest.IsEip1559Enabled);
            Assert.AreEqual(false, underTest.IsEip1884Enabled);
            Assert.AreEqual(false, underTest.IsEip2028Enabled);
            Assert.AreEqual(false, underTest.IsEip2200Enabled);
            Assert.AreEqual(false, underTest.IsEip2315Enabled);
            Assert.AreEqual(false, underTest.IsEip2537Enabled);
            Assert.AreEqual(false, underTest.IsEip2565Enabled);
            Assert.AreEqual(false, underTest.IsEip2929Enabled);
            Assert.AreEqual(false, underTest.IsEip2930Enabled);
            Assert.AreEqual(false, underTest.IsEip3198Enabled);
            Assert.AreEqual(false, underTest.IsEip3541Enabled);
            Assert.AreEqual(false, underTest.IsEip3529Enabled);
            Assert.AreEqual(false, underTest.ValidateChainId);
            Assert.AreEqual(false, underTest.ValidateReceipts);

            underTest = provider.GetSpec(1500L);
            Assert.AreEqual(underTest.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, underTest.IsEip2Enabled);
            Assert.AreEqual(true, underTest.IsEip7Enabled);
            Assert.AreEqual(true, underTest.IsEip100Enabled);
            Assert.AreEqual(true, underTest.IsEip140Enabled);
            Assert.AreEqual(true, underTest.IsEip145Enabled);
            Assert.AreEqual(true, underTest.IsEip150Enabled);
            Assert.AreEqual(false, underTest.IsEip152Enabled);
            Assert.AreEqual(false, underTest.IsEip155Enabled);
            Assert.AreEqual(false, underTest.IsEip158Enabled);
            Assert.AreEqual(false, underTest.IsEip160Enabled);
            Assert.AreEqual(true, underTest.IsEip170Enabled);
            Assert.AreEqual(false, underTest.IsEip196Enabled);
            Assert.AreEqual(false, underTest.IsEip197Enabled);
            Assert.AreEqual(false, underTest.IsEip198Enabled);
            Assert.AreEqual(false, underTest.IsEip211Enabled);
            Assert.AreEqual(false, underTest.IsEip214Enabled);
            Assert.AreEqual(false, underTest.IsEip649Enabled);
            Assert.AreEqual(false, underTest.IsEip658Enabled);
            Assert.AreEqual(false, underTest.IsEip1014Enabled);
            Assert.AreEqual(false, underTest.IsEip1052Enabled);
            Assert.AreEqual(false, underTest.IsEip1108Enabled);
            Assert.AreEqual(false, underTest.IsEip1234Enabled);
            Assert.AreEqual(false, underTest.IsEip1283Enabled);
            Assert.AreEqual(false, underTest.IsEip1344Enabled);
            Assert.AreEqual(false, underTest.IsEip1559Enabled);
            Assert.AreEqual(false, underTest.IsEip1884Enabled);
            Assert.AreEqual(false, underTest.IsEip2028Enabled);
            Assert.AreEqual(false, underTest.IsEip2200Enabled);
            Assert.AreEqual(false, underTest.IsEip2315Enabled);
            Assert.AreEqual(false, underTest.IsEip2537Enabled);
            Assert.AreEqual(false, underTest.IsEip2565Enabled);
            Assert.AreEqual(false, underTest.IsEip2929Enabled);
            Assert.AreEqual(false, underTest.IsEip2930Enabled);
            Assert.AreEqual(false, underTest.IsEip3198Enabled);
            Assert.AreEqual(false, underTest.IsEip3541Enabled);
            Assert.AreEqual(false, underTest.IsEip3529Enabled);
            Assert.AreEqual(false, underTest.ValidateChainId);
            Assert.AreEqual(false, underTest.ValidateReceipts);

            underTest = provider.GetSpec(1520L);
            Assert.AreEqual(underTest.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, underTest.IsEip2Enabled);
            Assert.AreEqual(true, underTest.IsEip7Enabled);
            Assert.AreEqual(true, underTest.IsEip100Enabled);
            Assert.AreEqual(true, underTest.IsEip140Enabled);
            Assert.AreEqual(true, underTest.IsEip145Enabled);
            Assert.AreEqual(true, underTest.IsEip150Enabled);
            Assert.AreEqual(true, underTest.IsEip152Enabled);
            Assert.AreEqual(false, underTest.IsEip155Enabled);
            Assert.AreEqual(false, underTest.IsEip158Enabled);
            Assert.AreEqual(false, underTest.IsEip160Enabled);
            Assert.AreEqual(true, underTest.IsEip170Enabled);
            Assert.AreEqual(false, underTest.IsEip196Enabled);
            Assert.AreEqual(false, underTest.IsEip197Enabled);
            Assert.AreEqual(false, underTest.IsEip198Enabled);
            Assert.AreEqual(false, underTest.IsEip211Enabled);
            Assert.AreEqual(false, underTest.IsEip214Enabled);
            Assert.AreEqual(false, underTest.IsEip649Enabled);
            Assert.AreEqual(false, underTest.IsEip658Enabled);
            Assert.AreEqual(false, underTest.IsEip1014Enabled);
            Assert.AreEqual(false, underTest.IsEip1052Enabled);
            Assert.AreEqual(false, underTest.IsEip1108Enabled);
            Assert.AreEqual(false, underTest.IsEip1234Enabled);
            Assert.AreEqual(false, underTest.IsEip1283Enabled);
            Assert.AreEqual(false, underTest.IsEip1344Enabled);
            Assert.AreEqual(false, underTest.IsEip1559Enabled);
            Assert.AreEqual(false, underTest.IsEip1884Enabled);
            Assert.AreEqual(false, underTest.IsEip2028Enabled);
            Assert.AreEqual(false, underTest.IsEip2200Enabled);
            Assert.AreEqual(false, underTest.IsEip2315Enabled);
            Assert.AreEqual(false, underTest.IsEip2537Enabled);
            Assert.AreEqual(false, underTest.IsEip2565Enabled);
            Assert.AreEqual(false, underTest.IsEip2929Enabled);
            Assert.AreEqual(false, underTest.IsEip2930Enabled);
            Assert.AreEqual(false, underTest.IsEip3198Enabled);
            Assert.AreEqual(false, underTest.IsEip3541Enabled);
            Assert.AreEqual(false, underTest.IsEip3529Enabled);
            Assert.AreEqual(false, underTest.ValidateChainId);
            Assert.AreEqual(false, underTest.ValidateReceipts);

            underTest = provider.GetSpec(1550L);
            Assert.AreEqual(underTest.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, underTest.IsEip2Enabled);
            Assert.AreEqual(true, underTest.IsEip7Enabled);
            Assert.AreEqual(true, underTest.IsEip100Enabled);
            Assert.AreEqual(true, underTest.IsEip140Enabled);
            Assert.AreEqual(true, underTest.IsEip145Enabled);
            Assert.AreEqual(true, underTest.IsEip150Enabled);
            Assert.AreEqual(true, underTest.IsEip152Enabled);
            Assert.AreEqual(true, underTest.IsEip155Enabled);
            Assert.AreEqual(false, underTest.IsEip158Enabled);
            Assert.AreEqual(false, underTest.IsEip160Enabled);
            Assert.AreEqual(true, underTest.IsEip170Enabled);
            Assert.AreEqual(false, underTest.IsEip196Enabled);
            Assert.AreEqual(false, underTest.IsEip197Enabled);
            Assert.AreEqual(false, underTest.IsEip198Enabled);
            Assert.AreEqual(false, underTest.IsEip211Enabled);
            Assert.AreEqual(false, underTest.IsEip214Enabled);
            Assert.AreEqual(false, underTest.IsEip649Enabled);
            Assert.AreEqual(false, underTest.IsEip658Enabled);
            Assert.AreEqual(false, underTest.IsEip1014Enabled);
            Assert.AreEqual(false, underTest.IsEip1052Enabled);
            Assert.AreEqual(false, underTest.IsEip1108Enabled);
            Assert.AreEqual(false, underTest.IsEip1234Enabled);
            Assert.AreEqual(false, underTest.IsEip1283Enabled);
            Assert.AreEqual(false, underTest.IsEip1344Enabled);
            Assert.AreEqual(false, underTest.IsEip1559Enabled);
            Assert.AreEqual(false, underTest.IsEip1884Enabled);
            Assert.AreEqual(false, underTest.IsEip2028Enabled);
            Assert.AreEqual(false, underTest.IsEip2200Enabled);
            Assert.AreEqual(false, underTest.IsEip2315Enabled);
            Assert.AreEqual(false, underTest.IsEip2537Enabled);
            Assert.AreEqual(false, underTest.IsEip2565Enabled);
            Assert.AreEqual(false, underTest.IsEip2929Enabled);
            Assert.AreEqual(false, underTest.IsEip2930Enabled);
            Assert.AreEqual(false, underTest.IsEip3198Enabled);
            Assert.AreEqual(false, underTest.IsEip3541Enabled);
            Assert.AreEqual(false, underTest.IsEip3529Enabled);
            Assert.AreEqual(false, underTest.ValidateChainId);
            Assert.AreEqual(false, underTest.ValidateReceipts);

            underTest = provider.GetSpec(1580L);
            Assert.AreEqual(underTest.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, underTest.IsEip2Enabled);
            Assert.AreEqual(true, underTest.IsEip7Enabled);
            Assert.AreEqual(true, underTest.IsEip100Enabled);
            Assert.AreEqual(true, underTest.IsEip140Enabled);
            Assert.AreEqual(true, underTest.IsEip145Enabled);
            Assert.AreEqual(true, underTest.IsEip150Enabled);
            Assert.AreEqual(true, underTest.IsEip152Enabled);
            Assert.AreEqual(true, underTest.IsEip155Enabled);
            Assert.AreEqual(true, underTest.IsEip158Enabled);
            Assert.AreEqual(false, underTest.IsEip160Enabled);
            Assert.AreEqual(true, underTest.IsEip170Enabled);
            Assert.AreEqual(false, underTest.IsEip196Enabled);
            Assert.AreEqual(false, underTest.IsEip197Enabled);
            Assert.AreEqual(false, underTest.IsEip198Enabled);
            Assert.AreEqual(false, underTest.IsEip211Enabled);
            Assert.AreEqual(false, underTest.IsEip214Enabled);
            Assert.AreEqual(false, underTest.IsEip649Enabled);
            Assert.AreEqual(false, underTest.IsEip658Enabled);
            Assert.AreEqual(false, underTest.IsEip1014Enabled);
            Assert.AreEqual(false, underTest.IsEip1052Enabled);
            Assert.AreEqual(false, underTest.IsEip1108Enabled);
            Assert.AreEqual(false, underTest.IsEip1234Enabled);
            Assert.AreEqual(false, underTest.IsEip1283Enabled);
            Assert.AreEqual(false, underTest.IsEip1344Enabled);
            Assert.AreEqual(false, underTest.IsEip1559Enabled);
            Assert.AreEqual(false, underTest.IsEip1884Enabled);
            Assert.AreEqual(false, underTest.IsEip2028Enabled);
            Assert.AreEqual(false, underTest.IsEip2200Enabled);
            Assert.AreEqual(false, underTest.IsEip2315Enabled);
            Assert.AreEqual(false, underTest.IsEip2537Enabled);
            Assert.AreEqual(false, underTest.IsEip2565Enabled);
            Assert.AreEqual(false, underTest.IsEip2929Enabled);
            Assert.AreEqual(false, underTest.IsEip2930Enabled);
            Assert.AreEqual(false, underTest.IsEip3198Enabled);
            Assert.AreEqual(false, underTest.IsEip3541Enabled);
            Assert.AreEqual(false, underTest.IsEip3529Enabled);
            Assert.AreEqual(false, underTest.ValidateChainId);
            Assert.AreEqual(false, underTest.ValidateReceipts);

            underTest = provider.GetSpec(1600L);
            Assert.AreEqual(underTest.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, underTest.IsEip2Enabled);
            Assert.AreEqual(true, underTest.IsEip7Enabled);
            Assert.AreEqual(true, underTest.IsEip100Enabled);
            Assert.AreEqual(true, underTest.IsEip140Enabled);
            Assert.AreEqual(true, underTest.IsEip145Enabled);
            Assert.AreEqual(true, underTest.IsEip150Enabled);
            Assert.AreEqual(true, underTest.IsEip152Enabled);
            Assert.AreEqual(true, underTest.IsEip155Enabled);
            Assert.AreEqual(true, underTest.IsEip158Enabled);
            Assert.AreEqual(true, underTest.IsEip160Enabled);
            Assert.AreEqual(true, underTest.IsEip170Enabled);
            Assert.AreEqual(false, underTest.IsEip196Enabled);
            Assert.AreEqual(false, underTest.IsEip197Enabled);
            Assert.AreEqual(false, underTest.IsEip198Enabled);
            Assert.AreEqual(false, underTest.IsEip211Enabled);
            Assert.AreEqual(false, underTest.IsEip214Enabled);
            Assert.AreEqual(false, underTest.IsEip649Enabled);
            Assert.AreEqual(false, underTest.IsEip658Enabled);
            Assert.AreEqual(false, underTest.IsEip1014Enabled);
            Assert.AreEqual(false, underTest.IsEip1052Enabled);
            Assert.AreEqual(false, underTest.IsEip1108Enabled);
            Assert.AreEqual(false, underTest.IsEip1234Enabled);
            Assert.AreEqual(false, underTest.IsEip1283Enabled);
            Assert.AreEqual(false, underTest.IsEip1344Enabled);
            Assert.AreEqual(false, underTest.IsEip1559Enabled);
            Assert.AreEqual(false, underTest.IsEip1884Enabled);
            Assert.AreEqual(false, underTest.IsEip2028Enabled);
            Assert.AreEqual(false, underTest.IsEip2200Enabled);
            Assert.AreEqual(false, underTest.IsEip2315Enabled);
            Assert.AreEqual(false, underTest.IsEip2537Enabled);
            Assert.AreEqual(false, underTest.IsEip2565Enabled);
            Assert.AreEqual(false, underTest.IsEip2929Enabled);
            Assert.AreEqual(false, underTest.IsEip2930Enabled);
            Assert.AreEqual(false, underTest.IsEip3198Enabled);
            Assert.AreEqual(false, underTest.IsEip3541Enabled);
            Assert.AreEqual(false, underTest.IsEip3529Enabled);
            Assert.AreEqual(false, underTest.ValidateChainId);
            Assert.AreEqual(false, underTest.ValidateReceipts);

            underTest = provider.GetSpec(1700L);
            Assert.AreEqual(underTest.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, underTest.IsEip2Enabled);
            Assert.AreEqual(true, underTest.IsEip7Enabled);
            Assert.AreEqual(true, underTest.IsEip100Enabled);
            Assert.AreEqual(true, underTest.IsEip140Enabled);
            Assert.AreEqual(true, underTest.IsEip145Enabled);
            Assert.AreEqual(true, underTest.IsEip150Enabled);
            Assert.AreEqual(true, underTest.IsEip152Enabled);
            Assert.AreEqual(true, underTest.IsEip155Enabled);
            Assert.AreEqual(true, underTest.IsEip158Enabled);
            Assert.AreEqual(true, underTest.IsEip160Enabled);
            Assert.AreEqual(true, underTest.IsEip170Enabled);
            Assert.AreEqual(false, underTest.IsEip196Enabled);
            Assert.AreEqual(false, underTest.IsEip197Enabled);
            Assert.AreEqual(false, underTest.IsEip198Enabled);
            Assert.AreEqual(false, underTest.IsEip211Enabled);
            Assert.AreEqual(false, underTest.IsEip214Enabled);
            Assert.AreEqual(false, underTest.IsEip649Enabled);
            Assert.AreEqual(false, underTest.IsEip658Enabled);
            Assert.AreEqual(false, underTest.IsEip1014Enabled);
            Assert.AreEqual(false, underTest.IsEip1052Enabled);
            Assert.AreEqual(false, underTest.IsEip1108Enabled);
            Assert.AreEqual(false, underTest.IsEip1234Enabled);
            Assert.AreEqual(false, underTest.IsEip1283Enabled);
            Assert.AreEqual(false, underTest.IsEip1344Enabled);
            Assert.AreEqual(false, underTest.IsEip1559Enabled);
            Assert.AreEqual(false, underTest.IsEip1884Enabled);
            Assert.AreEqual(false, underTest.IsEip2028Enabled);
            Assert.AreEqual(false, underTest.IsEip2200Enabled);
            Assert.AreEqual(false, underTest.IsEip2315Enabled);
            Assert.AreEqual(false, underTest.IsEip2537Enabled);
            Assert.AreEqual(false, underTest.IsEip2565Enabled);
            Assert.AreEqual(false, underTest.IsEip2929Enabled);
            Assert.AreEqual(false, underTest.IsEip2930Enabled);
            Assert.AreEqual(false, underTest.IsEip3198Enabled);
            Assert.AreEqual(false, underTest.IsEip3541Enabled);
            Assert.AreEqual(false, underTest.IsEip3529Enabled);
            Assert.AreEqual(false, underTest.ValidateChainId);
            Assert.AreEqual(false, underTest.ValidateReceipts);

            underTest = provider.GetSpec(1960L);
            Assert.AreEqual(underTest.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, underTest.IsEip2Enabled);
            Assert.AreEqual(true, underTest.IsEip7Enabled);
            Assert.AreEqual(true, underTest.IsEip100Enabled);
            Assert.AreEqual(true, underTest.IsEip140Enabled);
            Assert.AreEqual(true, underTest.IsEip145Enabled);
            Assert.AreEqual(true, underTest.IsEip150Enabled);
            Assert.AreEqual(true, underTest.IsEip152Enabled);
            Assert.AreEqual(true, underTest.IsEip155Enabled);
            Assert.AreEqual(true, underTest.IsEip158Enabled);
            Assert.AreEqual(true, underTest.IsEip160Enabled);
            Assert.AreEqual(true, underTest.IsEip170Enabled);
            Assert.AreEqual(true, underTest.IsEip196Enabled);
            Assert.AreEqual(true, underTest.IsEip197Enabled);
            Assert.AreEqual(true, underTest.IsEip198Enabled);
            Assert.AreEqual(false, underTest.IsEip211Enabled);
            Assert.AreEqual(false, underTest.IsEip214Enabled);
            Assert.AreEqual(true, underTest.IsEip649Enabled);
            Assert.AreEqual(false, underTest.IsEip658Enabled);
            Assert.AreEqual(false, underTest.IsEip1014Enabled);
            Assert.AreEqual(false, underTest.IsEip1052Enabled);
            Assert.AreEqual(false, underTest.IsEip1108Enabled);
            Assert.AreEqual(false, underTest.IsEip1234Enabled);
            Assert.AreEqual(false, underTest.IsEip1283Enabled);
            Assert.AreEqual(false, underTest.IsEip1344Enabled);
            Assert.AreEqual(false, underTest.IsEip1559Enabled);
            Assert.AreEqual(false, underTest.IsEip1884Enabled);
            Assert.AreEqual(false, underTest.IsEip2028Enabled);
            Assert.AreEqual(false, underTest.IsEip2200Enabled);
            Assert.AreEqual(false, underTest.IsEip2315Enabled);
            Assert.AreEqual(false, underTest.IsEip2537Enabled);
            Assert.AreEqual(false, underTest.IsEip2565Enabled);
            Assert.AreEqual(false, underTest.IsEip2929Enabled);
            Assert.AreEqual(false, underTest.IsEip2930Enabled);
            Assert.AreEqual(false, underTest.IsEip3198Enabled);
            Assert.AreEqual(false, underTest.IsEip3541Enabled);
            Assert.AreEqual(false, underTest.IsEip3529Enabled);
            Assert.AreEqual(false, underTest.ValidateChainId);
            Assert.AreEqual(false, underTest.ValidateReceipts);

            underTest = provider.GetSpec(2110L);
            Assert.AreEqual(underTest.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, underTest.IsEip2Enabled);
            Assert.AreEqual(true, underTest.IsEip7Enabled);
            Assert.AreEqual(true, underTest.IsEip100Enabled);
            Assert.AreEqual(true, underTest.IsEip140Enabled);
            Assert.AreEqual(true, underTest.IsEip145Enabled);
            Assert.AreEqual(true, underTest.IsEip150Enabled);
            Assert.AreEqual(true, underTest.IsEip152Enabled);
            Assert.AreEqual(true, underTest.IsEip155Enabled);
            Assert.AreEqual(true, underTest.IsEip158Enabled);
            Assert.AreEqual(true, underTest.IsEip160Enabled);
            Assert.AreEqual(true, underTest.IsEip170Enabled);
            Assert.AreEqual(true, underTest.IsEip196Enabled);
            Assert.AreEqual(true, underTest.IsEip197Enabled);
            Assert.AreEqual(true, underTest.IsEip198Enabled);
            Assert.AreEqual(true, underTest.IsEip211Enabled);
            Assert.AreEqual(false, underTest.IsEip214Enabled);
            Assert.AreEqual(true, underTest.IsEip649Enabled);
            Assert.AreEqual(false, underTest.IsEip658Enabled);
            Assert.AreEqual(false, underTest.IsEip1014Enabled);
            Assert.AreEqual(false, underTest.IsEip1052Enabled);
            Assert.AreEqual(false, underTest.IsEip1108Enabled);
            Assert.AreEqual(false, underTest.IsEip1234Enabled);
            Assert.AreEqual(false, underTest.IsEip1283Enabled);
            Assert.AreEqual(false, underTest.IsEip1344Enabled);
            Assert.AreEqual(false, underTest.IsEip1559Enabled);
            Assert.AreEqual(false, underTest.IsEip1884Enabled);
            Assert.AreEqual(false, underTest.IsEip2028Enabled);
            Assert.AreEqual(false, underTest.IsEip2200Enabled);
            Assert.AreEqual(false, underTest.IsEip2315Enabled);
            Assert.AreEqual(false, underTest.IsEip2537Enabled);
            Assert.AreEqual(false, underTest.IsEip2565Enabled);
            Assert.AreEqual(false, underTest.IsEip2929Enabled);
            Assert.AreEqual(false, underTest.IsEip2930Enabled);
            Assert.AreEqual(false, underTest.IsEip3198Enabled);
            Assert.AreEqual(false, underTest.IsEip3541Enabled);
            Assert.AreEqual(false, underTest.IsEip3529Enabled);
            Assert.AreEqual(false, underTest.ValidateChainId);
            Assert.AreEqual(false, underTest.ValidateReceipts);

            underTest = provider.GetSpec(2140L);
            Assert.AreEqual(underTest.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, underTest.IsEip2Enabled);
            Assert.AreEqual(true, underTest.IsEip7Enabled);
            Assert.AreEqual(true, underTest.IsEip100Enabled);
            Assert.AreEqual(true, underTest.IsEip140Enabled);
            Assert.AreEqual(true, underTest.IsEip145Enabled);
            Assert.AreEqual(true, underTest.IsEip150Enabled);
            Assert.AreEqual(true, underTest.IsEip152Enabled);
            Assert.AreEqual(true, underTest.IsEip155Enabled);
            Assert.AreEqual(true, underTest.IsEip158Enabled);
            Assert.AreEqual(true, underTest.IsEip160Enabled);
            Assert.AreEqual(true, underTest.IsEip170Enabled);
            Assert.AreEqual(true, underTest.IsEip196Enabled);
            Assert.AreEqual(true, underTest.IsEip197Enabled);
            Assert.AreEqual(true, underTest.IsEip198Enabled);
            Assert.AreEqual(true, underTest.IsEip211Enabled);
            Assert.AreEqual(true, underTest.IsEip214Enabled);
            Assert.AreEqual(true, underTest.IsEip649Enabled);
            Assert.AreEqual(false, underTest.IsEip658Enabled);
            Assert.AreEqual(false, underTest.IsEip1014Enabled);
            Assert.AreEqual(false, underTest.IsEip1052Enabled);
            Assert.AreEqual(false, underTest.IsEip1108Enabled);
            Assert.AreEqual(false, underTest.IsEip1234Enabled);
            Assert.AreEqual(false, underTest.IsEip1283Enabled);
            Assert.AreEqual(false, underTest.IsEip1344Enabled);
            Assert.AreEqual(false, underTest.IsEip1559Enabled);
            Assert.AreEqual(false, underTest.IsEip1884Enabled);
            Assert.AreEqual(false, underTest.IsEip2028Enabled);
            Assert.AreEqual(false, underTest.IsEip2200Enabled);
            Assert.AreEqual(false, underTest.IsEip2315Enabled);
            Assert.AreEqual(false, underTest.IsEip2537Enabled);
            Assert.AreEqual(false, underTest.IsEip2565Enabled);
            Assert.AreEqual(false, underTest.IsEip2929Enabled);
            Assert.AreEqual(false, underTest.IsEip2930Enabled);
            Assert.AreEqual(false, underTest.IsEip3198Enabled);
            Assert.AreEqual(false, underTest.IsEip3541Enabled);
            Assert.AreEqual(false, underTest.IsEip3529Enabled);
            Assert.AreEqual(false, underTest.ValidateChainId);
            Assert.AreEqual(false, underTest.ValidateReceipts);

            underTest = provider.GetSpec(6580L);
            Assert.AreEqual(underTest.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, underTest.IsEip2Enabled);
            Assert.AreEqual(true, underTest.IsEip7Enabled);
            Assert.AreEqual(true, underTest.IsEip100Enabled);
            Assert.AreEqual(true, underTest.IsEip140Enabled);
            Assert.AreEqual(true, underTest.IsEip145Enabled);
            Assert.AreEqual(true, underTest.IsEip150Enabled);
            Assert.AreEqual(true, underTest.IsEip152Enabled);
            Assert.AreEqual(true, underTest.IsEip155Enabled);
            Assert.AreEqual(true, underTest.IsEip158Enabled);
            Assert.AreEqual(true, underTest.IsEip160Enabled);
            Assert.AreEqual(true, underTest.IsEip170Enabled);
            Assert.AreEqual(true, underTest.IsEip196Enabled);
            Assert.AreEqual(true, underTest.IsEip197Enabled);
            Assert.AreEqual(true, underTest.IsEip198Enabled);
            Assert.AreEqual(true, underTest.IsEip211Enabled);
            Assert.AreEqual(true, underTest.IsEip214Enabled);
            Assert.AreEqual(true, underTest.IsEip649Enabled);
            Assert.AreEqual(true, underTest.IsEip658Enabled);
            Assert.AreEqual(false, underTest.IsEip1014Enabled);
            Assert.AreEqual(false, underTest.IsEip1052Enabled);
            Assert.AreEqual(false, underTest.IsEip1108Enabled);
            Assert.AreEqual(true, underTest.IsEip1234Enabled);
            Assert.AreEqual(false, underTest.IsEip1283Enabled);
            Assert.AreEqual(false, underTest.IsEip1344Enabled);
            Assert.AreEqual(false, underTest.IsEip1559Enabled);
            Assert.AreEqual(false, underTest.IsEip1884Enabled);
            Assert.AreEqual(false, underTest.IsEip2028Enabled);
            Assert.AreEqual(false, underTest.IsEip2200Enabled);
            Assert.AreEqual(false, underTest.IsEip2315Enabled);
            Assert.AreEqual(false, underTest.IsEip2537Enabled);
            Assert.AreEqual(false, underTest.IsEip2565Enabled);
            Assert.AreEqual(false, underTest.IsEip2929Enabled);
            Assert.AreEqual(false, underTest.IsEip2930Enabled);
            Assert.AreEqual(false, underTest.IsEip3198Enabled);
            Assert.AreEqual(false, underTest.IsEip3541Enabled);
            Assert.AreEqual(false, underTest.IsEip3529Enabled);
            Assert.AreEqual(false, underTest.ValidateChainId);
            Assert.AreEqual(false, underTest.ValidateReceipts);

            underTest = provider.GetSpec(10140L);
            Assert.AreEqual(underTest.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, underTest.IsEip2Enabled);
            Assert.AreEqual(true, underTest.IsEip7Enabled);
            Assert.AreEqual(true, underTest.IsEip100Enabled);
            Assert.AreEqual(true, underTest.IsEip140Enabled);
            Assert.AreEqual(true, underTest.IsEip145Enabled);
            Assert.AreEqual(true, underTest.IsEip150Enabled);
            Assert.AreEqual(true, underTest.IsEip152Enabled);
            Assert.AreEqual(true, underTest.IsEip155Enabled);
            Assert.AreEqual(true, underTest.IsEip158Enabled);
            Assert.AreEqual(true, underTest.IsEip160Enabled);
            Assert.AreEqual(true, underTest.IsEip170Enabled);
            Assert.AreEqual(true, underTest.IsEip196Enabled);
            Assert.AreEqual(true, underTest.IsEip197Enabled);
            Assert.AreEqual(true, underTest.IsEip198Enabled);
            Assert.AreEqual(true, underTest.IsEip211Enabled);
            Assert.AreEqual(true, underTest.IsEip214Enabled);
            Assert.AreEqual(true, underTest.IsEip649Enabled);
            Assert.AreEqual(true, underTest.IsEip658Enabled);
            Assert.AreEqual(true, underTest.IsEip1014Enabled);
            Assert.AreEqual(false, underTest.IsEip1052Enabled);
            Assert.AreEqual(false, underTest.IsEip1108Enabled);
            Assert.AreEqual(true, underTest.IsEip1234Enabled);
            Assert.AreEqual(false, underTest.IsEip1283Enabled);
            Assert.AreEqual(false, underTest.IsEip1344Enabled);
            Assert.AreEqual(false, underTest.IsEip1559Enabled);
            Assert.AreEqual(false, underTest.IsEip1884Enabled);
            Assert.AreEqual(false, underTest.IsEip2028Enabled);
            Assert.AreEqual(false, underTest.IsEip2200Enabled);
            Assert.AreEqual(false, underTest.IsEip2315Enabled);
            Assert.AreEqual(false, underTest.IsEip2537Enabled);
            Assert.AreEqual(false, underTest.IsEip2565Enabled);
            Assert.AreEqual(false, underTest.IsEip2929Enabled);
            Assert.AreEqual(false, underTest.IsEip2930Enabled);
            Assert.AreEqual(false, underTest.IsEip3198Enabled);
            Assert.AreEqual(false, underTest.IsEip3541Enabled);
            Assert.AreEqual(false, underTest.IsEip3529Enabled);
            Assert.AreEqual(false, underTest.ValidateChainId);
            Assert.AreEqual(false, underTest.ValidateReceipts);

            underTest = provider.GetSpec(10520L);
            Assert.AreEqual(underTest.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, underTest.IsEip2Enabled);
            Assert.AreEqual(true, underTest.IsEip7Enabled);
            Assert.AreEqual(true, underTest.IsEip100Enabled);
            Assert.AreEqual(true, underTest.IsEip140Enabled);
            Assert.AreEqual(true, underTest.IsEip145Enabled);
            Assert.AreEqual(true, underTest.IsEip150Enabled);
            Assert.AreEqual(true, underTest.IsEip152Enabled);
            Assert.AreEqual(true, underTest.IsEip155Enabled);
            Assert.AreEqual(true, underTest.IsEip158Enabled);
            Assert.AreEqual(true, underTest.IsEip160Enabled);
            Assert.AreEqual(true, underTest.IsEip170Enabled);
            Assert.AreEqual(true, underTest.IsEip196Enabled);
            Assert.AreEqual(true, underTest.IsEip197Enabled);
            Assert.AreEqual(true, underTest.IsEip198Enabled);
            Assert.AreEqual(true, underTest.IsEip211Enabled);
            Assert.AreEqual(true, underTest.IsEip214Enabled);
            Assert.AreEqual(true, underTest.IsEip649Enabled);
            Assert.AreEqual(true, underTest.IsEip658Enabled);
            Assert.AreEqual(true, underTest.IsEip1014Enabled);
            Assert.AreEqual(true, underTest.IsEip1052Enabled);
            Assert.AreEqual(false, underTest.IsEip1108Enabled);
            Assert.AreEqual(true, underTest.IsEip1234Enabled);
            Assert.AreEqual(false, underTest.IsEip1283Enabled);
            Assert.AreEqual(false, underTest.IsEip1344Enabled);
            Assert.AreEqual(false, underTest.IsEip1559Enabled);
            Assert.AreEqual(false, underTest.IsEip1884Enabled);
            Assert.AreEqual(false, underTest.IsEip2028Enabled);
            Assert.AreEqual(false, underTest.IsEip2200Enabled);
            Assert.AreEqual(false, underTest.IsEip2315Enabled);
            Assert.AreEqual(false, underTest.IsEip2537Enabled);
            Assert.AreEqual(false, underTest.IsEip2565Enabled);
            Assert.AreEqual(false, underTest.IsEip2929Enabled);
            Assert.AreEqual(false, underTest.IsEip2930Enabled);
            Assert.AreEqual(false, underTest.IsEip3198Enabled);
            Assert.AreEqual(false, underTest.IsEip3541Enabled);
            Assert.AreEqual(false, underTest.IsEip3529Enabled);
            Assert.AreEqual(false, underTest.ValidateChainId);
            Assert.AreEqual(false, underTest.ValidateReceipts);

            underTest = provider.GetSpec(11180L);
            Assert.AreEqual(underTest.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, underTest.IsEip2Enabled);
            Assert.AreEqual(true, underTest.IsEip7Enabled);
            Assert.AreEqual(true, underTest.IsEip100Enabled);
            Assert.AreEqual(true, underTest.IsEip140Enabled);
            Assert.AreEqual(true, underTest.IsEip145Enabled);
            Assert.AreEqual(true, underTest.IsEip150Enabled);
            Assert.AreEqual(true, underTest.IsEip152Enabled);
            Assert.AreEqual(true, underTest.IsEip155Enabled);
            Assert.AreEqual(true, underTest.IsEip158Enabled);
            Assert.AreEqual(true, underTest.IsEip160Enabled);
            Assert.AreEqual(true, underTest.IsEip170Enabled);
            Assert.AreEqual(true, underTest.IsEip196Enabled);
            Assert.AreEqual(true, underTest.IsEip197Enabled);
            Assert.AreEqual(true, underTest.IsEip198Enabled);
            Assert.AreEqual(true, underTest.IsEip211Enabled);
            Assert.AreEqual(true, underTest.IsEip214Enabled);
            Assert.AreEqual(true, underTest.IsEip649Enabled);
            Assert.AreEqual(true, underTest.IsEip658Enabled);
            Assert.AreEqual(true, underTest.IsEip1014Enabled);
            Assert.AreEqual(true, underTest.IsEip1052Enabled);
            Assert.AreEqual(true, underTest.IsEip1108Enabled);
            Assert.AreEqual(true, underTest.IsEip1234Enabled);
            Assert.AreEqual(false, underTest.IsEip1283Enabled);
            Assert.AreEqual(false, underTest.IsEip1344Enabled);
            Assert.AreEqual(false, underTest.IsEip1559Enabled);
            Assert.AreEqual(false, underTest.IsEip1884Enabled);
            Assert.AreEqual(false, underTest.IsEip2028Enabled);
            Assert.AreEqual(false, underTest.IsEip2200Enabled);
            Assert.AreEqual(false, underTest.IsEip2315Enabled);
            Assert.AreEqual(false, underTest.IsEip2537Enabled);
            Assert.AreEqual(false, underTest.IsEip2565Enabled);
            Assert.AreEqual(false, underTest.IsEip2929Enabled);
            Assert.AreEqual(false, underTest.IsEip2930Enabled);
            Assert.AreEqual(false, underTest.IsEip3198Enabled);
            Assert.AreEqual(false, underTest.IsEip3541Enabled);
            Assert.AreEqual(false, underTest.IsEip3529Enabled);
            Assert.AreEqual(false, underTest.ValidateChainId);
            Assert.AreEqual(false, underTest.ValidateReceipts);

            underTest = provider.GetSpec(12830L);
            Assert.AreEqual(underTest.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, underTest.IsEip2Enabled);
            Assert.AreEqual(true, underTest.IsEip7Enabled);
            Assert.AreEqual(true, underTest.IsEip100Enabled);
            Assert.AreEqual(true, underTest.IsEip140Enabled);
            Assert.AreEqual(true, underTest.IsEip145Enabled);
            Assert.AreEqual(true, underTest.IsEip150Enabled);
            Assert.AreEqual(true, underTest.IsEip152Enabled);
            Assert.AreEqual(true, underTest.IsEip155Enabled);
            Assert.AreEqual(true, underTest.IsEip158Enabled);
            Assert.AreEqual(true, underTest.IsEip160Enabled);
            Assert.AreEqual(true, underTest.IsEip170Enabled);
            Assert.AreEqual(true, underTest.IsEip196Enabled);
            Assert.AreEqual(true, underTest.IsEip197Enabled);
            Assert.AreEqual(true, underTest.IsEip198Enabled);
            Assert.AreEqual(true, underTest.IsEip211Enabled);
            Assert.AreEqual(true, underTest.IsEip214Enabled);
            Assert.AreEqual(true, underTest.IsEip649Enabled);
            Assert.AreEqual(true, underTest.IsEip658Enabled);
            Assert.AreEqual(true, underTest.IsEip1014Enabled);
            Assert.AreEqual(true, underTest.IsEip1052Enabled);
            Assert.AreEqual(true, underTest.IsEip1108Enabled);
            Assert.AreEqual(true, underTest.IsEip1234Enabled);
            Assert.AreEqual(true, underTest.IsEip1283Enabled);
            Assert.AreEqual(false, underTest.IsEip1344Enabled);
            Assert.AreEqual(false, underTest.IsEip1559Enabled);
            Assert.AreEqual(false, underTest.IsEip1884Enabled);
            Assert.AreEqual(false, underTest.IsEip2028Enabled);
            Assert.AreEqual(false, underTest.IsEip2200Enabled);
            Assert.AreEqual(false, underTest.IsEip2315Enabled);
            Assert.AreEqual(false, underTest.IsEip2537Enabled);
            Assert.AreEqual(false, underTest.IsEip2565Enabled);
            Assert.AreEqual(false, underTest.IsEip2929Enabled);
            Assert.AreEqual(false, underTest.IsEip2930Enabled);
            Assert.AreEqual(false, underTest.IsEip3198Enabled);
            Assert.AreEqual(false, underTest.IsEip3541Enabled);
            Assert.AreEqual(false, underTest.IsEip3529Enabled);
            Assert.AreEqual(false, underTest.ValidateChainId);
            Assert.AreEqual(false, underTest.ValidateReceipts);

            underTest = provider.GetSpec(12831L);
            Assert.AreEqual(underTest.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, underTest.IsEip2Enabled);
            Assert.AreEqual(true, underTest.IsEip7Enabled);
            Assert.AreEqual(true, underTest.IsEip100Enabled);
            Assert.AreEqual(true, underTest.IsEip140Enabled);
            Assert.AreEqual(true, underTest.IsEip145Enabled);
            Assert.AreEqual(true, underTest.IsEip150Enabled);
            Assert.AreEqual(true, underTest.IsEip152Enabled);
            Assert.AreEqual(true, underTest.IsEip155Enabled);
            Assert.AreEqual(true, underTest.IsEip158Enabled);
            Assert.AreEqual(true, underTest.IsEip160Enabled);
            Assert.AreEqual(true, underTest.IsEip170Enabled);
            Assert.AreEqual(true, underTest.IsEip196Enabled);
            Assert.AreEqual(true, underTest.IsEip197Enabled);
            Assert.AreEqual(true, underTest.IsEip198Enabled);
            Assert.AreEqual(true, underTest.IsEip211Enabled);
            Assert.AreEqual(true, underTest.IsEip214Enabled);
            Assert.AreEqual(true, underTest.IsEip649Enabled);
            Assert.AreEqual(true, underTest.IsEip658Enabled);
            Assert.AreEqual(true, underTest.IsEip1014Enabled);
            Assert.AreEqual(true, underTest.IsEip1052Enabled);
            Assert.AreEqual(true, underTest.IsEip1108Enabled);
            Assert.AreEqual(true, underTest.IsEip1234Enabled);
            Assert.AreEqual(false, underTest.IsEip1283Enabled);
            Assert.AreEqual(false, underTest.IsEip1344Enabled);
            Assert.AreEqual(false, underTest.IsEip1559Enabled);
            Assert.AreEqual(false, underTest.IsEip1884Enabled);
            Assert.AreEqual(false, underTest.IsEip2028Enabled);
            Assert.AreEqual(false, underTest.IsEip2200Enabled);
            Assert.AreEqual(false, underTest.IsEip2315Enabled);
            Assert.AreEqual(false, underTest.IsEip2537Enabled);
            Assert.AreEqual(false, underTest.IsEip2565Enabled);
            Assert.AreEqual(false, underTest.IsEip2929Enabled);
            Assert.AreEqual(false, underTest.IsEip2930Enabled);
            Assert.AreEqual(false, underTest.IsEip3198Enabled);
            Assert.AreEqual(false, underTest.IsEip3541Enabled);
            Assert.AreEqual(false, underTest.IsEip3529Enabled);
            Assert.AreEqual(false, underTest.ValidateChainId);
            Assert.AreEqual(false, underTest.ValidateReceipts);

            underTest = provider.GetSpec(13440L);
            Assert.AreEqual(underTest.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, underTest.IsEip2Enabled);
            Assert.AreEqual(true, underTest.IsEip7Enabled);
            Assert.AreEqual(true, underTest.IsEip100Enabled);
            Assert.AreEqual(true, underTest.IsEip140Enabled);
            Assert.AreEqual(true, underTest.IsEip145Enabled);
            Assert.AreEqual(true, underTest.IsEip150Enabled);
            Assert.AreEqual(true, underTest.IsEip152Enabled);
            Assert.AreEqual(true, underTest.IsEip155Enabled);
            Assert.AreEqual(true, underTest.IsEip158Enabled);
            Assert.AreEqual(true, underTest.IsEip160Enabled);
            Assert.AreEqual(true, underTest.IsEip170Enabled);
            Assert.AreEqual(true, underTest.IsEip196Enabled);
            Assert.AreEqual(true, underTest.IsEip197Enabled);
            Assert.AreEqual(true, underTest.IsEip198Enabled);
            Assert.AreEqual(true, underTest.IsEip211Enabled);
            Assert.AreEqual(true, underTest.IsEip214Enabled);
            Assert.AreEqual(true, underTest.IsEip649Enabled);
            Assert.AreEqual(true, underTest.IsEip658Enabled);
            Assert.AreEqual(true, underTest.IsEip1014Enabled);
            Assert.AreEqual(true, underTest.IsEip1052Enabled);
            Assert.AreEqual(true, underTest.IsEip1108Enabled);
            Assert.AreEqual(true, underTest.IsEip1234Enabled);
            Assert.AreEqual(false, underTest.IsEip1283Enabled);
            Assert.AreEqual(true, underTest.IsEip1344Enabled);
            Assert.AreEqual(false, underTest.IsEip1559Enabled);
            Assert.AreEqual(false, underTest.IsEip1884Enabled);
            Assert.AreEqual(false, underTest.IsEip2028Enabled);
            Assert.AreEqual(false, underTest.IsEip2200Enabled);
            Assert.AreEqual(false, underTest.IsEip2315Enabled);
            Assert.AreEqual(false, underTest.IsEip2537Enabled);
            Assert.AreEqual(false, underTest.IsEip2565Enabled);
            Assert.AreEqual(false, underTest.IsEip2929Enabled);
            Assert.AreEqual(false, underTest.IsEip2930Enabled);
            Assert.AreEqual(false, underTest.IsEip3198Enabled);
            Assert.AreEqual(false, underTest.IsEip3541Enabled);
            Assert.AreEqual(false, underTest.IsEip3529Enabled);
            Assert.AreEqual(false, underTest.ValidateChainId);
            Assert.AreEqual(false, underTest.ValidateReceipts);

            underTest = provider.GetSpec(15590L);
            Assert.AreEqual(underTest.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, underTest.IsEip2Enabled);
            Assert.AreEqual(true, underTest.IsEip7Enabled);
            Assert.AreEqual(true, underTest.IsEip100Enabled);
            Assert.AreEqual(true, underTest.IsEip140Enabled);
            Assert.AreEqual(true, underTest.IsEip145Enabled);
            Assert.AreEqual(true, underTest.IsEip150Enabled);
            Assert.AreEqual(true, underTest.IsEip152Enabled);
            Assert.AreEqual(true, underTest.IsEip155Enabled);
            Assert.AreEqual(true, underTest.IsEip158Enabled);
            Assert.AreEqual(true, underTest.IsEip160Enabled);
            Assert.AreEqual(true, underTest.IsEip170Enabled);
            Assert.AreEqual(true, underTest.IsEip196Enabled);
            Assert.AreEqual(true, underTest.IsEip197Enabled);
            Assert.AreEqual(true, underTest.IsEip198Enabled);
            Assert.AreEqual(true, underTest.IsEip211Enabled);
            Assert.AreEqual(true, underTest.IsEip214Enabled);
            Assert.AreEqual(true, underTest.IsEip649Enabled);
            Assert.AreEqual(true, underTest.IsEip658Enabled);
            Assert.AreEqual(true, underTest.IsEip1014Enabled);
            Assert.AreEqual(true, underTest.IsEip1052Enabled);
            Assert.AreEqual(true, underTest.IsEip1108Enabled);
            Assert.AreEqual(true, underTest.IsEip1234Enabled);
            Assert.AreEqual(false, underTest.IsEip1283Enabled);
            Assert.AreEqual(true, underTest.IsEip1344Enabled);
            Assert.AreEqual(true, underTest.IsEip1559Enabled);
            Assert.AreEqual(false, underTest.IsEip1884Enabled);
            Assert.AreEqual(false, underTest.IsEip2028Enabled);
            Assert.AreEqual(false, underTest.IsEip2200Enabled);
            Assert.AreEqual(false, underTest.IsEip2315Enabled);
            Assert.AreEqual(false, underTest.IsEip2537Enabled);
            Assert.AreEqual(false, underTest.IsEip2565Enabled);
            Assert.AreEqual(false, underTest.IsEip2929Enabled);
            Assert.AreEqual(false, underTest.IsEip2930Enabled);
            Assert.AreEqual(false, underTest.IsEip3198Enabled);
            Assert.AreEqual(false, underTest.IsEip3541Enabled);
            Assert.AreEqual(false, underTest.IsEip3529Enabled);
            Assert.AreEqual(false, underTest.ValidateChainId);
            Assert.AreEqual(false, underTest.ValidateReceipts);

            underTest = provider.GetSpec(18840L);
            Assert.AreEqual(underTest.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, underTest.IsEip2Enabled);
            Assert.AreEqual(true, underTest.IsEip7Enabled);
            Assert.AreEqual(true, underTest.IsEip100Enabled);
            Assert.AreEqual(true, underTest.IsEip140Enabled);
            Assert.AreEqual(true, underTest.IsEip145Enabled);
            Assert.AreEqual(true, underTest.IsEip150Enabled);
            Assert.AreEqual(true, underTest.IsEip152Enabled);
            Assert.AreEqual(true, underTest.IsEip155Enabled);
            Assert.AreEqual(true, underTest.IsEip158Enabled);
            Assert.AreEqual(true, underTest.IsEip160Enabled);
            Assert.AreEqual(true, underTest.IsEip170Enabled);
            Assert.AreEqual(true, underTest.IsEip196Enabled);
            Assert.AreEqual(true, underTest.IsEip197Enabled);
            Assert.AreEqual(true, underTest.IsEip198Enabled);
            Assert.AreEqual(true, underTest.IsEip211Enabled);
            Assert.AreEqual(true, underTest.IsEip214Enabled);
            Assert.AreEqual(true, underTest.IsEip649Enabled);
            Assert.AreEqual(true, underTest.IsEip658Enabled);
            Assert.AreEqual(true, underTest.IsEip1014Enabled);
            Assert.AreEqual(true, underTest.IsEip1052Enabled);
            Assert.AreEqual(true, underTest.IsEip1108Enabled);
            Assert.AreEqual(true, underTest.IsEip1234Enabled);
            Assert.AreEqual(false, underTest.IsEip1283Enabled);
            Assert.AreEqual(true, underTest.IsEip1344Enabled);
            Assert.AreEqual(true, underTest.IsEip1559Enabled);
            Assert.AreEqual(true, underTest.IsEip1884Enabled);
            Assert.AreEqual(false, underTest.IsEip2028Enabled);
            Assert.AreEqual(false, underTest.IsEip2200Enabled);
            Assert.AreEqual(false, underTest.IsEip2315Enabled);
            Assert.AreEqual(false, underTest.IsEip2537Enabled);
            Assert.AreEqual(false, underTest.IsEip2565Enabled);
            Assert.AreEqual(false, underTest.IsEip2929Enabled);
            Assert.AreEqual(false, underTest.IsEip2930Enabled);
            Assert.AreEqual(false, underTest.IsEip3198Enabled);
            Assert.AreEqual(false, underTest.IsEip3541Enabled);
            Assert.AreEqual(false, underTest.IsEip3529Enabled);
            Assert.AreEqual(false, underTest.ValidateChainId);
            Assert.AreEqual(false, underTest.ValidateReceipts);

            underTest = provider.GetSpec(20280L);
            Assert.AreEqual(underTest.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, underTest.IsEip2Enabled);
            Assert.AreEqual(true, underTest.IsEip7Enabled);
            Assert.AreEqual(true, underTest.IsEip100Enabled);
            Assert.AreEqual(true, underTest.IsEip140Enabled);
            Assert.AreEqual(true, underTest.IsEip145Enabled);
            Assert.AreEqual(true, underTest.IsEip150Enabled);
            Assert.AreEqual(true, underTest.IsEip152Enabled);
            Assert.AreEqual(true, underTest.IsEip155Enabled);
            Assert.AreEqual(true, underTest.IsEip158Enabled);
            Assert.AreEqual(true, underTest.IsEip160Enabled);
            Assert.AreEqual(true, underTest.IsEip170Enabled);
            Assert.AreEqual(true, underTest.IsEip196Enabled);
            Assert.AreEqual(true, underTest.IsEip197Enabled);
            Assert.AreEqual(true, underTest.IsEip198Enabled);
            Assert.AreEqual(true, underTest.IsEip211Enabled);
            Assert.AreEqual(true, underTest.IsEip214Enabled);
            Assert.AreEqual(true, underTest.IsEip649Enabled);
            Assert.AreEqual(true, underTest.IsEip658Enabled);
            Assert.AreEqual(true, underTest.IsEip1014Enabled);
            Assert.AreEqual(true, underTest.IsEip1052Enabled);
            Assert.AreEqual(true, underTest.IsEip1108Enabled);
            Assert.AreEqual(true, underTest.IsEip1234Enabled);
            Assert.AreEqual(false, underTest.IsEip1283Enabled);
            Assert.AreEqual(true, underTest.IsEip1344Enabled);
            Assert.AreEqual(true, underTest.IsEip1559Enabled);
            Assert.AreEqual(true, underTest.IsEip1884Enabled);
            Assert.AreEqual(true, underTest.IsEip2028Enabled);
            Assert.AreEqual(false, underTest.IsEip2200Enabled);
            Assert.AreEqual(false, underTest.IsEip2315Enabled);
            Assert.AreEqual(false, underTest.IsEip2537Enabled);
            Assert.AreEqual(false, underTest.IsEip2565Enabled);
            Assert.AreEqual(false, underTest.IsEip2929Enabled);
            Assert.AreEqual(false, underTest.IsEip2930Enabled);
            Assert.AreEqual(false, underTest.IsEip3198Enabled);
            Assert.AreEqual(false, underTest.IsEip3541Enabled);
            Assert.AreEqual(false, underTest.IsEip3529Enabled);
            Assert.AreEqual(false, underTest.ValidateChainId);
            Assert.AreEqual(false, underTest.ValidateReceipts);

            underTest = provider.GetSpec(22000L);
            Assert.AreEqual(underTest.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, underTest.IsEip2Enabled);
            Assert.AreEqual(true, underTest.IsEip7Enabled);
            Assert.AreEqual(true, underTest.IsEip100Enabled);
            Assert.AreEqual(true, underTest.IsEip140Enabled);
            Assert.AreEqual(true, underTest.IsEip145Enabled);
            Assert.AreEqual(true, underTest.IsEip150Enabled);
            Assert.AreEqual(true, underTest.IsEip152Enabled);
            Assert.AreEqual(true, underTest.IsEip155Enabled);
            Assert.AreEqual(true, underTest.IsEip158Enabled);
            Assert.AreEqual(true, underTest.IsEip160Enabled);
            Assert.AreEqual(true, underTest.IsEip170Enabled);
            Assert.AreEqual(true, underTest.IsEip196Enabled);
            Assert.AreEqual(true, underTest.IsEip197Enabled);
            Assert.AreEqual(true, underTest.IsEip198Enabled);
            Assert.AreEqual(true, underTest.IsEip211Enabled);
            Assert.AreEqual(true, underTest.IsEip214Enabled);
            Assert.AreEqual(true, underTest.IsEip649Enabled);
            Assert.AreEqual(true, underTest.IsEip658Enabled);
            Assert.AreEqual(true, underTest.IsEip1014Enabled);
            Assert.AreEqual(true, underTest.IsEip1052Enabled);
            Assert.AreEqual(true, underTest.IsEip1108Enabled);
            Assert.AreEqual(true, underTest.IsEip1234Enabled);
            Assert.AreEqual(false, underTest.IsEip1283Enabled);
            Assert.AreEqual(true, underTest.IsEip1559Enabled);
            Assert.AreEqual(true, underTest.IsEip1884Enabled);
            Assert.AreEqual(true, underTest.IsEip2028Enabled);
            Assert.AreEqual(true, underTest.IsEip2200Enabled);
            Assert.AreEqual(false, underTest.IsEip2315Enabled);
            Assert.AreEqual(false, underTest.IsEip2537Enabled);
            Assert.AreEqual(false, underTest.IsEip2565Enabled);
            Assert.AreEqual(false, underTest.IsEip2929Enabled);
            Assert.AreEqual(false, underTest.IsEip2930Enabled);
            Assert.AreEqual(false, underTest.IsEip3198Enabled);
            Assert.AreEqual(false, underTest.IsEip3541Enabled);
            Assert.AreEqual(false, underTest.IsEip3529Enabled);
            Assert.AreEqual(false, underTest.ValidateChainId);
            Assert.AreEqual(false, underTest.ValidateReceipts);

            underTest = provider.GetSpec(23000L);
            Assert.AreEqual(underTest.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, underTest.IsEip2Enabled);
            Assert.AreEqual(true, underTest.IsEip7Enabled);
            Assert.AreEqual(true, underTest.IsEip100Enabled);
            Assert.AreEqual(true, underTest.IsEip140Enabled);
            Assert.AreEqual(true, underTest.IsEip145Enabled);
            Assert.AreEqual(true, underTest.IsEip150Enabled);
            Assert.AreEqual(true, underTest.IsEip152Enabled);
            Assert.AreEqual(true, underTest.IsEip155Enabled);
            Assert.AreEqual(true, underTest.IsEip158Enabled);
            Assert.AreEqual(true, underTest.IsEip160Enabled);
            Assert.AreEqual(true, underTest.IsEip170Enabled);
            Assert.AreEqual(true, underTest.IsEip196Enabled);
            Assert.AreEqual(true, underTest.IsEip197Enabled);
            Assert.AreEqual(true, underTest.IsEip198Enabled);
            Assert.AreEqual(true, underTest.IsEip211Enabled);
            Assert.AreEqual(true, underTest.IsEip214Enabled);
            Assert.AreEqual(true, underTest.IsEip649Enabled);
            Assert.AreEqual(true, underTest.IsEip658Enabled);
            Assert.AreEqual(true, underTest.IsEip1014Enabled);
            Assert.AreEqual(true, underTest.IsEip1052Enabled);
            Assert.AreEqual(true, underTest.IsEip1108Enabled);
            Assert.AreEqual(true, underTest.IsEip1234Enabled);
            Assert.AreEqual(true, underTest.IsEip1283Enabled);
            Assert.AreEqual(true, underTest.IsEip1344Enabled);
            Assert.AreEqual(true, underTest.IsEip1559Enabled);
            Assert.AreEqual(true, underTest.IsEip1884Enabled);
            Assert.AreEqual(true, underTest.IsEip2028Enabled);
            Assert.AreEqual(true, underTest.IsEip2200Enabled);
            Assert.AreEqual(false, underTest.IsEip2315Enabled);
            Assert.AreEqual(false, underTest.IsEip2537Enabled);
            Assert.AreEqual(false, underTest.IsEip2565Enabled);
            Assert.AreEqual(false, underTest.IsEip2929Enabled);
            Assert.AreEqual(false, underTest.IsEip2930Enabled);
            Assert.AreEqual(false, underTest.IsEip3198Enabled);
            Assert.AreEqual(false, underTest.IsEip3541Enabled);
            Assert.AreEqual(false, underTest.IsEip3529Enabled);
            Assert.AreEqual(false, underTest.ValidateChainId);
            Assert.AreEqual(false, underTest.ValidateReceipts);

            underTest = provider.GetSpec(24000L);
            Assert.AreEqual(underTest.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, underTest.IsEip2Enabled);
            Assert.AreEqual(true, underTest.IsEip7Enabled);
            Assert.AreEqual(true, underTest.IsEip100Enabled);
            Assert.AreEqual(true, underTest.IsEip140Enabled);
            Assert.AreEqual(true, underTest.IsEip145Enabled);
            Assert.AreEqual(true, underTest.IsEip150Enabled);
            Assert.AreEqual(true, underTest.IsEip152Enabled);
            Assert.AreEqual(true, underTest.IsEip155Enabled);
            Assert.AreEqual(true, underTest.IsEip158Enabled);
            Assert.AreEqual(true, underTest.IsEip160Enabled);
            Assert.AreEqual(true, underTest.IsEip170Enabled);
            Assert.AreEqual(true, underTest.IsEip196Enabled);
            Assert.AreEqual(true, underTest.IsEip197Enabled);
            Assert.AreEqual(true, underTest.IsEip198Enabled);
            Assert.AreEqual(true, underTest.IsEip211Enabled);
            Assert.AreEqual(true, underTest.IsEip214Enabled);
            Assert.AreEqual(true, underTest.IsEip649Enabled);
            Assert.AreEqual(true, underTest.IsEip658Enabled);
            Assert.AreEqual(true, underTest.IsEip1014Enabled);
            Assert.AreEqual(true, underTest.IsEip1052Enabled);
            Assert.AreEqual(true, underTest.IsEip1108Enabled);
            Assert.AreEqual(true, underTest.IsEip1234Enabled);
            Assert.AreEqual(true, underTest.IsEip1283Enabled);
            Assert.AreEqual(true, underTest.IsEip1344Enabled);
            Assert.AreEqual(true, underTest.IsEip1559Enabled);
            Assert.AreEqual(true, underTest.IsEip1884Enabled);
            Assert.AreEqual(true, underTest.IsEip2028Enabled);
            Assert.AreEqual(true, underTest.IsEip2200Enabled);
            Assert.AreEqual(true, underTest.IsEip2315Enabled);
            Assert.AreEqual(false, underTest.IsEip2537Enabled);
            Assert.AreEqual(false, underTest.IsEip2565Enabled);
            Assert.AreEqual(false, underTest.IsEip2929Enabled);
            Assert.AreEqual(false, underTest.IsEip2930Enabled);
            Assert.AreEqual(false, underTest.IsEip3198Enabled);
            Assert.AreEqual(false, underTest.IsEip3541Enabled);
            Assert.AreEqual(false, underTest.IsEip3529Enabled);
            Assert.AreEqual(true, underTest.ValidateChainId);
            Assert.AreEqual(true, underTest.ValidateReceipts);

            underTest = provider.GetSpec(29290L);
            Assert.AreEqual(underTest.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, underTest.IsEip2Enabled);
            Assert.AreEqual(true, underTest.IsEip7Enabled);
            Assert.AreEqual(true, underTest.IsEip100Enabled);
            Assert.AreEqual(true, underTest.IsEip140Enabled);
            Assert.AreEqual(true, underTest.IsEip145Enabled);
            Assert.AreEqual(true, underTest.IsEip150Enabled);
            Assert.AreEqual(true, underTest.IsEip152Enabled);
            Assert.AreEqual(true, underTest.IsEip155Enabled);
            Assert.AreEqual(true, underTest.IsEip158Enabled);
            Assert.AreEqual(true, underTest.IsEip160Enabled);
            Assert.AreEqual(true, underTest.IsEip170Enabled);
            Assert.AreEqual(true, underTest.IsEip196Enabled);
            Assert.AreEqual(true, underTest.IsEip197Enabled);
            Assert.AreEqual(true, underTest.IsEip198Enabled);
            Assert.AreEqual(true, underTest.IsEip211Enabled);
            Assert.AreEqual(true, underTest.IsEip214Enabled);
            Assert.AreEqual(true, underTest.IsEip649Enabled);
            Assert.AreEqual(true, underTest.IsEip658Enabled);
            Assert.AreEqual(true, underTest.IsEip1014Enabled);
            Assert.AreEqual(true, underTest.IsEip1052Enabled);
            Assert.AreEqual(true, underTest.IsEip1108Enabled);
            Assert.AreEqual(true, underTest.IsEip1234Enabled);
            Assert.AreEqual(true, underTest.IsEip1283Enabled);
            Assert.AreEqual(true, underTest.IsEip1344Enabled);
            Assert.AreEqual(true, underTest.IsEip1884Enabled);
            Assert.AreEqual(true, underTest.IsEip2028Enabled);
            Assert.AreEqual(true, underTest.IsEip2200Enabled);
            Assert.AreEqual(true, underTest.ValidateChainId);
            Assert.AreEqual(true, underTest.ValidateReceipts);
            Assert.AreEqual(true, underTest.IsEip2929Enabled);
            Assert.AreEqual(false, underTest.IsEip2930Enabled);
            Assert.AreEqual(false, underTest.IsEip3198Enabled);
            Assert.AreEqual(false, underTest.IsEip3541Enabled);
            Assert.AreEqual(false, underTest.IsEip3529Enabled);

            underTest = provider.GetSpec(29300L);
            Assert.AreEqual(underTest.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, underTest.IsEip2Enabled);
            Assert.AreEqual(true, underTest.IsEip7Enabled);
            Assert.AreEqual(true, underTest.IsEip100Enabled);
            Assert.AreEqual(true, underTest.IsEip140Enabled);
            Assert.AreEqual(true, underTest.IsEip145Enabled);
            Assert.AreEqual(true, underTest.IsEip150Enabled);
            Assert.AreEqual(true, underTest.IsEip152Enabled);
            Assert.AreEqual(true, underTest.IsEip155Enabled);
            Assert.AreEqual(true, underTest.IsEip158Enabled);
            Assert.AreEqual(true, underTest.IsEip160Enabled);
            Assert.AreEqual(true, underTest.IsEip170Enabled);
            Assert.AreEqual(true, underTest.IsEip196Enabled);
            Assert.AreEqual(true, underTest.IsEip197Enabled);
            Assert.AreEqual(true, underTest.IsEip198Enabled);
            Assert.AreEqual(true, underTest.IsEip211Enabled);
            Assert.AreEqual(true, underTest.IsEip214Enabled);
            Assert.AreEqual(true, underTest.IsEip649Enabled);
            Assert.AreEqual(true, underTest.IsEip658Enabled);
            Assert.AreEqual(true, underTest.IsEip1014Enabled);
            Assert.AreEqual(true, underTest.IsEip1052Enabled);
            Assert.AreEqual(true, underTest.IsEip1108Enabled);
            Assert.AreEqual(true, underTest.IsEip1234Enabled);
            Assert.AreEqual(true, underTest.IsEip1283Enabled);
            Assert.AreEqual(true, underTest.IsEip1344Enabled);
            Assert.AreEqual(true, underTest.IsEip1884Enabled);
            Assert.AreEqual(true, underTest.IsEip2028Enabled);
            Assert.AreEqual(true, underTest.IsEip2200Enabled);
            Assert.AreEqual(true, underTest.ValidateChainId);
            Assert.AreEqual(true, underTest.ValidateReceipts);
            Assert.AreEqual(true, underTest.IsEip2929Enabled);
            Assert.AreEqual(true, underTest.IsEip2930Enabled);
            Assert.AreEqual(false, underTest.IsEip3198Enabled);
            Assert.AreEqual(false, underTest.IsEip3541Enabled);
            Assert.AreEqual(false, underTest.IsEip3529Enabled);
            
            underTest = provider.GetSpec(31980L);
            Assert.AreEqual(underTest.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, underTest.IsEip2Enabled);
            Assert.AreEqual(true, underTest.IsEip7Enabled);
            Assert.AreEqual(true, underTest.IsEip100Enabled);
            Assert.AreEqual(true, underTest.IsEip140Enabled);
            Assert.AreEqual(true, underTest.IsEip145Enabled);
            Assert.AreEqual(true, underTest.IsEip150Enabled);
            Assert.AreEqual(true, underTest.IsEip152Enabled);
            Assert.AreEqual(true, underTest.IsEip155Enabled);
            Assert.AreEqual(true, underTest.IsEip158Enabled);
            Assert.AreEqual(true, underTest.IsEip160Enabled);
            Assert.AreEqual(true, underTest.IsEip170Enabled);
            Assert.AreEqual(true, underTest.IsEip196Enabled);
            Assert.AreEqual(true, underTest.IsEip197Enabled);
            Assert.AreEqual(true, underTest.IsEip198Enabled);
            Assert.AreEqual(true, underTest.IsEip211Enabled);
            Assert.AreEqual(true, underTest.IsEip214Enabled);
            Assert.AreEqual(true, underTest.IsEip649Enabled);
            Assert.AreEqual(true, underTest.IsEip658Enabled);
            Assert.AreEqual(true, underTest.IsEip1014Enabled);
            Assert.AreEqual(true, underTest.IsEip1052Enabled);
            Assert.AreEqual(true, underTest.IsEip1108Enabled);
            Assert.AreEqual(true, underTest.IsEip1234Enabled);
            Assert.AreEqual(true, underTest.IsEip1283Enabled);
            Assert.AreEqual(true, underTest.IsEip1344Enabled);
            Assert.AreEqual(true, underTest.IsEip1884Enabled);
            Assert.AreEqual(true, underTest.IsEip2028Enabled);
            Assert.AreEqual(true, underTest.IsEip2200Enabled);
            Assert.AreEqual(true, underTest.ValidateChainId);
            Assert.AreEqual(true, underTest.ValidateReceipts);
            Assert.AreEqual(true, underTest.IsEip2929Enabled);
            Assert.AreEqual(true, underTest.IsEip2930Enabled);
            Assert.AreEqual(true, underTest.IsEip3198Enabled);
            Assert.AreEqual(false, underTest.IsEip3541Enabled);
            Assert.AreEqual(false, underTest.IsEip3529Enabled);
            
            underTest = provider.GetSpec(35290L);
            Assert.AreEqual(underTest.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, underTest.IsEip2Enabled);
            Assert.AreEqual(true, underTest.IsEip7Enabled);
            Assert.AreEqual(true, underTest.IsEip100Enabled);
            Assert.AreEqual(true, underTest.IsEip140Enabled);
            Assert.AreEqual(true, underTest.IsEip145Enabled);
            Assert.AreEqual(true, underTest.IsEip150Enabled);
            Assert.AreEqual(true, underTest.IsEip152Enabled);
            Assert.AreEqual(true, underTest.IsEip155Enabled);
            Assert.AreEqual(true, underTest.IsEip158Enabled);
            Assert.AreEqual(true, underTest.IsEip160Enabled);
            Assert.AreEqual(true, underTest.IsEip170Enabled);
            Assert.AreEqual(true, underTest.IsEip196Enabled);
            Assert.AreEqual(true, underTest.IsEip197Enabled);
            Assert.AreEqual(true, underTest.IsEip198Enabled);
            Assert.AreEqual(true, underTest.IsEip211Enabled);
            Assert.AreEqual(true, underTest.IsEip214Enabled);
            Assert.AreEqual(true, underTest.IsEip649Enabled);
            Assert.AreEqual(true, underTest.IsEip658Enabled);
            Assert.AreEqual(true, underTest.IsEip1014Enabled);
            Assert.AreEqual(true, underTest.IsEip1052Enabled);
            Assert.AreEqual(true, underTest.IsEip1108Enabled);
            Assert.AreEqual(true, underTest.IsEip1234Enabled);
            Assert.AreEqual(true, underTest.IsEip1283Enabled);
            Assert.AreEqual(true, underTest.IsEip1344Enabled);
            Assert.AreEqual(true, underTest.IsEip1884Enabled);
            Assert.AreEqual(true, underTest.IsEip2028Enabled);
            Assert.AreEqual(true, underTest.IsEip2200Enabled);
            Assert.AreEqual(true, underTest.ValidateChainId);
            Assert.AreEqual(true, underTest.ValidateReceipts);
            Assert.AreEqual(true, underTest.IsEip2929Enabled);
            Assert.AreEqual(true, underTest.IsEip2930Enabled);
            Assert.AreEqual(true, underTest.IsEip3198Enabled);
            Assert.AreEqual(true, underTest.IsEip3529Enabled);
            Assert.AreEqual(false, underTest.IsEip3541Enabled);
            
            underTest = provider.GetSpec(35410L);
            Assert.AreEqual(underTest.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, underTest.IsEip2Enabled);
            Assert.AreEqual(true, underTest.IsEip7Enabled);
            Assert.AreEqual(true, underTest.IsEip100Enabled);
            Assert.AreEqual(true, underTest.IsEip140Enabled);
            Assert.AreEqual(true, underTest.IsEip145Enabled);
            Assert.AreEqual(true, underTest.IsEip150Enabled);
            Assert.AreEqual(true, underTest.IsEip152Enabled);
            Assert.AreEqual(true, underTest.IsEip155Enabled);
            Assert.AreEqual(true, underTest.IsEip158Enabled);
            Assert.AreEqual(true, underTest.IsEip160Enabled);
            Assert.AreEqual(true, underTest.IsEip170Enabled);
            Assert.AreEqual(true, underTest.IsEip196Enabled);
            Assert.AreEqual(true, underTest.IsEip197Enabled);
            Assert.AreEqual(true, underTest.IsEip198Enabled);
            Assert.AreEqual(true, underTest.IsEip211Enabled);
            Assert.AreEqual(true, underTest.IsEip214Enabled);
            Assert.AreEqual(true, underTest.IsEip649Enabled);
            Assert.AreEqual(true, underTest.IsEip658Enabled);
            Assert.AreEqual(true, underTest.IsEip1014Enabled);
            Assert.AreEqual(true, underTest.IsEip1052Enabled);
            Assert.AreEqual(true, underTest.IsEip1108Enabled);
            Assert.AreEqual(true, underTest.IsEip1234Enabled);
            Assert.AreEqual(true, underTest.IsEip1283Enabled);
            Assert.AreEqual(true, underTest.IsEip1344Enabled);
            Assert.AreEqual(true, underTest.IsEip1884Enabled);
            Assert.AreEqual(true, underTest.IsEip2028Enabled);
            Assert.AreEqual(true, underTest.IsEip2200Enabled);
            Assert.AreEqual(true, underTest.ValidateChainId);
            Assert.AreEqual(true, underTest.ValidateReceipts);
            Assert.AreEqual(true, underTest.IsEip2929Enabled);
            Assert.AreEqual(true, underTest.IsEip2930Enabled);
            Assert.AreEqual(true, underTest.IsEip3198Enabled);
            Assert.AreEqual(true, underTest.IsEip3529Enabled);
            Assert.AreEqual(true, underTest.IsEip3541Enabled);
        }
    }
}
