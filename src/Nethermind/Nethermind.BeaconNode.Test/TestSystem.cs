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
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Nethermind.BeaconNode.Storage;
using Nethermind.Core2;
using Nethermind.Core2.Configuration;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Cryptography;
using Nethermind.Core2.Types;
using NSubstitute;

namespace Nethermind.BeaconNode.Test
{
    public static class TestSystem
    {
        public static IServiceCollection BuildTestServiceCollection(bool useBls = true, bool useStore = false)
        {
            var services = new ServiceCollection();
            
            var configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json")
                .AddJsonFile("Development/appsettings.json")
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["Peering:Mothra:LogSignedBeaconBlockJson"] = "false",
                    ["Storage:InMemory:LogBlockJson"] = "false",
                    ["Storage:InMemory:LogBlockStateJson"] = "false"
                })
                .Build();
            services.AddSingleton<IConfiguration>(configuration);

            services.AddLogging(configure =>
            {
                configure.SetMinimumLevel(LogLevel.Trace);
                configure.AddConsole(options => { 
                    options.Format = ConsoleLoggerFormat.Systemd;
                    options.DisableColors = true;
                    options.IncludeScopes = true;
                    options.TimestampFormat = " HH':'mm':'sszz ";
                });
            });
            
            services.ConfigureBeaconChain(configuration);
            services.AddBeaconNode(configuration);

            if (useBls)
            {
                services.AddCryptographyService(configuration);
            }
            else
            {
                services.AddSingleton<ICryptographyService, TestCryptographyService>();
            }

            if (useStore)
            {
                services.AddBeaconNodeStorage(configuration);
            }

            var networkPeering = Substitute.For<INetworkPeering>();
            services.AddSingleton<INetworkPeering>(networkPeering);

            services.AddTransient<IFileSystem, MockFileSystem>();
            
            return services;
        }

        public static IServiceProvider BuildTestServiceProvider(bool useBls = true, bool useStore = false)
        {
            var services = BuildTestServiceCollection(useBls, useStore);
            var options = new ServiceProviderOptions() { ValidateOnBuild = false };
            return services.BuildServiceProvider(options);
        }

        public class TestCryptographyService : ICryptographyService
        {
            private readonly ICryptographyService _cryptographyService;

            public TestCryptographyService(ChainConstants chainConstants,
                IOptionsMonitor<MiscellaneousParameters> miscellaneousParameterOptions,
                IOptionsMonitor<TimeParameters> timeParameterOptions,
                IOptionsMonitor<StateListLengths> stateListLengthOptions,
                IOptionsMonitor<MaxOperationsPerBlock> maxOperationsPerBlockOptions)
            {
                _cryptographyService = new CryptographyService(chainConstants, miscellaneousParameterOptions, timeParameterOptions, stateListLengthOptions, maxOperationsPerBlockOptions);
            }

            public BlsPublicKey BlsAggregatePublicKeys(IList<BlsPublicKey> publicKeys)
            {
                return BlsPublicKey.Zero;
            }

            public bool BlsAggregateVerify(IList<BlsPublicKey> publicKeys, IList<Root> signingRoots, BlsSignature signature)
            {
                throw new NotImplementedException();
            }

            public bool BlsFastAggregateVerify(IList<BlsPublicKey> publicKey, Root signingRoot, BlsSignature signature)
            {
                return true;
            }

            public bool BlsVerify(BlsPublicKey publicKey, Root signingRoot, BlsSignature signature)
            {
                return true;
            }

            public Bytes32 Hash(Bytes32 a, Bytes32 b)
            {
                return _cryptographyService.Hash(a, b);
            }

            public Bytes32 Hash(ReadOnlySpan<byte> bytes)
            {
                return _cryptographyService.Hash(bytes);
            }

            public Root HashTreeRoot(AttestationData attestationData)
            {
                throw new NotImplementedException();
            }

            public Root HashTreeRoot(BeaconBlock beaconBlock)
            {
                throw new NotImplementedException();
            }

            public Root HashTreeRoot(BeaconBlockBody beaconBlockBody)
            {
                throw new NotImplementedException();
            }

            public Root HashTreeRoot(BeaconBlockHeader beaconBlockHeader)
            {
                throw new NotImplementedException();
            }

            public Root HashTreeRoot(BeaconState beaconState)
            {
                throw new NotImplementedException();
            }

            public Root HashTreeRoot(DepositData depositData)
            {
                throw new NotImplementedException();
            }

            public Root HashTreeRoot(DepositMessage depositMessage)
            {
                throw new NotImplementedException();
            }

            public Root HashTreeRoot(List<Ref<DepositData>> depositData)
            {
                throw new NotImplementedException();
            }

            public Root HashTreeRoot(IList<DepositData> depositData)
            {
                throw new NotImplementedException();
            }

            public Root HashTreeRoot(List<DepositData> depositData)
            {
                throw new NotImplementedException();
            }

            public Root HashTreeRoot(Epoch epoch)
            {
                throw new NotImplementedException();
            }

            public Root HashTreeRoot(HistoricalBatch historicalBatch)
            {
                throw new NotImplementedException();
            }

            public Root HashTreeRoot(SigningRoot signingRoot)
            {
                throw new NotImplementedException();
            }

            public Root HashTreeRoot(VoluntaryExit voluntaryExit)
            {
                throw new NotImplementedException();
            }
        }
    }
}
