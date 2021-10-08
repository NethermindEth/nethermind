//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nethermind.BeaconNode.Eth1Bridge.MockedStart;
using Nethermind.Core2;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using NUnit.Framework;
using Shouldly;

namespace Nethermind.BeaconNode.Eth1Bridge.Test.MockedStart
{
    [TestFixture]
    public class QuickStartTests
    {
        private readonly TestData[] _testDataItems = 
        {
            new TestData
            (
                "0x25295f0d1d592a90b333e26e85149708208e9f8e8bc18f6c77bd62f8ad7a6866",
                "0xa99a76ed7796f7be22d5b7e85deeb7c5677e88e511e0b337618f8c4eb61349b4bf2d153f649f7b53359fe8b94a38e44c"
            ),
            new TestData
            (
                "0x51d0b65185db6989ab0b560d6deed19c7ead0e24b9b6372cbecb1f26bdfad000",
                "0xb89bebc699769726a318c8e9971bd3171297c61aea4a6578a7a4f94b547dcba5bac16a89108b6b6a1fe3695d1a874a0b"
            ),
            new TestData
            (
                "0x315ed405fafe339603932eebe8dbfd650ce5dafa561f6928664c75db85f97857",
                "0xa3a32b0f8b4ddb83f1a0a853d81dd725dfe577d4f4c3db8ece52ce2b026eca84815c1a7e8e92a4de3d755733bf7e4a9b"
            ),
            new TestData
            (
                "0x25b1166a43c109cb330af8945d364722757c65ed2bfed5444b5a2f057f82d391",
                "0x88c141df77cd9d8d7a71a75c826c41a9c9f03c6ee1b180f3e7852f6a280099ded351b58d66e653af8e42816a4d8f532e"
            ),
            new TestData
            (
                "0x3f5615898238c4c4f906b507ee917e9ea1bb69b93f1dbd11a34d229c3b06784b",
                "0x81283b7a20e1ca460ebd9bbd77005d557370cabb1f9a44f530c4c4c66230f675f8df8b4c2818851aa7d77a80ca5a4a5e"
            ),
            new TestData
            (
                "0x055794614bc85ed5436c1f5cab586aab6ca84835788621091f4f3b813761e7a8",
                "0xab0bdda0f85f842f431beaccf1250bf1fd7ba51b4100fd64364b6401fda85bb0069b3e715b58819684e7fc0b10a72a34"
            ),
            new TestData
            (
                "0x1023c68852075965e0f7352dee3f76a84a83e7582c181c10179936c6d6348893",
                "0x9977f1c8b731a8d5558146bfb86caea26434f3c5878b589bf280a42c9159e700e9df0e4086296c20b011d2e78c27d373"
            ),
            new TestData
            (
                "0x3a941600dc41e5d20e818473b817a28507c23cdfdb4b659c15461ee5c71e41f5",
                "0xa8d4c7c27795a725961317ef5953a7032ed6d83739db8b0e8a72353d1b8b4439427f7efa2c89caa03cc9f28f8cbab8ac"
            ),
            new TestData
            (
                "0x066e3bdc0415530e5c7fed6382d5c822c192b620203cf669903e1810a8c67d06",
                "0xa6d310dbbfab9a22450f59993f87a4ce5db6223f3b5f1f30d2c4ec718922d400e0b3c7741de8e59960f72411a0ee10a7"
            ),
            new TestData
            (
                "0x2b3b88a041168a1c4cd04bdd8de7964fd35238f95442dc678514f9dadb81ec34",
                "0x9893413c00283a3f9ed9fd9845dda1cea38228d22567f9541dccc357e54a2d6a6e204103c92564cbc05f4905ac7c493a"
            ),
        };

        [Test]
        public async Task TestGenerationOfFirstValidator()
        {
            // Arrange
            var overrideConfiguration = new Dictionary<string, string>
            {
                ["QuickStart:GenesisTime"] = "1578009600",
                ["QuickStart:ValidatorCount"] = "1",
                ["BeaconChain:MiscellaneousParameters:MinimumGenesisActiveValidatorCount"] = "1"
            };
            IServiceCollection testServiceCollection = TestSystem.BuildTestServiceCollection(overrideConfiguration);
            ServiceProvider testServiceProvider = testServiceCollection.BuildServiceProvider();

            // Act
            Eth1BridgeWorker eth1BridgeWorker =
                testServiceProvider.GetServices<IHostedService>().OfType<Eth1BridgeWorker>().First();
            await eth1BridgeWorker.ExecuteEth1GenesisAsync(CancellationToken.None);

            // Assert
            testServiceProvider.GetService<IEth1GenesisProvider>().ShouldBeOfType<QuickStartMockEth1GenesisProvider>();
            IStore store = testServiceProvider.GetService<IStore>();
            BeaconState state = await store.GetBlockStateAsync(store.FinalizedCheckpoint.Root);

            BlsPublicKey expectedKey0 = new BlsPublicKey(_testDataItems[0].PublicKey);
            state.Validators[0].PublicKey.ShouldBe(expectedKey0);
        }
        
