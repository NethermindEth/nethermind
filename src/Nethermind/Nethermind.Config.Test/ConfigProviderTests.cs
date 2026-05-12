// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Core.Extensions;
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

                Dictionary<string, string> args = new();
                if (bitArray.Get(4))
                {
                    args.Add("JsonRpc.Enabled", bitArray.Get(5).ToString());
                }

                Environment.SetEnvironmentVariable("NETHERMIND_JSONRPCCONFIG_ENABLED", null, EnvironmentVariableTarget.Process);
                if (bitArray.Get(2))
                {
                    Environment.SetEnvironmentVariable("NETHERMIND_JSONRPCCONFIG_ENABLED", bitArray.Get(3).ToString(), EnvironmentVariableTarget.Process);
                }

                Dictionary<string, string> fakeJson = new();
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

            configProvider.GetConfig<IBlocksConfig>().MinGasPrice.Should().Be(12345);
        }
    }

    [TestFixture]
    [Parallelizable(ParallelScope.None)]
    public class GetNonDefaultValuesTests
    {
        [Test]
        public void Returns_empty_when_no_overrides()
        {
            ConfigProvider configProvider = new();
            configProvider.Initialize();

            List<(string? Category, string Name, object? CurrentValue, object? DefaultValue)> nonDefaults =
                configProvider.GetNonDefaultValues().ToList();

            Assert.That(nonDefaults, Is.Empty);
        }

        [Test]
        public void Returns_overridden_value_with_current_and_default()
        {
            Dictionary<string, string> args = new()
            {
                { "Network.DiscoveryPort", "12345" }
            };
            ConfigProvider configProvider = new();
            configProvider.AddSource(new ArgsConfigSource(args));
            configProvider.Initialize();

            List<(string? Category, string Name, object? CurrentValue, object? DefaultValue)> nonDefaults =
                configProvider.GetNonDefaultValues()
                    .Where(static x => x.Category == "Network" && x.Name == nameof(INetworkConfig.DiscoveryPort))
                    .ToList();

            Assert.That(nonDefaults, Has.Count.EqualTo(1));
            Assert.That(nonDefaults[0].CurrentValue, Is.EqualTo(12345));
            Assert.That(nonDefaults[0].DefaultValue, Is.EqualTo(30303));
        }

        [Test]
        public void Surfaces_overrides_across_categories()
        {
            Dictionary<string, string> args = new()
            {
                { "Network.DiscoveryPort", "12345" },
                { "JsonRpc.Enabled", "true" }
            };
            ConfigProvider configProvider = new();
            configProvider.AddSource(new ArgsConfigSource(args));
            configProvider.Initialize();

            HashSet<string> keys = configProvider.GetNonDefaultValues()
                .Select(static x => $"{x.Category}.{x.Name}")
                .ToHashSet();

            Assert.That(keys, Does.Contain("Network.DiscoveryPort"));
            Assert.That(keys, Does.Contain("JsonRpc.Enabled"));
        }

        [Test]
        public void Skips_sensitive_properties()
        {
            Dictionary<string, string> args = new()
            {
                { "KeyStore.TestNodeKey", "0xdeadbeef" },
                { "KeyStore.Passwords", "[\"hunter2\"]" },
                { "KeyStore.KeyStoreDirectory", "custom-keystore" }
            };
            ConfigProvider configProvider = new();
            configProvider.AddSource(new ArgsConfigSource(args));
            configProvider.Initialize();

            HashSet<string> keys = configProvider.GetNonDefaultValues()
                .Select(static x => $"{x.Category}.{x.Name}")
                .ToHashSet();

            Assert.That(keys, Does.Not.Contain($"KeyStore.{nameof(IKeyStoreConfig.TestNodeKey)}"));
            Assert.That(keys, Does.Not.Contain($"KeyStore.{nameof(IKeyStoreConfig.Passwords)}"));
            Assert.That(keys, Does.Contain($"KeyStore.{nameof(IKeyStoreConfig.KeyStoreDirectory)}"));
        }

        [Test]
        public void Failing_provider_for_one_type_does_not_poison_others()
        {
            Dictionary<string, string> args = new() { { "Network.DiscoveryPort", "12345" } };
            ConfigProvider inner = new();
            inner.AddSource(new ArgsConfigSource(args));
            inner.Initialize();

            FailingProvider failing = new(inner, typeof(IJsonRpcConfig));
            List<(Type ConfigType, Exception Error)> errors = [];

            HashSet<string> keys = failing.GetNonDefaultValues((t, e) => errors.Add((t, e)))
                .Select(static x => $"{x.Category}.{x.Name}")
                .ToHashSet();

            Assert.That(keys, Does.Contain("Network.DiscoveryPort"));
            Assert.That(errors, Has.Some.Matches<(Type, Exception)>(static x => x.Item1 == typeof(IJsonRpcConfig)));
        }

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
