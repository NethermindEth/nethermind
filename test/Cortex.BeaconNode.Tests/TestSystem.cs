using System;
using System.Collections.Generic;
using System.Text;
using Cortex.BeaconNode.Storage;
using Cortex.Containers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cortex.BeaconNode.Tests
{
    public static class TestSystem
    {
        public static IServiceProvider BuildTestServiceProvider(bool useBls = true, bool useStore = false)
        {
            var services = new ServiceCollection();
            services.AddLogging(configure => configure.AddConsole());
            var configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.Development.json")
                .Build();
            services.AddBeaconNode(configuration);

            if (!useBls)
            {
                // NOTE: Can't mock ByRef Span<T>
                var testCryptographyService = new TestCryptographyService();
                services.AddSingleton<ICryptographyService>(testCryptographyService);
            }

            if (useStore)
            {
                services.AddSingleton<IStoreProvider, StoreProvider>();
            }

            var options = new ServiceProviderOptions() { ValidateOnBuild = false };

            return services.BuildServiceProvider(options);
        }

        public class TestCryptographyService : ICryptographyService
        {
            ICryptographyService _cryptographyService = new CryptographyService();

            public BlsPublicKey BlsAggregatePublicKeys(IEnumerable<BlsPublicKey> publicKeys)
            {
                return new BlsPublicKey();
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