        [Test]
        public async Task TestValidatorKeyGeneration10()
        {
            // Arrange
            var overrideConfiguration = new Dictionary<string, string>
            {
                ["QuickStart:GenesisTime"] = "1578009600",
                ["QuickStart:ValidatorCount"] = "10",
                ["BeaconChain:MiscellaneousParameters:MinimumGenesisActiveValidatorCount"] = "10"
            };
            IServiceCollection testServiceCollection = TestSystem.BuildTestServiceCollection(overrideConfiguration);
            ServiceProvider testServiceProvider = testServiceCollection.BuildServiceProvider();

            // Act
            Eth1BridgeWorker eth1BridgeWorker =
                testServiceProvider.GetServices<IHostedService>().OfType<Eth1BridgeWorker>().First();
            await eth1BridgeWorker.ExecuteEth1GenesisAsync(CancellationToken.None);

            // Assert
            testServiceProvider.GetService<IEth1GenesisProvider>().ShouldBeOfType<QuickStartMockEth1GenesisProvider>();
            IStore store = testServiceProvider.GetService<IStore>();
            BeaconState state = await store.GetBlockStateAsync(store.FinalizedCheckpoint.Root);

            for (int index = 1; index < 10; index++)
            {
                BlsPublicKey expectedKey = new BlsPublicKey(_testDataItems[index].PublicKey);
                state.Validators[index].PublicKey.ShouldBe(expectedKey, $"Validator index {index}");
            }
        }

        [Test]
        public async Task TestValidatorKeyGeneration64()
        {
            // Arrange
            var overrideConfiguration = new Dictionary<string, string>
            {
                ["QuickStart:GenesisTime"] = "1578009600",
                ["QuickStart:ValidatorCount"] = "64"
            };
            IServiceCollection testServiceCollection = TestSystem.BuildTestServiceCollection(overrideConfiguration);
            ServiceProvider testServiceProvider = testServiceCollection.BuildServiceProvider();

            // Act
            Eth1BridgeWorker eth1BridgeWorker =
                testServiceProvider.GetServices<IHostedService>().OfType<Eth1BridgeWorker>().First();
            await eth1BridgeWorker.ExecuteEth1GenesisAsync(CancellationToken.None);

            // Assert
            testServiceProvider.GetService<IEth1GenesisProvider>().ShouldBeOfType<QuickStartMockEth1GenesisProvider>();
            IStore store = testServiceProvider.GetService<IStore>();
            BeaconState state = await store.GetBlockStateAsync(store.FinalizedCheckpoint.Root);

            state.Validators.Count.ShouldBe(64);
        }

        
//        [TestMethod]
//        public async Task TestValidatorKeyGeneration10000()
//        {
//            Stopwatch stopwatch = Stopwatch.StartNew();
//            
//            // Arrange
//            IServiceCollection testServiceCollection = TestSystem.BuildTestServiceCollection(useStore: true);
//            IConfigurationRoot configuration = new ConfigurationBuilder()
//                .AddInMemoryCollection(new Dictionary<string, string> {["QuickStart:ValidatorCount"] = "10000"})
//                .Build();
//            testServiceCollection.AddQuickStart(configuration);
//            ServiceProvider testServiceProvider = testServiceCollection.BuildServiceProvider();
//
//            // Act
//            INodeStart quickStart = testServiceProvider.GetService<INodeStart>();
//            await quickStart.InitializeNodeAsync();
//
//            // Assert
//            IStoreProvider storeProvider = testServiceProvider.GetService<IStoreProvider>();
//            storeProvider.TryGetStore(out IStore? store).ShouldBeTrue();
//            if (!store!.TryGetBlockState(store.FinalizedCheckpoint.Root, out BeaconState? state))
//            {
//                throw new InvalidDataException("Missing finalized checkpoint block state");
//            }
//            state!.Validators.Count.ShouldBe(10000);
//            
//            Console.WriteLine("Generate quickstart 10,000, took {0}", stopwatch.Elapsed);
//        }

        [Test]
        public void GeneratePrivateKey63()
        {
            // Hash of the validator index (as little endian bytes), is converted to integer as little endian,
            // then that integer (after modulus) is used as the big endian private key.
            // However, Hash(63) is small enough that it only has 31 bytes (not 32),
            // so when writing we have to ensure the padding is at the correct end.
            //
            // i.e. little endian 63 = 0x3f,
            // Hash(0x3f00000000000000000000000000000000000000000000000000000000000000)
            //    = 0xbdd4daa5482af796206dc02a6780cfb51efe5e16c491c61bd9bf64cfc0da6e00
            // Little endian this is 195862955980072989385619164996948568652240008241846978685122117889393611965
            // When converted to bytes big endian (as per Eth BLS), this is only 31 bytes:
            //      0x6edac0cf64bfd91bc691c4165efe1eb5cf80672ac06d2096f72a48a5dad4bd
            // If written to an array/span it would pad on the right, but needs to pad on the left:
            //      0x006edac0cf64bfd91bc691c4165efe1eb5cf80672ac06d2096f72a48a5dad4bd
            
            // Arrange
            var overrideConfiguration = new Dictionary<string, string>
            {
                ["QuickStart:GenesisTime"] = "1578009600",
                ["QuickStart:ValidatorCount"] = "64"
            };
            IServiceCollection testServiceCollection = TestSystem.BuildTestServiceCollection(overrideConfiguration);
            ServiceProvider testServiceProvider = testServiceCollection.BuildServiceProvider();

            // Act
            QuickStartMockEth1GenesisProvider quickStartMockEth1GenesisProvider = (QuickStartMockEth1GenesisProvider) testServiceProvider.GetService<IEth1GenesisProvider>();
            byte[] privateKey = quickStartMockEth1GenesisProvider.GeneratePrivateKey(63);

            // Assert
            privateKey.Length.ShouldBe(32);
            privateKey[0].ShouldBe((byte)0x00);
            privateKey[1].ShouldBe((byte)0x6e);
            privateKey[31].ShouldBe((byte)0xbd);
        }

        private class TestData
        {
            public TestData(string privateKey, string publicKey)
            {
                PrivateKey = privateKey;
                PublicKey = publicKey;
            }

            public string PrivateKey { get; }
            public string PublicKey { get; }
        }
    }
}