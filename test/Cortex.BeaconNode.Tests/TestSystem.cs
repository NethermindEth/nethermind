using System;
using System.Collections.Generic;
using System.Text;
using Cortex.Containers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cortex.BeaconNode.Tests
{
    public static class TestSystem
    {
        public static IServiceProvider BuildTestServiceProvider(bool useBls = true)
        {
            var services = new ServiceCollection();
            services.AddLogging(configure => configure.AddConsole());
            var configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.Development.json")
                //.AddInMemoryCollection(GetMinimalConfigurationDictionary())
                .Build();
            services.AddBeaconNode(configuration);

            if (!useBls)
            {
                // NOTE: Can't mock ByRef Span<T>
                var testCryptographyService = new TestCryptographyService();
                services.AddSingleton<ICryptographyService>(testCryptographyService);
            }

            var options = new ServiceProviderOptions() { ValidateOnBuild = false };

            return services.BuildServiceProvider(options);
        }

        public static IDictionary<string, string> GetMinimalConfigurationDictionary()
        {
            var configuration = new Dictionary<string, string>
            {
                // Miscellaneous parameters
                ["MAX_COMMITTEES_PER_SLOT"] = "4",
                ["TARGET_COMMITTEE_SIZE"] = "4",
                ["MAX_VALIDATORS_PER_COMMITTEE"] = "2048",
                ["MIN_PER_EPOCH_CHURN_LIMIT"] = "4",
                ["CHURN_LIMIT_QUOTIENT"] = "65536",
                ["SHUFFLE_ROUND_COUNT"] = "10",
                ["MIN_GENESIS_ACTIVE_VALIDATOR_COUNT"] = "64",
                ["MIN_GENESIS_TIME"] = "1578009600", // Jan 3, 2020

                // Gwei values
                //["MIN_DEPOSIT_AMOUNT"] = "",
                ["MAX_EFFECTIVE_BALANCE"] = "32000000000",
                ["EJECTION_BALANCE"] = "16000000000",
                ["EFFECTIVE_BALANCE_INCREMENT"] = "1000000000",

                // Initial values
                //["GENESIS_SLOT"] = "0",
                ["GENESIS_EPOCH"] = "0",
                ["BLS_WITHDRAWAL_PREFIX"] = "0x00",

                // Time parameters
                ["SECONDS_PER_SLOT"] = "6",
                ["MIN_ATTESTATION_INCLUSION_DELAY"] = "1",
                ["SLOTS_PER_EPOCH"] = "8",
                ["MIN_SEED_LOOKAHEAD"] = "1",
                ["MAX_SEED_LOOKAHEAD"] = "4",
                ["SLOTS_PER_ETH1_VOTING_PERIOD"] = "16",
                ["SLOTS_PER_HISTORICAL_ROOT"] = "64",
                ["MIN_VALIDATOR_WITHDRAWABILITY_DELAY"] = "256",
                ["PERSISTENT_COMMITTEE_PERIOD"] = "2048",
                ["MIN_EPOCHS_TO_INACTIVITY_PENALTY"] = "4",

                // State list lengths
                ["EPOCHS_PER_HISTORICAL_VECTOR"] = "64",
                ["EPOCHS_PER_SLASHINGS_VECTOR"] = "64",
                ["HISTORICAL_ROOTS_LIMIT"] = "16777216",
                ["VALIDATOR_REGISTRY_LIMIT"] = "1099511627776",

                // Reward and penalty quotients
                ["BASE_REWARD_FACTOR"] = "64",
                ["WHISTLEBLOWER_REWARD_QUOTIENT"] = "512",
                ["PROPOSER_REWARD_QUOTIENT"] = "8",
                ["INACTIVITY_PENALTY_QUOTIENT"] = "33554432",
                ["MIN_SLASHING_PENALTY_QUOTIENT"] = "32",

                // Max operations per block
                ["MAX_PROPOSER_SLASHINGS"] = "16",
                ["MAX_ATTESTER_SLASHINGS"] = "1",
                ["MAX_ATTESTATIONS"] = "128",
                ["MAX_DEPOSITS"] = "16",
                ["MAX_VOLUNTARY_EXITS"] = "16",

                // Signature domains
                ["DOMAIN_BEACON_PROPOSER"] = "0x00000000",
                ["DOMAIN_BEACON_ATTESTER"] = "0x01000000",
                ["DOMAIN_RANDAO"] = "0x02000000",
                ["DOMAIN_DEPOSIT"] = "0x03000000",
                ["DOMAIN_VOLUNTARY_EXIT"] = "0x04000000",
                ["DOMAIN_CUSTODY_BIT_CHALLENGE"] = "0x06000000",
                ["DOMAIN_SHARD_PROPOSER"] = "0x80000000",
                ["DOMAIN_SHARD_ATTESTER"] = "0x81000000",

                // Fork choice configuration
                ["SAFE_SLOTS_TO_UPDATE_JUSTIFIED"] = "8",
            };
            return configuration;
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
