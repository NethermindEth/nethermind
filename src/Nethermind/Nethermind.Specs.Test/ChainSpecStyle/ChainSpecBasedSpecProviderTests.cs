// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Specs.Test.ChainSpecStyle
{
    [Parallelizable(ParallelScope.All)]
    [TestFixture]
    public class ChainSpecBasedSpecProviderTests
    {
        [Test]
        public void Shandong_loads_properly()
        {
            ChainSpecLoader loader = new(new EthereumJsonSerializer());
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../Chains/shandong.json");
            ChainSpec chainSpec = loader.Load(File.ReadAllText(path));
            chainSpec.Parameters.Eip2537Transition.Should().BeNull();

            ChainSpecBasedSpecProvider provider = new(chainSpec);

            ReleaseSpec shandongSpec = (ReleaseSpec)((ReleaseSpec)MainnetSpecProvider
                .Instance.GetSpec(MainnetSpecProvider.ShanghaiActivation)).Clone();
            shandongSpec.Name = "shandong";
            shandongSpec.IsEip3651Enabled = true;
            shandongSpec.IsEip3855Enabled = true;
            shandongSpec.IsEip3860Enabled = true;
            shandongSpec.Eip1559TransitionBlock = 0;
            shandongSpec.DifficultyBombDelay = 0;
            TestSpecProvider testProvider = TestSpecProvider.Instance;
            testProvider.SpecToReturn = shandongSpec;
            testProvider.TerminalTotalDifficulty = 0;
            testProvider.GenesisSpec = shandongSpec;

            List<ForkActivation> forkActivationsToTest = new()
            {
                (ForkActivation)0,
                (0, 0),
                (0, null),
                (ForkActivation)1,
                (ForkActivation)999_999_999, // far in the future
            };

            CompareSpecProviders(testProvider, provider, forkActivationsToTest);
            Assert.AreEqual(testProvider.TerminalTotalDifficulty, provider.TerminalTotalDifficulty);
            Assert.AreEqual(testProvider.GenesisSpec.Eip1559TransitionBlock, provider.GenesisSpec.Eip1559TransitionBlock);
            Assert.AreEqual(testProvider.GenesisSpec.DifficultyBombDelay, provider.GenesisSpec.DifficultyBombDelay);
        }

        [Test]
        [NonParallelizable]
        public void Timstamp_activation_equal_to_genesis_timestamp_loads_correctly()
        {
            ChainSpecLoader loader = new(new EthereumJsonSerializer());
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../Specs/Timstamp_activation_equal_to_genesis_timestamp_test.json");
            ChainSpec chainSpec = loader.Load(File.ReadAllText(path));
            chainSpec.Parameters.Eip2537Transition.Should().BeNull();
            var logger = Substitute.ForPartsOf<LimboTraceLogger>();
            var logManager = Substitute.For<ILogManager>();
            logManager.GetClassLogger<ChainSpecBasedSpecProvider>().Returns(logger);
            ChainSpecBasedSpecProvider provider = new(chainSpec);
            ReleaseSpec expectedSpec = ((ReleaseSpec)MainnetSpecProvider
                .Instance.GetSpec((MainnetSpecProvider.GrayGlacierBlockNumber, null))).Clone();
            expectedSpec.Name = "Genesis_with_non_zero_timestamp";
            expectedSpec.IsEip3651Enabled = true;
            expectedSpec.IsEip3198Enabled = false;
            expectedSpec.Eip1559TransitionBlock = 0;
            expectedSpec.DifficultyBombDelay = 0;
            TestSpecProvider testProvider = TestSpecProvider.Instance;
            testProvider.SpecToReturn = expectedSpec;
            testProvider.TerminalTotalDifficulty = 0;
            testProvider.GenesisSpec = expectedSpec;
            List<ForkActivation> forkActivationsToTest = new()
            {
                (0, null),
                (0, 0),
                (0, 4660),
                (1, 4660),
                (1, 4661),
            };
            CompareSpecProviders(testProvider, provider, forkActivationsToTest);
            Assert.AreEqual(testProvider.GenesisSpec.Eip1559TransitionBlock, provider.GenesisSpec.Eip1559TransitionBlock);
            Assert.AreEqual(testProvider.GenesisSpec.DifficultyBombDelay, provider.GenesisSpec.DifficultyBombDelay);
            expectedSpec.IsEip3855Enabled = true;
            List<ForkActivation> forkActivationsToTest3 = new()
            {
                (4, 4672),
                (4, 4673),
                (5, 4680),
            };
            CompareSpecProviders(testProvider, provider, forkActivationsToTest3);
        }

        [Test]
        [NonParallelizable]
        public void Logs_warning_when_timestampActivation_happens_before_blockActivation()
        {
            ChainSpecLoader loader = new(new EthereumJsonSerializer());
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../Specs/Logs_warning_when_timestampActivation_happens_before_blockActivation_test.json");
            ChainSpec chainSpec = loader.Load(File.ReadAllText(path));
            chainSpec.Parameters.Eip2537Transition.Should().BeNull();
            var logger = Substitute.For<ILogger>();
            logger.IsWarn.Returns(true);
            var logManager = Substitute.For<ILogManager>();
            logManager.GetClassLogger<ChainSpecBasedSpecProvider>().Returns(logger);
            ChainSpecBasedSpecProvider provider = new(chainSpec, logManager);
            ReleaseSpec expectedSpec = ((ReleaseSpec)MainnetSpecProvider
                .Instance.GetSpec((MainnetSpecProvider.GrayGlacierBlockNumber, null))).Clone();
            expectedSpec.Name = "Genesis_with_non_zero_timestamp";
            expectedSpec.IsEip3651Enabled = true;
            expectedSpec.IsEip3198Enabled = false;
            expectedSpec.Eip1559TransitionBlock = 0;
            expectedSpec.DifficultyBombDelay = 0;
            TestSpecProvider testProvider = TestSpecProvider.Instance;
            testProvider.SpecToReturn = expectedSpec;
            testProvider.TerminalTotalDifficulty = 0;
            testProvider.GenesisSpec = expectedSpec;
            List<ForkActivation> forkActivationsToTest = new()
            {
                (0, null),
                (0, 0),
                (0, 4660),
                (1, 4660),
                (1, 4661),
            };
            CompareSpecProviders(testProvider, provider, forkActivationsToTest);
            Assert.AreEqual(testProvider.GenesisSpec.Eip1559TransitionBlock, provider.GenesisSpec.Eip1559TransitionBlock);
            Assert.AreEqual(testProvider.GenesisSpec.DifficultyBombDelay, provider.GenesisSpec.DifficultyBombDelay);
            expectedSpec.IsEip3855Enabled = false; // this will only activate in the block after the last block activation happens
            List<ForkActivation> forkActivationsToTest2 = new()
            {
                (1, 4672),
                (2, 4673),
                (3, 4680),
            };
            CompareSpecProviders(testProvider, provider, forkActivationsToTest2);
            logger.Received(2).Warn(Arg.Is("Chainspec file is misconfigured! Timestamp transition is configured to happen before the last block transition."));
            expectedSpec.IsEip3198Enabled = true;
            List<ForkActivation> forkActivationsToTest3 = new()
            {
                (4, 4672),
            };
            CompareSpecProviders(testProvider, provider, forkActivationsToTest3);
            expectedSpec.IsEip3855Enabled = true; // since the block transition happened the block before, now the timestamp transition activates, even though it should have activated long ago.
            List<ForkActivation> forkActivationsToTest4 = new()
            {
                (5, 4672),
                (5, 4673),
                (6, 4680),
            };
            CompareSpecProviders(testProvider, provider, forkActivationsToTest4);
        }

        [Test]
        public void Sepolia_loads_properly()
        {
            ChainSpecLoader loader = new(new EthereumJsonSerializer());
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../Chains/sepolia.json");
            ChainSpec chainSpec = loader.Load(File.ReadAllText(path));
            chainSpec.Parameters.Eip2537Transition.Should().BeNull();

            ChainSpecBasedSpecProvider provider = new(chainSpec);
            SepoliaSpecProvider sepolia = SepoliaSpecProvider.Instance;

            List<ForkActivation> forkActivationsToTest = new()
            {
                (ForkActivation)120_000_000, // far in the future
            };

            CompareSpecProviders(sepolia, provider, forkActivationsToTest);
            Assert.AreEqual(SepoliaSpecProvider.Instance.TerminalTotalDifficulty, provider.TerminalTotalDifficulty);
            Assert.AreEqual(0, provider.GenesisSpec.Eip1559TransitionBlock);
            Assert.AreEqual(long.MaxValue, provider.GenesisSpec.DifficultyBombDelay);
            Assert.AreEqual(BlockchainIds.Sepolia, provider.ChainId);
            Assert.AreEqual(BlockchainIds.Sepolia, provider.NetworkId);
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

            List<ForkActivation> forkActivationsToTest = new()
            {
                (ForkActivation)RinkebySpecProvider.ByzantiumBlockNumber,
                (ForkActivation)(RinkebySpecProvider.ConstantinopleFixBlockNumber - 1),
                (ForkActivation)RinkebySpecProvider.ConstantinopleFixBlockNumber,
                (ForkActivation)(RinkebySpecProvider.IstanbulBlockNumber - 1),
                (ForkActivation)RinkebySpecProvider.IstanbulBlockNumber,
                (ForkActivation)(RinkebySpecProvider.BerlinBlockNumber - 1),
                (ForkActivation)RinkebySpecProvider.BerlinBlockNumber,
                (ForkActivation)(RinkebySpecProvider.LondonBlockNumber - 1),
                (ForkActivation)RinkebySpecProvider.LondonBlockNumber,
                (ForkActivation)120_000_000, // far in the future
            };

            CompareSpecProviders(rinkeby, provider, forkActivationsToTest);
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

            List<ForkActivation> forkActivationsToTest = new()
            {
                (ForkActivation)0,
                (ForkActivation)1,
                (ForkActivation)(GoerliSpecProvider.IstanbulBlockNumber - 1),
                (ForkActivation)GoerliSpecProvider.IstanbulBlockNumber,
                (ForkActivation)(GoerliSpecProvider.BerlinBlockNumber - 1),
                (ForkActivation)GoerliSpecProvider.BerlinBlockNumber,
                (ForkActivation)(GoerliSpecProvider.LondonBlockNumber - 1),
                (ForkActivation)GoerliSpecProvider.LondonBlockNumber,
                (ForkActivation)100000000, // far in the future
            };

            CompareSpecProviders(goerli, provider, forkActivationsToTest);
            Assert.AreEqual(GoerliSpecProvider.LondonBlockNumber, provider.GenesisSpec.Eip1559TransitionBlock);
            Assert.AreEqual(GoerliSpecProvider.Instance.TerminalTotalDifficulty, provider.TerminalTotalDifficulty);
            Assert.AreEqual(BlockchainIds.Goerli, provider.ChainId);
            Assert.AreEqual(BlockchainIds.Goerli, provider.NetworkId);
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

            List<ForkActivation> forkActivationsToTest = new()
            {
                (ForkActivation)0,
                (0, 0),
                (0, null),
                (ForkActivation)1,
                (ForkActivation)(MainnetSpecProvider.HomesteadBlockNumber - 1),
                (ForkActivation)MainnetSpecProvider.HomesteadBlockNumber,
                (ForkActivation)(MainnetSpecProvider.TangerineWhistleBlockNumber - 1),
                (ForkActivation)MainnetSpecProvider.TangerineWhistleBlockNumber,
                (ForkActivation)(MainnetSpecProvider.SpuriousDragonBlockNumber - 1),
                (ForkActivation)MainnetSpecProvider.SpuriousDragonBlockNumber,
                (ForkActivation)(MainnetSpecProvider.ByzantiumBlockNumber - 1),
                (ForkActivation)MainnetSpecProvider.ByzantiumBlockNumber,
                (ForkActivation)(MainnetSpecProvider.ConstantinopleFixBlockNumber - 1),
                (ForkActivation)MainnetSpecProvider.ConstantinopleFixBlockNumber,
                (ForkActivation)(MainnetSpecProvider.IstanbulBlockNumber - 1),
                (ForkActivation)MainnetSpecProvider.IstanbulBlockNumber,
                (ForkActivation)(MainnetSpecProvider.MuirGlacierBlockNumber - 1),
                (ForkActivation)MainnetSpecProvider.MuirGlacierBlockNumber,
                (ForkActivation)(MainnetSpecProvider.BerlinBlockNumber - 1),
                (ForkActivation)MainnetSpecProvider.BerlinBlockNumber,
                (ForkActivation)(MainnetSpecProvider.LondonBlockNumber - 1),
                (ForkActivation)MainnetSpecProvider.LondonBlockNumber,
                (ForkActivation)(MainnetSpecProvider.ArrowGlacierBlockNumber - 1),
                (ForkActivation)MainnetSpecProvider.ArrowGlacierBlockNumber,
                (ForkActivation)(MainnetSpecProvider.GrayGlacierBlockNumber - 1),
                (ForkActivation)MainnetSpecProvider.GrayGlacierBlockNumber,
                (ForkActivation)99_000_000, // far in the future
            };

            CompareSpecProviders(mainnet, provider, forkActivationsToTest);

            Assert.AreEqual(MainnetSpecProvider.LondonBlockNumber, provider.GenesisSpec.Eip1559TransitionBlock);
            Assert.AreEqual(0_000_000, provider.GetSpec((ForkActivation)4_369_999).DifficultyBombDelay);
            Assert.AreEqual(3_000_000, provider.GetSpec((ForkActivation)4_370_000).DifficultyBombDelay);
            Assert.AreEqual(3_000_000, provider.GetSpec((ForkActivation)7_279_999).DifficultyBombDelay);
            Assert.AreEqual(3_000_000, provider.GetSpec((ForkActivation)7_279_999).DifficultyBombDelay);
            Assert.AreEqual(5_000_000, provider.GetSpec((ForkActivation)7_280_000).DifficultyBombDelay);
            Assert.AreEqual(5_000_000, provider.GetSpec((ForkActivation)9_199_999).DifficultyBombDelay);
            Assert.AreEqual(9_000_000, provider.GetSpec((ForkActivation)9_200_000).DifficultyBombDelay);
            Assert.AreEqual(9_000_000, provider.GetSpec((ForkActivation)12_000_000).DifficultyBombDelay);
            Assert.AreEqual(9_000_000, provider.GetSpec((ForkActivation)12_964_999).DifficultyBombDelay);
            Assert.AreEqual(9_700_000, provider.GetSpec((ForkActivation)12_965_000).DifficultyBombDelay);
            Assert.AreEqual(9_700_000, provider.GetSpec((ForkActivation)13_772_999).DifficultyBombDelay);
            Assert.AreEqual(10_700_000, provider.GetSpec((ForkActivation)13_773_000).DifficultyBombDelay);
            Assert.AreEqual(10_700_000, provider.GetSpec((ForkActivation)15_049_999).DifficultyBombDelay);
            Assert.AreEqual(11_400_000, provider.GetSpec((ForkActivation)15_050_000).DifficultyBombDelay);
            Assert.AreEqual(11_400_000, provider.GetSpec((ForkActivation)99_414_000).DifficultyBombDelay);
            Assert.AreEqual(MainnetSpecProvider.Instance.TerminalTotalDifficulty, provider.TerminalTotalDifficulty);
            Assert.AreEqual(BlockchainIds.Mainnet, provider.ChainId);
            Assert.AreEqual(BlockchainIds.Mainnet, provider.NetworkId);
        }

        private static void CompareSpecProviders(
            ISpecProvider oldSpecProvider,
            ISpecProvider newSpecProvider,
            IEnumerable<ForkActivation> forkActivations,
            bool checkDifficultyBomb = false)
        {
            foreach (ForkActivation activation in forkActivations)
            {
                IReleaseSpec oldSpec = oldSpecProvider.GetSpec(activation);
                IReleaseSpec newSpec = newSpecProvider.GetSpec(activation);
                long? daoBlockNumber = newSpecProvider.DaoBlockNumber;
                bool isMainnet = daoBlockNumber is not null;

                CompareSpecs(oldSpec, newSpec, activation, isMainnet, checkDifficultyBomb);
            }
        }

        private static void CompareSpecs(IReleaseSpec expectedSpec, IReleaseSpec ActualSpec, ForkActivation activation, bool isMainnet,
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
                         .Where(p => p.Name != nameof(IReleaseSpec.Eip1559TransitionBlock))
                         .Where(p => p.Name != nameof(IReleaseSpec.WithdrawalTimestamp))
                         .Where(p => p.Name != nameof(IReleaseSpec.Eip4844TransitionTimestamp)))
            {
                Assert.AreEqual(propertyInfo.GetValue(expectedSpec), propertyInfo.GetValue(ActualSpec),
                    activation + "." + propertyInfo.Name);
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

            List<ForkActivation> forkActivationsToTest = new()
            {
                (ForkActivation)0,
                (ForkActivation)1,
                (ForkActivation)(RopstenSpecProvider.SpuriousDragonBlockNumber - 1),
                (ForkActivation)RopstenSpecProvider.SpuriousDragonBlockNumber,
                (ForkActivation)(RopstenSpecProvider.ByzantiumBlockNumber - 1),
                (ForkActivation)RopstenSpecProvider.ByzantiumBlockNumber,
                (ForkActivation)(RopstenSpecProvider.ConstantinopleFixBlockNumber - 1),
                (ForkActivation)RopstenSpecProvider.ConstantinopleFixBlockNumber,
                (ForkActivation)(RopstenSpecProvider.IstanbulBlockNumber - 1),
                (ForkActivation)RopstenSpecProvider.IstanbulBlockNumber,
                (ForkActivation)(RopstenSpecProvider.MuirGlacierBlockNumber - 1),
                (ForkActivation)RopstenSpecProvider.MuirGlacierBlockNumber,
                (ForkActivation)(RopstenSpecProvider.BerlinBlockNumber - 1),
                (ForkActivation)RopstenSpecProvider.BerlinBlockNumber,
                (ForkActivation)(RopstenSpecProvider.LondonBlockNumber - 1),
                (ForkActivation)RopstenSpecProvider.LondonBlockNumber,
                (ForkActivation)999_999_999, // far in the future
            };

            CompareSpecProviders(ropsten, provider, forkActivationsToTest, true);
            Assert.AreEqual(RopstenSpecProvider.Instance.TerminalTotalDifficulty, provider.TerminalTotalDifficulty);
            Assert.AreEqual(RopstenSpecProvider.LondonBlockNumber, provider.GenesisSpec.Eip1559TransitionBlock);
        }

        [Test]
        public void Chain_id_is_set_correctly()
        {
            ChainSpec chainSpec = new() { Parameters = new ChainParameters(), NetworkId = 2, ChainId = 5 };

            ChainSpecBasedSpecProvider provider = new(chainSpec);
            Assert.AreEqual(2, provider.NetworkId);
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
            Assert.AreEqual(100, provider.GetSpec((ForkActivation)3).DifficultyBombDelay);
            Assert.AreEqual(300, provider.GetSpec((ForkActivation)7).DifficultyBombDelay);
            Assert.AreEqual(600, provider.GetSpec((ForkActivation)13).DifficultyBombDelay);
            Assert.AreEqual(1000, provider.GetSpec((ForkActivation)17).DifficultyBombDelay);
            Assert.AreEqual(1500, provider.GetSpec((ForkActivation)19).DifficultyBombDelay);
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
                    MaxCodeSizeTransition = maxCodeTransition,
                    MaxCodeSize = maxCodeSize
                }
            };

            ChainSpecBasedSpecProvider provider = new(chainSpec);
            Assert.AreEqual(long.MaxValue, provider.GetSpec((ForkActivation)(maxCodeTransition - 1)).MaxCodeSize, "one before");
            Assert.AreEqual(maxCodeSize, provider.GetSpec((ForkActivation)maxCodeTransition).MaxCodeSize, "at transition");
            Assert.AreEqual(maxCodeSize, provider.GetSpec((ForkActivation)(maxCodeTransition + 1)).MaxCodeSize, "one after");
        }

        [Test]
        public void Eip2200_is_set_correctly_directly()
        {
            ChainSpec chainSpec = new() { Parameters = new ChainParameters { Eip2200Transition = 5 } };

            ChainSpecBasedSpecProvider provider = new(chainSpec);
            provider.GetSpec((ForkActivation)5).IsEip2200Enabled.Should().BeTrue();
        }

        [Test]
        public void Eip2200_is_set_correctly_indirectly()
        {
            ChainSpec chainSpec =
                new() { Parameters = new ChainParameters { Eip1706Transition = 5, Eip1283Transition = 5 } };

            ChainSpecBasedSpecProvider provider = new(chainSpec);
            provider.GetSpec((ForkActivation)5).IsEip2200Enabled.Should().BeTrue();
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
            provider.GetSpec((ForkActivation)5).IsEip2200Enabled.Should().BeTrue();
        }

        [Test]
        public void Eip2200_is_not_set_correctly_indirectly_after_disabling_eip1283()
        {
            ChainSpec chainSpec = new()
            {
                Parameters = new ChainParameters
                {
                    Eip1706Transition = 5,
                    Eip1283Transition = 1,
                    Eip1283DisableTransition = 4
                }
            };

            ChainSpecBasedSpecProvider provider = new(chainSpec);
            provider.GetSpec((ForkActivation)5).IsEip2200Enabled.Should().BeFalse();
        }

        [Test]
        public void Eip_transitions_loaded_correctly()
        {
            const long maxCodeTransition = 1;
            const long maxCodeSize = 1;

            var currentTimestamp = Timestamper.Default.UnixTime.Seconds;
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
                    MergeForkIdTransition = 40000L,
                    Eip3651TransitionTimestamp = 1000000012,
                    Eip3855TransitionTimestamp = 1000000012,
                    Eip3860TransitionTimestamp = 1000000012,
                    Eip1153TransitionTimestamp = 1000000024,
                }
            };

            ChainSpecBasedSpecProvider provider = new(chainSpec);
            Assert.AreEqual(long.MaxValue, provider.GetSpec((ForkActivation)(maxCodeTransition - 1)).MaxCodeSize, "one before");
            Assert.AreEqual(maxCodeSize, provider.GetSpec((ForkActivation)maxCodeTransition).MaxCodeSize, "at transition");
            Assert.AreEqual(maxCodeSize, provider.GetSpec((ForkActivation)(maxCodeTransition + 1)).MaxCodeSize, "one after");

            ReleaseSpec expected = new();

            void TestTransitions(ForkActivation activation, Action<ReleaseSpec> changes)
            {
                changes(expected);
                IReleaseSpec underTest = provider.GetSpec(activation);
                underTest.Should().BeEquivalentTo(expected);
            }

            TestTransitions((ForkActivation)0L, r =>
            {
                r.MinGasLimit = 11L;
                r.GasLimitBoundDivisor = 13L;
                r.MaximumExtraDataSize = 17L;
                r.MaxCodeSize = long.MaxValue;
                r.Eip1559TransitionBlock = 15590L;
                r.IsTimeAdjustmentPostOlympic = true;
                r.MaximumUncleCount = 2;
                r.WithdrawalTimestamp = ulong.MaxValue;
                r.Eip4844TransitionTimestamp = ulong.MaxValue;
            });

            TestTransitions((ForkActivation)1L, r =>
            {
                r.MaxCodeSize = maxCodeSize;
                r.IsEip170Enabled = true;
            });
            TestTransitions((ForkActivation)70L, r => { r.IsEip2Enabled = r.IsEip7Enabled = true; });
            TestTransitions((ForkActivation)1000L, r => { r.IsEip100Enabled = true; });
            TestTransitions((ForkActivation)1400L, r => { r.IsEip140Enabled = true; });
            TestTransitions((ForkActivation)1450L, r => { r.IsEip145Enabled = true; });
            TestTransitions((ForkActivation)1500L, r => { r.IsEip150Enabled = true; });
            TestTransitions((ForkActivation)1520L, r => { r.IsEip152Enabled = true; });
            TestTransitions((ForkActivation)1550L, r => { r.IsEip155Enabled = true; });
            TestTransitions((ForkActivation)1580L, r => { r.IsEip158Enabled = true; });
            TestTransitions((ForkActivation)1600L, r => { r.IsEip160Enabled = true; });
            TestTransitions((ForkActivation)1960L,
                r => { r.IsEip196Enabled = r.IsEip197Enabled = r.IsEip198Enabled = r.IsEip649Enabled = true; });
            TestTransitions((ForkActivation)2110L, r => { r.IsEip211Enabled = true; });
            TestTransitions((ForkActivation)2140L, r => { r.IsEip214Enabled = true; });
            TestTransitions((ForkActivation)6580L, r => { r.IsEip658Enabled = r.IsEip1234Enabled = true; });
            TestTransitions((ForkActivation)10140L, r => { r.IsEip1014Enabled = true; });
            TestTransitions((ForkActivation)10520L, r => { r.IsEip1052Enabled = true; });
            TestTransitions((ForkActivation)11180L, r => { r.IsEip1108Enabled = true; });
            TestTransitions((ForkActivation)12830L, r => { r.IsEip1283Enabled = true; });
            TestTransitions((ForkActivation)12831L, r => { r.IsEip1283Enabled = false; });
            TestTransitions((ForkActivation)13440L, r => { r.IsEip1344Enabled = true; });
            TestTransitions((ForkActivation)15590L, r => { r.IsEip1559Enabled = true; });
            TestTransitions((ForkActivation)15591L, r => { r.Eip1559FeeCollector = Address.SystemUser; });
            TestTransitions((ForkActivation)15592L, r => { r.Eip1559BaseFeeMinValue = UInt256.UInt128MaxValue; });
            TestTransitions((ForkActivation)18840L, r => { r.IsEip1884Enabled = true; });
            TestTransitions((ForkActivation)20280L, r => { r.IsEip2028Enabled = true; });
            TestTransitions((ForkActivation)22000L, r => { r.IsEip2200Enabled = true; });
            TestTransitions((ForkActivation)23000L, r => { r.IsEip1283Enabled = r.IsEip1344Enabled = true; });
            TestTransitions((ForkActivation)24000L, r => { r.IsEip2315Enabled = r.ValidateChainId = r.ValidateReceipts = true; });
            TestTransitions((ForkActivation)29290L, r => { r.IsEip2929Enabled = r.IsEip2537Enabled = r.IsEip2565Enabled = true; });
            TestTransitions((ForkActivation)29300L, r => { r.IsEip2930Enabled = true; });
            TestTransitions((ForkActivation)31980L, r => { r.IsEip3198Enabled = true; });
            TestTransitions((ForkActivation)35290L, r => { r.IsEip3529Enabled = true; });
            TestTransitions((ForkActivation)35410L, r => { r.IsEip3541Enabled = true; });

            TestTransitions((41000L, 1000000012), r =>
            {
                r.IsEip3651Enabled = true;
                r.IsEip3855Enabled = true;
                r.IsEip3860Enabled = true;
            });
            TestTransitions((40001L, 1000000024), r => { r.IsEip1153Enabled = true; });
        }
    }
}
