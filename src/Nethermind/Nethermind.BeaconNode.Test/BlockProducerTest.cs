using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nethermind.BeaconNode.Containers;
using Nethermind.BeaconNode.MockedStart;
using Nethermind.BeaconNode.Services;
using Nethermind.BeaconNode.Storage;
using Nethermind.Core2;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using NSubstitute;
using Shouldly;

namespace Nethermind.BeaconNode.Tests
{
    [TestClass]
    public class BlockProducerTest
    {
        [TestMethod]
        public async Task BasicNewBlock()
        {
            // Arrange
            int numberOfValidators = 64;
            IServiceCollection testServiceCollection = TestSystem.BuildTestServiceCollection(useStore: true);
            IConfigurationRoot configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string> {["QuickStart:ValidatorCount"] = $"{numberOfValidators}"})
                .Build();
            testServiceCollection.AddQuickStart(configuration);
            testServiceCollection.AddSingleton<IHostEnvironment>(Substitute.For<IHostEnvironment>());
            ServiceProvider testServiceProvider = testServiceCollection.BuildServiceProvider();
            
            INodeStart quickStart = testServiceProvider.GetService<INodeStart>();
            await quickStart.InitializeNodeAsync();

            // Act
            BlockProducer blockProducer = testServiceProvider.GetService<BlockProducer>();
            Slot targetSlot = new Slot(1);
            BlsSignature randaoReveal = new BlsSignature(Bytes.FromHexString("0x0102030405060708090a0b0c0d0e0f100102030405060708090a0b0c0d0e0f100102030405060708090a0b0c0d0e0f100102030405060708090a0b0c0d0e0f100102030405060708090a0b0c0d0e0f100102030405060708090a0b0c0d0e0f10"));
            BeaconBlock newBlock = await blockProducer.NewBlockAsync(targetSlot, randaoReveal);
            
            // Assert
            newBlock.Slot.ShouldBe(targetSlot);
            newBlock.Body.RandaoReveal.ShouldBe(randaoReveal);
            Hash32 expectedParentRoot = new Hash32(Bytes.FromHexString("0x5054595adf55d7d426542d56e488d1222f012d0e004629775d1a047a68872382"));
            newBlock.ParentRoot.ShouldBe(expectedParentRoot);
            newBlock.Body.Eth1Data.DepositCount.ShouldBe((ulong)numberOfValidators);
            Hash32 expectedEth1DataDepositRoot = new Hash32(Bytes.FromHexString("0x66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925"));
            newBlock.Body.Eth1Data.DepositRoot.ShouldBe(expectedEth1DataDepositRoot);
        }
    }
}
