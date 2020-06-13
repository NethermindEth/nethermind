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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nethermind.BeaconNode;
using Nethermind.BeaconNode.OApiClient;
using Nethermind.Core2;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Json;
using Nethermind.Core2.Types;
using Nethermind.HonestValidator.MockedStart;
using Nethermind.HonestValidator.Services;
using Nethermind.HonestValidator.Tests.Helpers;
using NSubstitute;
using NSubstitute.Core;
using Shouldly;
//using ValidatorDuty = Nethermind.BeaconNode.OApiClient.ValidatorDuty;

namespace Nethermind.HonestValidator.Test
{
    [TestClass]
    public class ValidatorClientTest
    {
        private TestContext? _testContext;

        public TestContext TestContext
        {
            get => _testContext!;
            set => _testContext = value;
        }

        [TestMethod]
        [Ignore("Disable test until OAPI is working again after 0.10.1 spec updates")]
        public async Task BasicSignBlockFromBeaconNodeProxy()
        {
            await Task.CompletedTask;
            /*
            // Epoch 0 proposals validators:
            // Slot 0, [56] 0x94f0c853 (attestation slot 4, shard 0) 
            // Slot 1, [20] 0xa1c76af1 (attestation slot 5, shard 1) 
            // Slot 2, [43] 0xb7ee0ef2 (attestation slot 5, shard 0) 
            // Slot 3, [19] 0x8d5d3672 (attestation slot 7, shard 1) 
            // Slot 4, [62] 0x80a2be2c (attestation slot 2, shard 1) 
            // Slot 5, [23] 0xaf344fce (attestation slot 6, shard 0) 
            // Slot 6, [13] 0x903e2989 (attestation slot 2, shard 0)
            
            // Arrange
            IServiceCollection testServiceCollection = TestSystem.BuildTestServiceCollection();
            
            IConfigurationRoot configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["QuickStart:ValidatorStartIndex"] = "0",
                    ["QuickStart:NumberOfValidators"] = "1"
                })
                .Build();
            testServiceCollection.AddHonestValidatorQuickStart(configuration);
            
            IBeaconNodeOApiClient beaconNodeOApiClient = Substitute.For<IBeaconNodeOApiClient>();

            Nethermind.BeaconNode.OApiClient.Response2 response2 = new Nethermind.BeaconNode.OApiClient.Response2()
            {
                Fork = new Fork()
                {
                    Epoch = 0,
                    Previous_version = new byte[ForkVersion.Length],
                    Current_version = new byte[ForkVersion.Length]
                }
            };
            beaconNodeOApiClient
                .ForkAsync(Arg.Any<CancellationToken>())
                .Returns(response2);
            
            byte[] publicKeyBytes =
                Bytes.FromHexString(
                    "0xa99a76ed7796f7be22d5b7e85deeb7c5677e88e511e0b337618f8c4eb61349b4bf2d153f649f7b53359fe8b94a38e44c");
            ValidatorDuty validatorDuty = new Nethermind.BeaconNode.OApiClient.ValidatorDuty()
            {
                Validator_pubkey = publicKeyBytes,
                Attestation_slot = 6,
                Attestation_shard = 0,
                Block_proposal_slot = 1
            };
            List<Nethermind.BeaconNode.OApiClient.ValidatorDuty> validatorDuties =
                new List<Nethermind.BeaconNode.OApiClient.ValidatorDuty>() {validatorDuty};
            beaconNodeOApiClient
                .DutiesAsync(Arg.Any<IEnumerable<byte[]>>(), Arg.Any<ulong?>(), Arg.Any<CancellationToken>())
                .Returns(validatorDuties);
            
            Deposits deposit = new Deposits()
            {
                Index = 0,
                Data = new Data()
                {
                    Amount = 1_000_000,
                    Pubkey = Enumerable.Repeat((byte)0x9a, 48).ToArray(),
                    Withdrawal_credentials = Enumerable.Repeat((byte)0xbc, 32).ToArray(),
                    Signature = Enumerable.Repeat((byte)0xde, 96).ToArray()
                },
                Proof = Enumerable.Repeat(Enumerable.Repeat((byte)0x01, 32).ToArray(), 32).ToList()
            };
            BeaconBlock beaconBlock = new BeaconBlock()
            {
                Slot = 1,
                Parent_root = "0x1212121212121212121212121212121212121212121212121212121212121212",
                State_root = "0x3434343434343434343434343434343434343434343434343434343434343434",
                Signature =  "0x000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000",
                Body = new BeaconBlockBody()
                {
                    Randao_reveal = Enumerable.Repeat((byte) 0x42, 96).ToArray(),
                    Eth1_data = new Eth1_data()
                    {
                        Block_hash = Enumerable.Repeat((byte) 0x56, 32).ToArray(),
                        Deposit_count = 1,
                        Deposit_root = Enumerable.Repeat((byte) 0x78, 32).ToArray()
                    },
                    Graffiti = new byte[32],
                    Attester_slashings = new List<Attester_slashings>(),
                    Proposer_slashings = new List<Proposer_slashings>(),
                    Attestations = new List<Attestations>(),
                    Deposits = new List<Deposits>() { deposit },
                    Voluntary_exits = new List<Voluntary_exits>()
                }
            };
            beaconNodeOApiClient
                .BlockAsync(Arg.Any<ulong>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
                .Returns(beaconBlock);
            
            JsonSerializerOptions options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            options.ConfigureNethermindCore2();
            string originalBlock = System.Text.Json.JsonSerializer.Serialize(beaconBlock, options);
            TestContext.WriteLine("Original block: {0}", originalBlock);

            IBeaconNodeOApiClientFactory beaconNodeOApiClientFactory = Substitute.For<IBeaconNodeOApiClientFactory>();
            beaconNodeOApiClientFactory.CreateClient(Arg.Any<string>()).Returns(beaconNodeOApiClient);
            testServiceCollection.AddSingleton<IBeaconNodeOApiClientFactory>(beaconNodeOApiClientFactory);

            ServiceProvider testServiceProvider = testServiceCollection.BuildServiceProvider();
            
            // Act
            IValidatorKeyProvider validatorKeyProvider = testServiceProvider.GetService<IValidatorKeyProvider>();
            
            ValidatorClient validatorClient = testServiceProvider.GetService<ValidatorClient>();
            BeaconChainInformation beaconChainInformation = testServiceProvider.GetService<BeaconChainInformation>();
            
            await validatorClient.OnTickAsync(beaconChainInformation, 7, new CancellationToken());
            
            // Assert
            List<ICall> clientReceived = beaconNodeOApiClient.ReceivedCalls().ToList();
            clientReceived.Count(x => x.GetMethodInfo().Name == nameof(beaconNodeOApiClient.BlockAsync)).ShouldBe(1);

            ICall publishCall =
                clientReceived.SingleOrDefault(x => x.GetMethodInfo().Name == nameof(beaconNodeOApiClient.Block2Async));
            publishCall.ShouldNotBeNull();
            BlsSignature blsSignature = (BlsSignature)publishCall.GetArguments()[1];

            // NOTE: This value not checked separately, just from running the test.
            // HACK: It should not even be passed this way as a parameter; just grabbing it to get green test. OAPI needs to be fixed.
            blsSignature.ToString().ShouldBe("0xa9e9f15bfb2bd949e0fbfa7f0a6d3273bf325144f25082c828f6e0638fc3ebcd98bce6e040b139f9ca7873125af9f61509551cf292864522d57702931b136f313463632fb1d152b6058ae3527453344ec9784507b025549778685494ae2b9442");
            */
        }
    }
}