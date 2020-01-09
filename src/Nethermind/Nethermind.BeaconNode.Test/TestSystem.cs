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
using System.Xml.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethermind.BeaconNode.Services;
using Nethermind.BeaconNode.Storage;
using Nethermind.Core2;
using Nethermind.Core2.Configuration;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Cryptography;
using Nethermind.Core2.Types;
using NSubstitute;
using Hash32 = Nethermind.Core2.Crypto.Hash32;

namespace Nethermind.BeaconNode.Test
{
    public static class TestSystem
    {
        public static IServiceCollection BuildTestServiceCollection(bool useBls = true, bool useStore = false)
        {
            var services = new ServiceCollection();
            
            var configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.Development.json")
                .Build();
            services.AddSingleton<IConfiguration>(configuration);

            services.AddLogging(configure =>
            {
                configure.SetMinimumLevel(LogLevel.Trace);
                configure.AddConsole();
            });
            
            services.ConfigureBeaconChain(configuration);
            services.AddBeaconNode(configuration);

            if (useBls)
            {
                services.AddCryptographyService(configuration);
            }
            else
            {
                // NOTE: Can't mock ByRef Span<T>
                var testCryptographyService = new TestCryptographyService();
                services.AddSingleton<ICryptographyService>(testCryptographyService);
            }

            if (useStore)
            {
                services.AddSingleton<IStoreProvider, MemoryStoreProvider>();
            }

            var networkPeering = Substitute.For<INetworkPeering>();
            services.AddSingleton<INetworkPeering>(networkPeering);

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
            ICryptographyService _cryptographyService = new CortexCryptographyService();

            public BlsPublicKey BlsAggregatePublicKeys(IEnumerable<BlsPublicKey> publicKeys)
            {
                return BlsPublicKey.Empty;
            }

            public bool BlsVerify(BlsPublicKey publicKey, Hash32 signingRoot, BlsSignature signature, Domain domain)
            {
                return true;
            }

            public bool BlsVerifyMultiple(IEnumerable<BlsPublicKey> publicKeys, IEnumerable<Hash32> messageHashes, BlsSignature signature, Domain domain)
            {
                return true;
            }

            public Hash32 Hash(Hash32 a, Hash32 b)
            {
                return _cryptographyService.Hash(a, b);
            }

            public Hash32 Hash(ReadOnlySpan<byte> bytes)
            {
                return _cryptographyService.Hash(bytes);
            }
        }

    }
}
