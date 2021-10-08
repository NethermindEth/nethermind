using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nethermind.BeaconNode.Eth1Bridge;
using Nethermind.BeaconNode.Eth1Bridge.MockedStart;
using Nethermind.Core2.Configuration;
using Nethermind.BeaconNode.MockedStart;
using Nethermind.BeaconNode.Services;
using Nethermind.BeaconNode.Storage;
using Nethermind.Core2;
using Nethermind.Core2.Api;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Cryptography.Ssz;
using Nethermind.Core2.Types;
using NSubstitute;
using Shouldly;

namespace Nethermind.BeaconNode.Test
{
    [TestClass]
    public class BlockProducerTests
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
            testServiceCollection.AddBeaconNodeEth1Bridge(configuration);
            testServiceCollection.AddSingleton<IHostEnvironment>(Substitute.For<IHostEnvironment>());
            ServiceProvider testServiceProvider = testServiceCollection.BuildServiceProvider();

            Eth1BridgeWorker eth1BridgeWorker =
                testServiceProvider.GetServices<IHostedService>().OfType<Eth1BridgeWorker>().First();
            await eth1BridgeWorker.ExecuteEth1GenesisAsync(CancellationToken.None);
            
            IBeaconNodeApi beaconNode = testServiceProvider.GetService<IBeaconNodeApi>();
            ApiResponse<Fork> forkResponse = await beaconNode.GetNodeForkAsync(CancellationToken.None);
            Fork fork = forkResponse.Content;
            fork.CurrentVersion.ShouldBe(new ForkVersion(new byte[4] { 0x00, 0x00, 0x00, 0x01}));

            // Act
            BlockProducer blockProducer = testServiceProvider.GetService<BlockProducer>();
            Slot targetSlot = new Slot(1);

            // With QuickStart64, proposer for Slot 1 is validator index 29, 0xa98ed496...
            QuickStartMockEth1GenesisProvider quickStartMockEth1GenesisProvider = (QuickStartMockEth1GenesisProvider) testServiceProvider.GetService<IEth1GenesisProvider>();
            byte[] privateKey = quickStartMockEth1GenesisProvider.GeneratePrivateKey(29);
            BlsSignature randaoReveal = GetEpochSignature(testServiceProvider, privateKey, fork.CurrentVersion, targetSlot);
            
            // value for quickstart 20/64, fork 0, slot 1
            randaoReveal.ToString().StartsWith("0x932f8730");
            BeaconBlock newBlock = await blockProducer.NewBlockAsync(targetSlot, randaoReveal, CancellationToken.None);
            
            // Assert
            newBlock.Slot.ShouldBe(targetSlot);
            newBlock.Body.RandaoReveal.ShouldBe(randaoReveal);
            
            newBlock.ParentRoot.ToString().ShouldStartWith("0x4d4e9a16");
            
            newBlock.Body.Eth1Data.DepositCount.ShouldBe((ulong)numberOfValidators);
            
            newBlock.Body.Eth1Data.DepositRoot.ToString().ShouldStartWith("0x66687aad");
            
            newBlock.StateRoot.ToString().ShouldStartWith("0x0138b69f");
            
            newBlock.Body.Attestations.Count.ShouldBe(0);
            newBlock.Body.Deposits.Count.ShouldBe(0);
        }

        private BlsSignature GetEpochSignature(IServiceProvider testServiceProvider, byte[] privateKey, ForkVersion forkVersion, Slot slot)
        {
            SignatureDomains signatureDomains = testServiceProvider.GetService<IOptions<SignatureDomains>>().Value;
            IBeaconChainUtility beaconChainUtility = testServiceProvider.GetService<IBeaconChainUtility>();
            var domain = beaconChainUtility.ComputeDomain(signatureDomains.Randao, forkVersion);

            var epoch = beaconChainUtility.ComputeEpochAtSlot(slot);

            var epochRoot = epoch.HashTreeRoot();
            var epochSigningRoot = beaconChainUtility.ComputeSigningRoot(epochRoot, domain);
            
            BLSParameters parameters = new BLSParameters() { PrivateKey =  privateKey };
            BLS bls = BLS.Create(parameters);
            var destination = new Span<byte>(new byte[96]);
            bls.TrySignData(epochSigningRoot.AsSpan(), destination, out var bytesWritten);
            
            var signature = new BlsSignature(destination.ToArray());
            return signature;
        }
    }
}
