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

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Nethermind.Core.Json;
using Nethermind.Core.Specs;
using Nethermind.Core.Specs.ChainSpecStyle;
using NUnit.Framework;

namespace Nethermind.Core.Test.Specs.ChainSpecStyle
{
    [TestFixture]
    public class ChainSpecBasedSpecProviderTests
    {
        [Test]
        public void Rinkeby_loads_properly()
        {
            ChainSpecLoader loader = new ChainSpecLoader(new EthereumJsonSerializer());
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../Chains/rinkeby.json");
            ChainSpec chainSpec = loader.Load(File.ReadAllBytes(path));
            ChainSpecBasedSpecProvider provider = new ChainSpecBasedSpecProvider(chainSpec);
            RinkebySpecProvider rinkeby = RinkebySpecProvider.Instance;

            IReleaseSpec oldRinkebySpec = rinkeby.GetSpec(3660663);
            IReleaseSpec newRinkebySpec = provider.GetSpec(3660663);

            PropertyInfo[] propertyInfos = typeof(IReleaseSpec).GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (PropertyInfo propertyInfo in propertyInfos.Where(pi =>
                pi.Name != "MaximumExtraDataSize"
                && pi.Name != "Registrar"
                && pi.Name != "BlockReward"
                && pi.Name != "DifficultyBombDelay"
                && pi.Name != "DifficultyBoundDivisor"))
            {
                object a = propertyInfo.GetValue(oldRinkebySpec);
                object b = propertyInfo.GetValue(newRinkebySpec);

                Assert.AreEqual(a, b, propertyInfo.Name);
            }
        }
        
        [Test]
        public void Mainnet_loads_properly()
        {
            ChainSpecLoader loader = new ChainSpecLoader(new EthereumJsonSerializer());
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../Chains/foundation.json");
            ChainSpec chainSpec = loader.Load(File.ReadAllBytes(path));
            ChainSpecBasedSpecProvider provider = new ChainSpecBasedSpecProvider(chainSpec);
            MainNetSpecProvider mainnet = MainNetSpecProvider.Instance;

            IReleaseSpec oldSpec = mainnet.GetSpec(7280000);
            IReleaseSpec newSpec = provider.GetSpec(7280000);

            PropertyInfo[] propertyInfos = typeof(IReleaseSpec).GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (PropertyInfo propertyInfo in propertyInfos)
            {
                object a = propertyInfo.GetValue(oldSpec);
                object b = propertyInfo.GetValue(newSpec);

                Assert.AreEqual(a, b, propertyInfo.Name);
            }
        }

        [Test]
        public void Chain_id_is_set_correctly()
        {
            ChainSpec chainSpec = new ChainSpec();
            chainSpec.Parameters = new ChainParameters();
            chainSpec.ChainId = 5;

            ChainSpecBasedSpecProvider provider = new ChainSpecBasedSpecProvider(chainSpec);
            Assert.AreEqual(5, provider.ChainId);
        }

        [Test]
        public void Dao_block_number_is_set_correctly()
        {
            ChainSpec chainSpec = new ChainSpec();
            chainSpec.Parameters = new ChainParameters();
            chainSpec.DaoForkBlockNumber = 23;

            ChainSpecBasedSpecProvider provider = new ChainSpecBasedSpecProvider(chainSpec);
            Assert.AreEqual(23, provider.DaoBlockNumber);
        }

        [Test]
        public void Bound_divisors_set_correctly()
        {
            ChainSpec chainSpec = new ChainSpec();
            chainSpec.Parameters = new ChainParameters();
            chainSpec.Parameters.GasLimitBoundDivisor = 17;
            chainSpec.Ethash = new EthashParameters();
            chainSpec.Ethash.DifficultyBoundDivisor = 19;

            ChainSpecBasedSpecProvider provider = new ChainSpecBasedSpecProvider(chainSpec);
            Assert.AreEqual(19, provider.GenesisSpec.DifficultyBoundDivisor);
            Assert.AreEqual(17, provider.GenesisSpec.GasLimitBoundDivisor);
        }

        [Test]
        public void Difficulty_bomb_delays_loaded_correctly()
        {
            ChainSpec chainSpec = new ChainSpec();
            chainSpec.Parameters = new ChainParameters();
            chainSpec.Ethash = new EthashParameters();
            chainSpec.Ethash.DifficultyBombDelays = new Dictionary<long, long>
            {
                {3, 100},
                {7, 200},
                {13, 300},
                {17, 400},
                {19, 500},
            };

            ChainSpecBasedSpecProvider provider = new ChainSpecBasedSpecProvider(chainSpec);
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

            ChainSpec chainSpec = new ChainSpec();
            chainSpec.Parameters = new ChainParameters();
            chainSpec.Parameters.MaxCodeSizeTransition = maxCodeTransition;
            chainSpec.Parameters.MaxCodeSize = maxCodeSize;

            ChainSpecBasedSpecProvider provider = new ChainSpecBasedSpecProvider(chainSpec);
            Assert.AreEqual(long.MaxValue, provider.GetSpec(maxCodeTransition - 1).MaxCodeSize, "one before");
            Assert.AreEqual(maxCodeSize, provider.GetSpec(maxCodeTransition).MaxCodeSize, "at transition");
            Assert.AreEqual(maxCodeSize, provider.GetSpec(maxCodeTransition + 1).MaxCodeSize, "one after");
        }

