using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cortex.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nethermind.Core2.Configuration;
using Nethermind.BeaconNode.Containers;
using Nethermind.BeaconNode.MockedStart;
using Nethermind.BeaconNode.Services;
using Nethermind.BeaconNode.Ssz;
using Nethermind.BeaconNode.Storage;
using Nethermind.Core2;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using NSubstitute;
using Shouldly;

namespace Nethermind.BeaconNode.Test
{
    [TestClass]
    public class BlockProducerTest
    {
        [TestMethod]
        public async Task BasicNewBlock()
        {
            // Arrange
            int numberOfValidators = 64;
            int genesisTime = 1578009600;
            IServiceCollection testServiceCollection = TestSystem.BuildTestServiceCollection(useStore: true);
            IConfigurationRoot configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["QuickStart:ValidatorCount"] = $"{numberOfValidators}",
                    ["QuickStart:GenesisTime"] = $"{genesisTime}"
                })
                .Build();
            testServiceCollection.AddBeaconNodeQuickStart(configuration);
            testServiceCollection.AddSingleton<IHostEnvironment>(Substitute.For<IHostEnvironment>());
            ServiceProvider testServiceProvider = testServiceCollection.BuildServiceProvider();

            QuickStart quickStart = (QuickStart)testServiceProvider.GetService<INodeStart>();
            await quickStart.InitializeNodeAsync();
            
            IBeaconNodeApi beaconNode = testServiceProvider.GetService<IBeaconNodeApi>();
            Core2.Containers.Fork fork = await beaconNode.GetNodeForkAsync(CancellationToken.None);
            fork.CurrentVersion.ShouldBe(new ForkVersion());

            // Act
            BlockProducer blockProducer = testServiceProvider.GetService<BlockProducer>();
            Slot targetSlot = new Slot(1);
            // With QuickStart64, proposer for Slot 1 is validator index 20, 0xa1c76af1...
            byte[] privateKey = quickStart.GeneratePrivateKey(20);
            BlsSignature randaoReveal = GetEpochSignature(testServiceProvider, privateKey, fork.CurrentVersion, targetSlot);
            // value for quickstart 20/64, fork 0, slot 1
            randaoReveal.ToString().ShouldBe("0xa3426b6391a29c88f2280428d5fdae9e20f4c75a8d38d0714e3aa5b9e55594dbd555c4bc685191e83d39158c3be9744d06adc34b21d2885998a206e3b3fd435eab424cf1c01b8fd562deb411348a601e83d7332d8774d1fd3bf8b88d7a33c67c");
            BeaconBlock newBlock = await blockProducer.NewBlockAsync(targetSlot, randaoReveal);
            
            // Assert
            newBlock.Slot.ShouldBe(targetSlot);
            newBlock.Body.RandaoReveal.ShouldBe(randaoReveal);
            
            Hash32 expectedParentRoot = new Hash32(Bytes.FromHexString("0x3111350140726cc0501223143ae5c7baad7f5a06764fcc7d444a657016e7d616"));
            newBlock.ParentRoot.ShouldBe(expectedParentRoot);
            
            newBlock.Body.Eth1Data.DepositCount.ShouldBe((ulong)numberOfValidators);
            
            Hash32 expectedEth1DataDepositRoot = new Hash32(Bytes.FromHexString("0x66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925"));
            newBlock.Body.Eth1Data.DepositRoot.ShouldBe(expectedEth1DataDepositRoot);
            
            Hash32 expectedStateRoot = new Hash32(Bytes.FromHexString("0xba7192e86b77d51c5e7835ece9c572f01b7fc720300acac8d98b4db42eb94321"));
            newBlock.StateRoot.ShouldBe(expectedStateRoot);
            
            newBlock.Signature.ShouldBe(new BlsSignature(new byte[96])); // signature should be empty
            
            newBlock.Body.Attestations.Count.ShouldBe(0);
            newBlock.Body.Deposits.Count.ShouldBe(0);
        }

        private BlsSignature GetEpochSignature(IServiceProvider testServiceProvider, byte[] privateKey, ForkVersion forkVersion, Slot slot)
        {
            SignatureDomains signatureDomains = testServiceProvider.GetService<IOptions<SignatureDomains>>().Value;
            BeaconChainUtility beaconChainUtility = testServiceProvider.GetService<BeaconChainUtility>();
            var domain = beaconChainUtility.ComputeDomain(signatureDomains.Randao, forkVersion);

            var epoch = beaconChainUtility.ComputeEpochAtSlot(slot);

            var epochRoot = epoch.HashTreeRoot();
            
            BLSParameters parameters = new BLSParameters() { PrivateKey =  privateKey };
            BLS bls = BLS.Create(parameters);
            var destination = new Span<byte>(new byte[96]);
            bls.TrySignHash(epochRoot.AsSpan(), destination, out var bytesWritten, domain.AsSpan());
            
            var signature = new BlsSignature(destination.ToArray());
            return signature;
        }
    }
}
