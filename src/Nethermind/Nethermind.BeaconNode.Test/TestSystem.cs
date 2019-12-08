using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethermind.BeaconNode.Containers;
using Nethermind.BeaconNode.Services;
using Nethermind.BeaconNode.Storage;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Hash32 = Nethermind.Core2.Types.Hash32;

namespace Nethermind.BeaconNode.Tests
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
            
            services.AddBeaconNode(configuration);

            if (!useBls)
            {
                // NOTE: Can't mock ByRef Span<T>
                var testCryptographyService = new TestCryptographyService();
                services.AddSingleton<ICryptographyService>(testCryptographyService);
            }

            if (useStore)
            {
                services.AddSingleton<IStoreProvider, MemoryStoreProvider>();
            }

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