        [Test]
        public void Eip_transitions_loaded_correctly()
        {
            const long maxCodeTransition = 1;
            const long maxCodeSize = 1;

            ChainSpec chainSpec = new ChainSpec();
            chainSpec.Ethash = new EthashParameters();
            chainSpec.Ethash.HomesteadTransition = 70;
            chainSpec.Ethash.Eip100bTransition = 1000;

            chainSpec.ByzantiumBlockNumber = 1960;
            chainSpec.ConstantinopleBlockNumber = 6490;

            chainSpec.Parameters = new ChainParameters();
            chainSpec.Parameters.MaxCodeSizeTransition = maxCodeTransition;
            chainSpec.Parameters.MaxCodeSize = maxCodeSize;
            chainSpec.Parameters.Registrar = Address.Zero;
            chainSpec.Parameters.MinGasLimit = 11;
            chainSpec.Parameters.GasLimitBoundDivisor = 13;
            chainSpec.Parameters.MaximumExtraDataSize = 17;

            chainSpec.Parameters.Eip140Transition = 1400L;
            chainSpec.Parameters.Eip145Transition = 1450L;
            chainSpec.Parameters.Eip150Transition = 1500L;
            chainSpec.Parameters.Eip155Transition = 1550L;
            chainSpec.Parameters.Eip160Transition = 1600L;
            chainSpec.Parameters.Eip161abcTransition = 1580L;
            chainSpec.Parameters.Eip161dTransition = 1580L;
            chainSpec.Parameters.Eip211Transition = 2110L;
            chainSpec.Parameters.Eip214Transition = 2140L;
            chainSpec.Parameters.Eip658Transition = 6580L;
            chainSpec.Parameters.Eip1014Transition = 10140L;
            chainSpec.Parameters.Eip1052Transition = 10520L;
            chainSpec.Parameters.Eip1283Transition = 12830L;
            chainSpec.Parameters.Eip1283DisableTransition = 12831L;

            ChainSpecBasedSpecProvider provider = new ChainSpecBasedSpecProvider(chainSpec);
            Assert.AreEqual(long.MaxValue, provider.GetSpec(maxCodeTransition - 1).MaxCodeSize, "one before");
            Assert.AreEqual(maxCodeSize, provider.GetSpec(maxCodeTransition).MaxCodeSize, "at transition");
            Assert.AreEqual(maxCodeSize, provider.GetSpec(maxCodeTransition + 1).MaxCodeSize, "one after");

            IReleaseSpec releaseSpec0 = provider.GetSpec(0L);
            Assert.AreEqual(Address.Zero, releaseSpec0.Registrar);
            Assert.AreEqual(11L, releaseSpec0.MinGasLimit);
            Assert.AreEqual(13L, releaseSpec0.GasLimitBoundDivisor);
            Assert.AreEqual(17L, releaseSpec0.MaximumExtraDataSize);

            Assert.AreEqual(long.MaxValue, releaseSpec0.MaxCodeSize);
            Assert.AreEqual(false, releaseSpec0.IsEip2Enabled);
            Assert.AreEqual(false, releaseSpec0.IsEip7Enabled);
            Assert.AreEqual(false, releaseSpec0.IsEip100Enabled);
            Assert.AreEqual(false, releaseSpec0.IsEip140Enabled);
            Assert.AreEqual(false, releaseSpec0.IsEip145Enabled);
            Assert.AreEqual(false, releaseSpec0.IsEip150Enabled);
            Assert.AreEqual(false, releaseSpec0.IsEip155Enabled);
            Assert.AreEqual(false, releaseSpec0.IsEip158Enabled);
            Assert.AreEqual(false, releaseSpec0.IsEip160Enabled);
            Assert.AreEqual(false, releaseSpec0.IsEip170Enabled);
            Assert.AreEqual(false, releaseSpec0.IsEip196Enabled);
            Assert.AreEqual(false, releaseSpec0.IsEip197Enabled);
            Assert.AreEqual(false, releaseSpec0.IsEip198Enabled);
            Assert.AreEqual(false, releaseSpec0.IsEip211Enabled);
            Assert.AreEqual(false, releaseSpec0.IsEip214Enabled);
            Assert.AreEqual(false, releaseSpec0.IsEip649Enabled);
            Assert.AreEqual(false, releaseSpec0.IsEip658Enabled);
            Assert.AreEqual(false, releaseSpec0.IsEip1014Enabled);
            Assert.AreEqual(false, releaseSpec0.IsEip1052Enabled);
            Assert.AreEqual(false, releaseSpec0.IsEip1234Enabled);
            Assert.AreEqual(false, releaseSpec0.IsEip1283Enabled);

            IReleaseSpec releaseSpec1 = provider.GetSpec(1L);
            Assert.AreEqual(maxCodeSize, releaseSpec1.MaxCodeSize);
            Assert.AreEqual(false, releaseSpec1.IsEip2Enabled);
            Assert.AreEqual(false, releaseSpec1.IsEip7Enabled);
            Assert.AreEqual(false, releaseSpec1.IsEip100Enabled);
            Assert.AreEqual(false, releaseSpec1.IsEip140Enabled);
            Assert.AreEqual(false, releaseSpec1.IsEip145Enabled);
            Assert.AreEqual(false, releaseSpec1.IsEip150Enabled);
            Assert.AreEqual(false, releaseSpec1.IsEip155Enabled);
            Assert.AreEqual(false, releaseSpec1.IsEip158Enabled);
            Assert.AreEqual(false, releaseSpec1.IsEip160Enabled);
            Assert.AreEqual(true, releaseSpec1.IsEip170Enabled);
            Assert.AreEqual(false, releaseSpec1.IsEip196Enabled);
            Assert.AreEqual(false, releaseSpec1.IsEip197Enabled);
            Assert.AreEqual(false, releaseSpec1.IsEip198Enabled);
            Assert.AreEqual(false, releaseSpec1.IsEip211Enabled);
            Assert.AreEqual(false, releaseSpec1.IsEip214Enabled);
            Assert.AreEqual(false, releaseSpec1.IsEip649Enabled);
            Assert.AreEqual(false, releaseSpec1.IsEip658Enabled);
            Assert.AreEqual(false, releaseSpec1.IsEip1014Enabled);
            Assert.AreEqual(false, releaseSpec1.IsEip1052Enabled);
            Assert.AreEqual(false, releaseSpec1.IsEip1234Enabled);
            Assert.AreEqual(false, releaseSpec1.IsEip1283Enabled);

            IReleaseSpec releaseSpec7 = provider.GetSpec(70L);
            Assert.AreEqual(releaseSpec7.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, releaseSpec7.IsEip2Enabled);
            Assert.AreEqual(true, releaseSpec7.IsEip7Enabled);
            Assert.AreEqual(false, releaseSpec7.IsEip100Enabled);
            Assert.AreEqual(false, releaseSpec7.IsEip140Enabled);
            Assert.AreEqual(false, releaseSpec7.IsEip145Enabled);
            Assert.AreEqual(false, releaseSpec7.IsEip150Enabled);
            Assert.AreEqual(false, releaseSpec7.IsEip155Enabled);
            Assert.AreEqual(false, releaseSpec7.IsEip158Enabled);
            Assert.AreEqual(false, releaseSpec7.IsEip160Enabled);
            Assert.AreEqual(true, releaseSpec7.IsEip170Enabled);
            Assert.AreEqual(false, releaseSpec7.IsEip196Enabled);
            Assert.AreEqual(false, releaseSpec7.IsEip197Enabled);
            Assert.AreEqual(false, releaseSpec7.IsEip198Enabled);
            Assert.AreEqual(false, releaseSpec7.IsEip211Enabled);
            Assert.AreEqual(false, releaseSpec7.IsEip214Enabled);
            Assert.AreEqual(false, releaseSpec7.IsEip649Enabled);
            Assert.AreEqual(false, releaseSpec7.IsEip658Enabled);
            Assert.AreEqual(false, releaseSpec7.IsEip1014Enabled);
            Assert.AreEqual(false, releaseSpec7.IsEip1052Enabled);
            Assert.AreEqual(false, releaseSpec7.IsEip1234Enabled);
            Assert.AreEqual(false, releaseSpec7.IsEip1283Enabled);

            IReleaseSpec releaseSpec100 = provider.GetSpec(1000L);
            Assert.AreEqual(releaseSpec100.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, releaseSpec100.IsEip2Enabled);
            Assert.AreEqual(true, releaseSpec100.IsEip7Enabled);
            Assert.AreEqual(true, releaseSpec100.IsEip100Enabled);
            Assert.AreEqual(false, releaseSpec100.IsEip140Enabled);
            Assert.AreEqual(false, releaseSpec100.IsEip145Enabled);
            Assert.AreEqual(false, releaseSpec100.IsEip150Enabled);
            Assert.AreEqual(false, releaseSpec100.IsEip155Enabled);
            Assert.AreEqual(false, releaseSpec100.IsEip158Enabled);
            Assert.AreEqual(false, releaseSpec100.IsEip160Enabled);
            Assert.AreEqual(true, releaseSpec100.IsEip170Enabled);
            Assert.AreEqual(false, releaseSpec100.IsEip196Enabled);
            Assert.AreEqual(false, releaseSpec100.IsEip197Enabled);
            Assert.AreEqual(false, releaseSpec100.IsEip198Enabled);
            Assert.AreEqual(false, releaseSpec100.IsEip211Enabled);
            Assert.AreEqual(false, releaseSpec100.IsEip214Enabled);
            Assert.AreEqual(false, releaseSpec100.IsEip649Enabled);
            Assert.AreEqual(false, releaseSpec100.IsEip658Enabled);
            Assert.AreEqual(false, releaseSpec100.IsEip1014Enabled);
            Assert.AreEqual(false, releaseSpec100.IsEip1052Enabled);
            Assert.AreEqual(false, releaseSpec100.IsEip1234Enabled);
            Assert.AreEqual(false, releaseSpec100.IsEip1283Enabled);

            IReleaseSpec releaseSpec140 = provider.GetSpec(1400L);
            Assert.AreEqual(releaseSpec100.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, releaseSpec140.IsEip2Enabled);
            Assert.AreEqual(true, releaseSpec140.IsEip7Enabled);
            Assert.AreEqual(true, releaseSpec140.IsEip100Enabled);
            Assert.AreEqual(true, releaseSpec140.IsEip140Enabled);
            Assert.AreEqual(false, releaseSpec140.IsEip145Enabled);
            Assert.AreEqual(false, releaseSpec140.IsEip150Enabled);
            Assert.AreEqual(false, releaseSpec140.IsEip155Enabled);
            Assert.AreEqual(false, releaseSpec140.IsEip158Enabled);
            Assert.AreEqual(false, releaseSpec140.IsEip160Enabled);
            Assert.AreEqual(true, releaseSpec140.IsEip170Enabled);
            Assert.AreEqual(false, releaseSpec140.IsEip196Enabled);
            Assert.AreEqual(false, releaseSpec140.IsEip197Enabled);
            Assert.AreEqual(false, releaseSpec140.IsEip198Enabled);
            Assert.AreEqual(false, releaseSpec140.IsEip211Enabled);
            Assert.AreEqual(false, releaseSpec140.IsEip214Enabled);
            Assert.AreEqual(false, releaseSpec140.IsEip649Enabled);
            Assert.AreEqual(false, releaseSpec140.IsEip658Enabled);
            Assert.AreEqual(false, releaseSpec140.IsEip1014Enabled);
            Assert.AreEqual(false, releaseSpec140.IsEip1052Enabled);
            Assert.AreEqual(false, releaseSpec140.IsEip1234Enabled);
            Assert.AreEqual(false, releaseSpec140.IsEip1283Enabled);

            IReleaseSpec releaseSpec145 = provider.GetSpec(1450L);
            Assert.AreEqual(releaseSpec145.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, releaseSpec145.IsEip2Enabled);
            Assert.AreEqual(true, releaseSpec145.IsEip7Enabled);
            Assert.AreEqual(true, releaseSpec145.IsEip100Enabled);
            Assert.AreEqual(true, releaseSpec145.IsEip140Enabled);
            Assert.AreEqual(true, releaseSpec145.IsEip145Enabled);
            Assert.AreEqual(false, releaseSpec145.IsEip150Enabled);
            Assert.AreEqual(false, releaseSpec145.IsEip155Enabled);
            Assert.AreEqual(false, releaseSpec145.IsEip158Enabled);
            Assert.AreEqual(false, releaseSpec145.IsEip160Enabled);
            Assert.AreEqual(true, releaseSpec145.IsEip170Enabled);
            Assert.AreEqual(false, releaseSpec145.IsEip196Enabled);
            Assert.AreEqual(false, releaseSpec145.IsEip197Enabled);
            Assert.AreEqual(false, releaseSpec145.IsEip198Enabled);
            Assert.AreEqual(false, releaseSpec145.IsEip211Enabled);
            Assert.AreEqual(false, releaseSpec145.IsEip214Enabled);
            Assert.AreEqual(false, releaseSpec145.IsEip649Enabled);
            Assert.AreEqual(false, releaseSpec145.IsEip658Enabled);
            Assert.AreEqual(false, releaseSpec145.IsEip1014Enabled);
            Assert.AreEqual(false, releaseSpec145.IsEip1052Enabled);
            Assert.AreEqual(false, releaseSpec145.IsEip1234Enabled);
            Assert.AreEqual(false, releaseSpec145.IsEip1283Enabled);

            IReleaseSpec releaseSpec150 = provider.GetSpec(1500L);
            Assert.AreEqual(releaseSpec150.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, releaseSpec150.IsEip2Enabled);
            Assert.AreEqual(true, releaseSpec150.IsEip7Enabled);
            Assert.AreEqual(true, releaseSpec150.IsEip100Enabled);
            Assert.AreEqual(true, releaseSpec150.IsEip140Enabled);
            Assert.AreEqual(true, releaseSpec150.IsEip145Enabled);
            Assert.AreEqual(true, releaseSpec150.IsEip150Enabled);
            Assert.AreEqual(false, releaseSpec150.IsEip155Enabled);
            Assert.AreEqual(false, releaseSpec150.IsEip158Enabled);
            Assert.AreEqual(false, releaseSpec150.IsEip160Enabled);
            Assert.AreEqual(true, releaseSpec150.IsEip170Enabled);
            Assert.AreEqual(false, releaseSpec150.IsEip196Enabled);
            Assert.AreEqual(false, releaseSpec150.IsEip197Enabled);
            Assert.AreEqual(false, releaseSpec150.IsEip198Enabled);
            Assert.AreEqual(false, releaseSpec150.IsEip211Enabled);
            Assert.AreEqual(false, releaseSpec150.IsEip214Enabled);
            Assert.AreEqual(false, releaseSpec150.IsEip649Enabled);
            Assert.AreEqual(false, releaseSpec150.IsEip658Enabled);
            Assert.AreEqual(false, releaseSpec150.IsEip1014Enabled);
            Assert.AreEqual(false, releaseSpec150.IsEip1052Enabled);
            Assert.AreEqual(false, releaseSpec150.IsEip1234Enabled);
            Assert.AreEqual(false, releaseSpec150.IsEip1283Enabled);

            IReleaseSpec releaseSpec155 = provider.GetSpec(1550L);
            Assert.AreEqual(releaseSpec155.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, releaseSpec155.IsEip2Enabled);
            Assert.AreEqual(true, releaseSpec155.IsEip7Enabled);
            Assert.AreEqual(true, releaseSpec155.IsEip100Enabled);
            Assert.AreEqual(true, releaseSpec155.IsEip140Enabled);
            Assert.AreEqual(true, releaseSpec155.IsEip145Enabled);
            Assert.AreEqual(true, releaseSpec155.IsEip150Enabled);
            Assert.AreEqual(true, releaseSpec155.IsEip155Enabled);
            Assert.AreEqual(false, releaseSpec155.IsEip158Enabled);
            Assert.AreEqual(false, releaseSpec155.IsEip160Enabled);
            Assert.AreEqual(true, releaseSpec155.IsEip170Enabled);
            Assert.AreEqual(false, releaseSpec155.IsEip196Enabled);
            Assert.AreEqual(false, releaseSpec155.IsEip197Enabled);
            Assert.AreEqual(false, releaseSpec155.IsEip198Enabled);
            Assert.AreEqual(false, releaseSpec155.IsEip211Enabled);
            Assert.AreEqual(false, releaseSpec155.IsEip214Enabled);
            Assert.AreEqual(false, releaseSpec155.IsEip649Enabled);
            Assert.AreEqual(false, releaseSpec155.IsEip658Enabled);
            Assert.AreEqual(false, releaseSpec155.IsEip1014Enabled);
            Assert.AreEqual(false, releaseSpec155.IsEip1052Enabled);
            Assert.AreEqual(false, releaseSpec155.IsEip1234Enabled);
            Assert.AreEqual(false, releaseSpec155.IsEip1283Enabled);

            IReleaseSpec releaseSpec158 = provider.GetSpec(1580L);
            Assert.AreEqual(releaseSpec158.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, releaseSpec158.IsEip2Enabled);
            Assert.AreEqual(true, releaseSpec158.IsEip7Enabled);
            Assert.AreEqual(true, releaseSpec158.IsEip100Enabled);
            Assert.AreEqual(true, releaseSpec158.IsEip140Enabled);
            Assert.AreEqual(true, releaseSpec158.IsEip145Enabled);
            Assert.AreEqual(true, releaseSpec158.IsEip150Enabled);
            Assert.AreEqual(true, releaseSpec158.IsEip155Enabled);
            Assert.AreEqual(true, releaseSpec158.IsEip158Enabled);
            Assert.AreEqual(false, releaseSpec158.IsEip160Enabled);
            Assert.AreEqual(true, releaseSpec158.IsEip170Enabled);
            Assert.AreEqual(false, releaseSpec158.IsEip196Enabled);
            Assert.AreEqual(false, releaseSpec158.IsEip197Enabled);
            Assert.AreEqual(false, releaseSpec158.IsEip198Enabled);
            Assert.AreEqual(false, releaseSpec158.IsEip211Enabled);
            Assert.AreEqual(false, releaseSpec158.IsEip214Enabled);
            Assert.AreEqual(false, releaseSpec158.IsEip649Enabled);
            Assert.AreEqual(false, releaseSpec158.IsEip658Enabled);
            Assert.AreEqual(false, releaseSpec158.IsEip1014Enabled);
            Assert.AreEqual(false, releaseSpec158.IsEip1052Enabled);
            Assert.AreEqual(false, releaseSpec158.IsEip1234Enabled);
            Assert.AreEqual(false, releaseSpec158.IsEip1283Enabled);

            IReleaseSpec releaseSpec160 = provider.GetSpec(1600L);
            Assert.AreEqual(releaseSpec160.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, releaseSpec160.IsEip2Enabled);
            Assert.AreEqual(true, releaseSpec160.IsEip7Enabled);
            Assert.AreEqual(true, releaseSpec160.IsEip100Enabled);
            Assert.AreEqual(true, releaseSpec160.IsEip140Enabled);
            Assert.AreEqual(true, releaseSpec160.IsEip145Enabled);
            Assert.AreEqual(true, releaseSpec160.IsEip150Enabled);
            Assert.AreEqual(true, releaseSpec160.IsEip155Enabled);
            Assert.AreEqual(true, releaseSpec160.IsEip158Enabled);
            Assert.AreEqual(true, releaseSpec160.IsEip160Enabled);
            Assert.AreEqual(true, releaseSpec160.IsEip170Enabled);
            Assert.AreEqual(false, releaseSpec160.IsEip196Enabled);
            Assert.AreEqual(false, releaseSpec160.IsEip197Enabled);
            Assert.AreEqual(false, releaseSpec160.IsEip198Enabled);
            Assert.AreEqual(false, releaseSpec160.IsEip211Enabled);
            Assert.AreEqual(false, releaseSpec160.IsEip214Enabled);
            Assert.AreEqual(false, releaseSpec160.IsEip649Enabled);
            Assert.AreEqual(false, releaseSpec160.IsEip658Enabled);
            Assert.AreEqual(false, releaseSpec160.IsEip1014Enabled);
            Assert.AreEqual(false, releaseSpec160.IsEip1052Enabled);
            Assert.AreEqual(false, releaseSpec160.IsEip1234Enabled);
            Assert.AreEqual(false, releaseSpec160.IsEip1283Enabled);

            IReleaseSpec releaseSpec170 = provider.GetSpec(1700L);
            Assert.AreEqual(releaseSpec170.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, releaseSpec170.IsEip2Enabled);
            Assert.AreEqual(true, releaseSpec170.IsEip7Enabled);
            Assert.AreEqual(true, releaseSpec170.IsEip100Enabled);
            Assert.AreEqual(true, releaseSpec170.IsEip140Enabled);
            Assert.AreEqual(true, releaseSpec170.IsEip145Enabled);
            Assert.AreEqual(true, releaseSpec170.IsEip150Enabled);
            Assert.AreEqual(true, releaseSpec170.IsEip155Enabled);
            Assert.AreEqual(true, releaseSpec170.IsEip158Enabled);
            Assert.AreEqual(true, releaseSpec170.IsEip160Enabled);
            Assert.AreEqual(true, releaseSpec170.IsEip170Enabled);
            Assert.AreEqual(false, releaseSpec170.IsEip196Enabled);
            Assert.AreEqual(false, releaseSpec170.IsEip197Enabled);
            Assert.AreEqual(false, releaseSpec170.IsEip198Enabled);
            Assert.AreEqual(false, releaseSpec170.IsEip211Enabled);
            Assert.AreEqual(false, releaseSpec170.IsEip214Enabled);
            Assert.AreEqual(false, releaseSpec170.IsEip649Enabled);
            Assert.AreEqual(false, releaseSpec170.IsEip658Enabled);
            Assert.AreEqual(false, releaseSpec170.IsEip1014Enabled);
            Assert.AreEqual(false, releaseSpec170.IsEip1052Enabled);
            Assert.AreEqual(false, releaseSpec170.IsEip1234Enabled);
            Assert.AreEqual(false, releaseSpec170.IsEip1283Enabled);

            IReleaseSpec releaseSpec196 = provider.GetSpec(1960L);
            Assert.AreEqual(releaseSpec196.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, releaseSpec196.IsEip2Enabled);
            Assert.AreEqual(true, releaseSpec196.IsEip7Enabled);
            Assert.AreEqual(true, releaseSpec196.IsEip100Enabled);
            Assert.AreEqual(true, releaseSpec196.IsEip140Enabled);
            Assert.AreEqual(true, releaseSpec196.IsEip145Enabled);
            Assert.AreEqual(true, releaseSpec196.IsEip150Enabled);
            Assert.AreEqual(true, releaseSpec196.IsEip155Enabled);
            Assert.AreEqual(true, releaseSpec196.IsEip158Enabled);
            Assert.AreEqual(true, releaseSpec196.IsEip160Enabled);
            Assert.AreEqual(true, releaseSpec196.IsEip170Enabled);
            Assert.AreEqual(true, releaseSpec196.IsEip196Enabled);
            Assert.AreEqual(true, releaseSpec196.IsEip197Enabled);
            Assert.AreEqual(true, releaseSpec196.IsEip198Enabled);
            Assert.AreEqual(false, releaseSpec196.IsEip211Enabled);
            Assert.AreEqual(false, releaseSpec196.IsEip214Enabled);
            Assert.AreEqual(true, releaseSpec196.IsEip649Enabled);
            Assert.AreEqual(false, releaseSpec196.IsEip658Enabled);
            Assert.AreEqual(false, releaseSpec196.IsEip1014Enabled);
            Assert.AreEqual(false, releaseSpec196.IsEip1052Enabled);
            Assert.AreEqual(false, releaseSpec196.IsEip1234Enabled);
            Assert.AreEqual(false, releaseSpec196.IsEip1283Enabled);

            IReleaseSpec releaseSpec211 = provider.GetSpec(2110L);
            Assert.AreEqual(releaseSpec211.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, releaseSpec211.IsEip2Enabled);
            Assert.AreEqual(true, releaseSpec211.IsEip7Enabled);
            Assert.AreEqual(true, releaseSpec211.IsEip100Enabled);
            Assert.AreEqual(true, releaseSpec211.IsEip140Enabled);
            Assert.AreEqual(true, releaseSpec211.IsEip145Enabled);
            Assert.AreEqual(true, releaseSpec211.IsEip150Enabled);
            Assert.AreEqual(true, releaseSpec211.IsEip155Enabled);
            Assert.AreEqual(true, releaseSpec211.IsEip158Enabled);
            Assert.AreEqual(true, releaseSpec211.IsEip160Enabled);
            Assert.AreEqual(true, releaseSpec211.IsEip170Enabled);
            Assert.AreEqual(true, releaseSpec211.IsEip196Enabled);
            Assert.AreEqual(true, releaseSpec211.IsEip197Enabled);
            Assert.AreEqual(true, releaseSpec211.IsEip198Enabled);
            Assert.AreEqual(true, releaseSpec211.IsEip211Enabled);
            Assert.AreEqual(false, releaseSpec211.IsEip214Enabled);
            Assert.AreEqual(true, releaseSpec211.IsEip649Enabled);
            Assert.AreEqual(false, releaseSpec211.IsEip658Enabled);
            Assert.AreEqual(false, releaseSpec211.IsEip1014Enabled);
            Assert.AreEqual(false, releaseSpec211.IsEip1052Enabled);
            Assert.AreEqual(false, releaseSpec211.IsEip1234Enabled);
            Assert.AreEqual(false, releaseSpec211.IsEip1283Enabled);

            IReleaseSpec releaseSpec214 = provider.GetSpec(2140L);
            Assert.AreEqual(releaseSpec214.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, releaseSpec214.IsEip2Enabled);
            Assert.AreEqual(true, releaseSpec214.IsEip7Enabled);
            Assert.AreEqual(true, releaseSpec214.IsEip100Enabled);
            Assert.AreEqual(true, releaseSpec214.IsEip140Enabled);
            Assert.AreEqual(true, releaseSpec214.IsEip145Enabled);
            Assert.AreEqual(true, releaseSpec214.IsEip150Enabled);
            Assert.AreEqual(true, releaseSpec214.IsEip155Enabled);
            Assert.AreEqual(true, releaseSpec214.IsEip158Enabled);
            Assert.AreEqual(true, releaseSpec214.IsEip160Enabled);
            Assert.AreEqual(true, releaseSpec214.IsEip170Enabled);
            Assert.AreEqual(true, releaseSpec214.IsEip196Enabled);
            Assert.AreEqual(true, releaseSpec214.IsEip197Enabled);
            Assert.AreEqual(true, releaseSpec214.IsEip198Enabled);
            Assert.AreEqual(true, releaseSpec214.IsEip211Enabled);
            Assert.AreEqual(true, releaseSpec214.IsEip214Enabled);
            Assert.AreEqual(true, releaseSpec214.IsEip649Enabled);
            Assert.AreEqual(false, releaseSpec214.IsEip658Enabled);
            Assert.AreEqual(false, releaseSpec214.IsEip1014Enabled);
            Assert.AreEqual(false, releaseSpec214.IsEip1052Enabled);
            Assert.AreEqual(false, releaseSpec214.IsEip1234Enabled);
            Assert.AreEqual(false, releaseSpec214.IsEip1283Enabled);

            IReleaseSpec releaseSpec658 = provider.GetSpec(6580L);
            Assert.AreEqual(releaseSpec658.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, releaseSpec658.IsEip2Enabled);
            Assert.AreEqual(true, releaseSpec658.IsEip7Enabled);
            Assert.AreEqual(true, releaseSpec658.IsEip100Enabled);
            Assert.AreEqual(true, releaseSpec658.IsEip140Enabled);
            Assert.AreEqual(true, releaseSpec658.IsEip145Enabled);
            Assert.AreEqual(true, releaseSpec658.IsEip150Enabled);
            Assert.AreEqual(true, releaseSpec658.IsEip155Enabled);
            Assert.AreEqual(true, releaseSpec658.IsEip158Enabled);
            Assert.AreEqual(true, releaseSpec658.IsEip160Enabled);
            Assert.AreEqual(true, releaseSpec658.IsEip170Enabled);
            Assert.AreEqual(true, releaseSpec658.IsEip196Enabled);
            Assert.AreEqual(true, releaseSpec658.IsEip197Enabled);
            Assert.AreEqual(true, releaseSpec658.IsEip198Enabled);
            Assert.AreEqual(true, releaseSpec658.IsEip211Enabled);
            Assert.AreEqual(true, releaseSpec658.IsEip214Enabled);
            Assert.AreEqual(true, releaseSpec658.IsEip649Enabled);
            Assert.AreEqual(true, releaseSpec658.IsEip658Enabled);
            Assert.AreEqual(false, releaseSpec658.IsEip1014Enabled);
            Assert.AreEqual(false, releaseSpec658.IsEip1052Enabled);
            Assert.AreEqual(true, releaseSpec658.IsEip1234Enabled);
            Assert.AreEqual(false, releaseSpec658.IsEip1283Enabled);

            IReleaseSpec releaseSpec1014 = provider.GetSpec(10140L);
            Assert.AreEqual(releaseSpec1014.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, releaseSpec1014.IsEip2Enabled);
            Assert.AreEqual(true, releaseSpec1014.IsEip7Enabled);
            Assert.AreEqual(true, releaseSpec1014.IsEip100Enabled);
            Assert.AreEqual(true, releaseSpec1014.IsEip140Enabled);
            Assert.AreEqual(true, releaseSpec1014.IsEip145Enabled);
            Assert.AreEqual(true, releaseSpec1014.IsEip150Enabled);
            Assert.AreEqual(true, releaseSpec1014.IsEip155Enabled);
            Assert.AreEqual(true, releaseSpec1014.IsEip158Enabled);
            Assert.AreEqual(true, releaseSpec1014.IsEip160Enabled);
            Assert.AreEqual(true, releaseSpec1014.IsEip170Enabled);
            Assert.AreEqual(true, releaseSpec1014.IsEip196Enabled);
            Assert.AreEqual(true, releaseSpec1014.IsEip197Enabled);
            Assert.AreEqual(true, releaseSpec1014.IsEip198Enabled);
            Assert.AreEqual(true, releaseSpec1014.IsEip211Enabled);
            Assert.AreEqual(true, releaseSpec1014.IsEip214Enabled);
            Assert.AreEqual(true, releaseSpec1014.IsEip649Enabled);
            Assert.AreEqual(true, releaseSpec1014.IsEip658Enabled);
            Assert.AreEqual(true, releaseSpec1014.IsEip1014Enabled);
            Assert.AreEqual(false, releaseSpec1014.IsEip1052Enabled);
            Assert.AreEqual(true, releaseSpec1014.IsEip1234Enabled);
            Assert.AreEqual(false, releaseSpec1014.IsEip1283Enabled);

            IReleaseSpec releaseSpec1052 = provider.GetSpec(10520L);
            Assert.AreEqual(releaseSpec1052.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, releaseSpec1052.IsEip2Enabled);
            Assert.AreEqual(true, releaseSpec1052.IsEip7Enabled);
            Assert.AreEqual(true, releaseSpec1052.IsEip100Enabled);
            Assert.AreEqual(true, releaseSpec1052.IsEip140Enabled);
            Assert.AreEqual(true, releaseSpec1052.IsEip145Enabled);
            Assert.AreEqual(true, releaseSpec1052.IsEip150Enabled);
            Assert.AreEqual(true, releaseSpec1052.IsEip155Enabled);
            Assert.AreEqual(true, releaseSpec1052.IsEip158Enabled);
            Assert.AreEqual(true, releaseSpec1052.IsEip160Enabled);
            Assert.AreEqual(true, releaseSpec1052.IsEip170Enabled);
            Assert.AreEqual(true, releaseSpec1052.IsEip196Enabled);
            Assert.AreEqual(true, releaseSpec1052.IsEip197Enabled);
            Assert.AreEqual(true, releaseSpec1052.IsEip198Enabled);
            Assert.AreEqual(true, releaseSpec1052.IsEip211Enabled);
            Assert.AreEqual(true, releaseSpec1052.IsEip214Enabled);
            Assert.AreEqual(true, releaseSpec1052.IsEip649Enabled);
            Assert.AreEqual(true, releaseSpec1052.IsEip658Enabled);
            Assert.AreEqual(true, releaseSpec1052.IsEip1014Enabled);
            Assert.AreEqual(true, releaseSpec1052.IsEip1052Enabled);
            Assert.AreEqual(true, releaseSpec1052.IsEip1234Enabled);
            Assert.AreEqual(false, releaseSpec1052.IsEip1283Enabled);

            IReleaseSpec releaseSpec1283 = provider.GetSpec(12830L);
            Assert.AreEqual(releaseSpec1283.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, releaseSpec1283.IsEip2Enabled);
            Assert.AreEqual(true, releaseSpec1283.IsEip7Enabled);
            Assert.AreEqual(true, releaseSpec1283.IsEip100Enabled);
            Assert.AreEqual(true, releaseSpec1283.IsEip140Enabled);
            Assert.AreEqual(true, releaseSpec1283.IsEip145Enabled);
            Assert.AreEqual(true, releaseSpec1283.IsEip150Enabled);
            Assert.AreEqual(true, releaseSpec1283.IsEip155Enabled);
            Assert.AreEqual(true, releaseSpec1283.IsEip158Enabled);
            Assert.AreEqual(true, releaseSpec1283.IsEip160Enabled);
            Assert.AreEqual(true, releaseSpec1283.IsEip170Enabled);
            Assert.AreEqual(true, releaseSpec1283.IsEip196Enabled);
            Assert.AreEqual(true, releaseSpec1283.IsEip197Enabled);
            Assert.AreEqual(true, releaseSpec1283.IsEip198Enabled);
            Assert.AreEqual(true, releaseSpec1283.IsEip211Enabled);
            Assert.AreEqual(true, releaseSpec1283.IsEip214Enabled);
            Assert.AreEqual(true, releaseSpec1283.IsEip649Enabled);
            Assert.AreEqual(true, releaseSpec1283.IsEip658Enabled);
            Assert.AreEqual(true, releaseSpec1283.IsEip1014Enabled);
            Assert.AreEqual(true, releaseSpec1283.IsEip1052Enabled);
            Assert.AreEqual(true, releaseSpec1283.IsEip1234Enabled);
            Assert.AreEqual(true, releaseSpec1283.IsEip1283Enabled);

            IReleaseSpec releaseSpec1283Disabled = provider.GetSpec(12831L);
            Assert.AreEqual(releaseSpec1283Disabled.MaxCodeSize, maxCodeSize);
            Assert.AreEqual(true, releaseSpec1283Disabled.IsEip2Enabled);
            Assert.AreEqual(true, releaseSpec1283Disabled.IsEip7Enabled);
            Assert.AreEqual(true, releaseSpec1283Disabled.IsEip100Enabled);
            Assert.AreEqual(true, releaseSpec1283Disabled.IsEip140Enabled);
            Assert.AreEqual(true, releaseSpec1283Disabled.IsEip145Enabled);
            Assert.AreEqual(true, releaseSpec1283Disabled.IsEip150Enabled);
            Assert.AreEqual(true, releaseSpec1283Disabled.IsEip155Enabled);
            Assert.AreEqual(true, releaseSpec1283Disabled.IsEip158Enabled);
            Assert.AreEqual(true, releaseSpec1283Disabled.IsEip160Enabled);
            Assert.AreEqual(true, releaseSpec1283Disabled.IsEip170Enabled);
            Assert.AreEqual(true, releaseSpec1283Disabled.IsEip196Enabled);
            Assert.AreEqual(true, releaseSpec1283Disabled.IsEip197Enabled);
            Assert.AreEqual(true, releaseSpec1283Disabled.IsEip198Enabled);
            Assert.AreEqual(true, releaseSpec1283Disabled.IsEip211Enabled);
            Assert.AreEqual(true, releaseSpec1283Disabled.IsEip214Enabled);
            Assert.AreEqual(true, releaseSpec1283Disabled.IsEip649Enabled);
            Assert.AreEqual(true, releaseSpec1283Disabled.IsEip658Enabled);
            Assert.AreEqual(true, releaseSpec1283Disabled.IsEip1014Enabled);
            Assert.AreEqual(true, releaseSpec1283Disabled.IsEip1052Enabled);
            Assert.AreEqual(true, releaseSpec1283Disabled.IsEip1234Enabled);
            Assert.AreEqual(false, releaseSpec1283Disabled.IsEip1283Enabled);
        }
    }
}