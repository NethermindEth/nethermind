// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.KeyStore.Config;
using Nethermind.Network.Config;
using NUnit.Framework;

namespace Nethermind.Config.Test
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class ConfigSourceHelperTests
    {
        [TestCase(typeof(int), 0)]
        [TestCase(typeof(bool), false)]
        [TestCase(typeof(long), 0L)]
        public void GetDefault_returns_correct_default_value_for_value_types(Type type, object expected)
        {
            object? result = ConfigSourceHelper.GetDefault(type);
            Assert.That(result, Is.EqualTo(expected));
        }

        [Test]
        public void GetDefault_returns_null_for_reference_types() => Assert.That(ConfigSourceHelper.GetDefault(typeof(string)), Is.Null);
    }

    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class DefaultConfigProviderTests
    {
        [Test]
        public void Can_read_without_sources()
        {
            ConfigProvider configProvider = new();
            INetworkConfig config = configProvider.GetConfig<INetworkConfig>();
            Assert.That(config.DiscoveryPort, Is.EqualTo(30303));
        }

        public int DefaultTestProperty { get; set; } = 5;

        [Test]
        public void Can_read_overwrites()
        {
            BitArray bitArray = new(6);
            for (int i = 0; i < 2 * 2 * 2 * 2 * 2 * 2; i++)
            {
                ConfigProvider configProvider = new();
                bitArray.Set(0, (i >> 0) % 2 == 1);
                bitArray.Set(1, (i >> 1) % 2 == 1);
                bitArray.Set(2, (i >> 2) % 2 == 1);
                bitArray.Set(3, (i >> 3) % 2 == 1);
                bitArray.Set(4, (i >> 4) % 2 == 1);
                bitArray.Set(5, (i >> 5) % 2 == 1);

                Dictionary<string, string> args = [];
                if (bitArray.Get(4))
                {
                    args.Add("JsonRpc.Enabled", bitArray.Get(5).ToString());
                }

                Environment.SetEnvironmentVariable("NETHERMIND_JSONRPCCONFIG_ENABLED", null, EnvironmentVariableTarget.Process);
                if (bitArray.Get(2))
                {
                    Environment.SetEnvironmentVariable("NETHERMIND_JSONRPCCONFIG_ENABLED", bitArray.Get(3).ToString(), EnvironmentVariableTarget.Process);
                }

                Dictionary<string, string> fakeJson = [];
                if (bitArray.Get(0))
                {
                    fakeJson.Add("JsonRpc.Enabled", bitArray.Get(1).ToString());
                }

                configProvider.AddSource(new ArgsConfigSource(args));
                configProvider.AddSource(new EnvConfigSource());
                configProvider.AddSource(new ArgsConfigSource(fakeJson));

                IJsonRpcConfig? config = configProvider.GetConfig<IJsonRpcConfig>();
                bool expectedResult = bitArray.Get(4)
                    ? bitArray.Get(5)
                    : bitArray.Get(2)
                        ? bitArray.Get(3)
                        : bitArray.Get(0) && bitArray.Get(1);

                Assert.That(config.Enabled, Is.EqualTo(expectedResult), bitArray.ToBitString());
            }
        }

        [Test]
        public void Can_useExistingConfig()
        {
            BlocksConfig blocksConfig = new()
            {
                MinGasPrice = 12345,
            };
            IConfigProvider configProvider = new ConfigProvider(blocksConfig);

            Assert.That(configProvider.GetConfig<IBlocksConfig>().MinGasPrice, Is.EqualTo((UInt256)12345));
        }
    }

    [TestFixture]
    [Parallelizable(ParallelScope.None)]
    public class GetNonDefaultValuesTests
    {
        [Test]
        public void Returns_empty_when_no_overrides() =>
            Assert.That(Provider().GetNonDefaultValues(), Is.Empty);

        [Test]
        public void Returns_overridden_value_with_current_and_default()
        {
            List<NonDefaultConfigValue> nonDefaults = Provider(("Network.DiscoveryPort", "12345"))
                .GetNonDefaultValues()
                .Where(static x => x.Category == "Network" && x.Name == nameof(INetworkConfig.DiscoveryPort))
                .ToList();

            Assert.That(nonDefaults, Has.Count.EqualTo(1));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(nonDefaults[0].CurrentValue, Is.EqualTo(12345));
                Assert.That(nonDefaults[0].DefaultValue, Is.EqualTo(30303));
            }
        }

        public static IEnumerable<TestCaseData> ReportedKeyCases()
        {
            yield return new TestCaseData(
                new[] { ("Network.DiscoveryPort", "12345"), ("JsonRpc.Enabled", "true") },
                new[] { "Network.DiscoveryPort", "JsonRpc.Enabled" },
                Array.Empty<string>())
                .SetName("Surfaces overrides across categories");

            yield return new TestCaseData(
                new[]
                {
                    ("KeyStore.TestNodeKey", "0xdeadbeef"),
                    ("KeyStore.Passwords", "[\"hunter2\"]"),
                    ("KeyStore.KeyStoreDirectory", "custom-keystore")
                },
                new[] { $"KeyStore.{nameof(IKeyStoreConfig.KeyStoreDirectory)}" },
                new[]
                {
                    $"KeyStore.{nameof(IKeyStoreConfig.TestNodeKey)}",
                    $"KeyStore.{nameof(IKeyStoreConfig.Passwords)}"
                })
                .SetName("Skips sensitive properties");
        }

        [TestCaseSource(nameof(ReportedKeyCases))]
        public void Reports_expected_keys(
            (string Key, string Value)[] overrides,
            string[] mustContain,
            string[] mustNotContain)
        {
            HashSet<string> keys = NonDefaultKeys(Provider(overrides));

            foreach (string key in mustContain) Assert.That(keys, Does.Contain(key));
            foreach (string key in mustNotContain) Assert.That(keys, Does.Not.Contain(key));
        }

        [Test]
        public void Failing_provider_for_one_type_does_not_poison_others()
        {
            FailingProvider failing = new(Provider(("Network.DiscoveryPort", "12345")), typeof(IJsonRpcConfig));
            List<(Type ConfigType, Exception Error)> errors = [];

            HashSet<string> keys = NonDefaultKeys(failing, (t, e) => errors.Add((t, e)));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(keys, Does.Contain("Network.DiscoveryPort"));
                Assert.That(errors, Has.Some.Matches<(Type, Exception)>(static x => x.Item1 == typeof(IJsonRpcConfig)));
            }
        }

        private static IConfigProvider Provider(params (string Key, string Value)[] overrides)
        {
            ConfigProvider provider = new();
            if (overrides.Length > 0)
                provider.AddSource(new ArgsConfigSource(overrides.ToDictionary(static o => o.Key, static o => o.Value)));
            provider.Initialize();
            return provider;
        }

        private static HashSet<string> NonDefaultKeys(IConfigProvider provider, Action<Type, Exception>? onError = null) =>
            provider.GetNonDefaultValues(onError)
                .Select(static x => $"{x.Category}.{x.Name}")
                .ToHashSet();

        private sealed class FailingProvider(IConfigProvider inner, Type failingType) : IConfigProvider
        {
            public T GetConfig<T>() where T : IConfig => (T)GetConfig(typeof(T));

            public IConfig GetConfig(Type configType) =>
                configType == failingType
                    ? throw new InvalidOperationException("simulated failure")
                    : inner.GetConfig(configType);

            public object? GetRawValue(string category, string name) => inner.GetRawValue(category, name);
        }
    }
}
