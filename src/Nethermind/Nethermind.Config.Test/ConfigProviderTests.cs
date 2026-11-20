// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Nethermind.Api;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.KeyStore.Config;
using Nethermind.Network.Config;
using NSubstitute;
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

        [TestCase(typeof(string[]), "[]", new string[0])]
        [TestCase(typeof(string[]), " [ ] ", new string[0])]
        [TestCase(typeof(int[]), "[]", new int[0])]
        [TestCase(typeof(NetworkNode[]), "[]", new object[0])]
        [TestCase(typeof(IEnumerable<string>), "[]", new string[0])]
        [TestCase(typeof(string[]), """["a\\b", "c,d"]""", new[] { "a\\b", "c,d" })]
        [TestCase(typeof(string[]), """["a", "b"]""", new[] { "a", "b" })]
        [TestCase(typeof(string[]), "[a, b]", new[] { "a", "b" })]
        [TestCase(typeof(string[]), "a,b", new[] { "a", "b" })]
        [TestCase(typeof(int[]), "[1, 2]", new[] { 1, 2 })]
        [TestCase(typeof(int[]), "1,2", new[] { 1, 2 })]
        [TestCase(typeof(string[]), "", new[] { "" })] // an explicit empty item, e.g. an empty password
        [TestCase(typeof(string[]), "a,", new[] { "a", "" })]
        [TestCase(typeof(string[]), """[""]""", new[] { "" })]
        [TestCase(typeof(string[]), "\"", new[] { "\"" })]
        [TestCase(typeof(string[]), "\"\",a", new[] { "", "a" })]
        public void ParseValue_parses_arrays(Type type, string valueString, IEnumerable expected) =>
            Assert.That(ConfigSourceHelper.ParseValue(type, valueString, "Category", "Name"), Is.EqualTo(expected).AsCollection);

        [TestCase(typeof(int), "0x10", 16)]
        [TestCase(typeof(uint), "0xFFFFFFFF", uint.MaxValue)]
        [TestCase(typeof(ulong), "0xFFFFFFFFFFFFFFFF", ulong.MaxValue)]
        [TestCase(typeof(long), "0xFFFFFFFFFFFFFFFF", -1L)]
        [TestCase(typeof(bool), "0x1", true)]
        [TestCase(typeof(string), "0x10", "0x10")]
        public void ParseValue_parses_hex_through_64_bit_integers(Type type, string valueString, object expected) =>
            Assert.That(ConfigSourceHelper.ParseValue(type, valueString, "Category", "Name"), Is.EqualTo(expected).And.TypeOf(type));

        [TestCase(typeof(int), "0xFFFFFFFF")]
        [TestCase(typeof(int), "0X10")]
        public void ParseValue_rejects_out_of_range_and_upper_case_prefixed_hex(Type type, string valueString) =>
            Assert.That(() => ConfigSourceHelper.ParseValue(type, valueString, "Category", "Name"),
                Throws.TypeOf<System.Configuration.ConfigurationErrorsException>());
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

        [Test]
        public void Can_read_obsolete_trie_cache_budget_without_changing_buffer_budget([Values] bool overrideLegacyBudget)
        {
            ConfigProvider provider = new();
            if (overrideLegacyBudget)
            {
                provider.AddSource(new ArgsConfigSource(new Dictionary<string, string>
                {
                    ["FlatDb.TrieCacheMemoryBudget"] = "1234"
                }));
            }

            IFlatDbConfig config = provider.GetConfig<IFlatDbConfig>();
            PropertyInfo property = typeof(IFlatDbConfig).GetProperty("TrieCacheMemoryBudget")!;
            StandardConfigTests.CheckDefault(property, new FlatDbConfig());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(property.GetValue(config), Is.EqualTo(overrideLegacyBudget ? 1234UL : 512UL.MiB));
                Assert.That(config.CompressedNodeRecordBufferMemoryBudget, Is.EqualTo(500UL.MiB));
                Assert.That(property.GetCustomAttribute<ObsoleteAttribute>(), Is.Not.Null);
                Assert.That(typeof(FlatDbConfig).GetProperty(property.Name)!.GetCustomAttribute<ObsoleteAttribute>(), Is.Not.Null);
                Assert.That(property.GetCustomAttribute<ConfigItemAttribute>()!.HiddenFromDocs, Is.True);
            }
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

        [TestCase('a', 32, true)]
        [TestCase('a', 33, false)]
        [TestCase('€', 11, false)] // 11 characters, 33 UTF-8 bytes
        public void ExtraData_is_limited_to_32_bytes(char character, int count, bool accepted)
        {
            string extraData = new(character, count);
            BlocksConfig config = new() { ExtraData = "previous" };

            if (accepted)
            {
                config.ExtraData = extraData;
                Assert.That(config.ExtraData, Is.EqualTo(extraData));
                return;
            }

            InvalidConfigurationException exception = Assert.Throws<InvalidConfigurationException>(() => config.ExtraData = extraData)!;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(exception.Message, Does.Contain(extraData));
                Assert.That(exception.ExitCode, Is.EqualTo(ExitCodes.TooLongExtraData));
                Assert.That(config.ExtraData, Is.EqualTo("previous"));
            }
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

        [Test]
        public void Reports_path_as_maskable_and_skips_password_file_paths()
        {
            List<NonDefaultConfigValue> nonDefaults = Provider(
                ("Init.BaseDbPath", "private-db"),
                ("KeyStore.PasswordFiles", "[\"secret-path\"]"))
                .GetNonDefaultValues()
                .ToList();

            NonDefaultConfigValue path = nonDefaults.Single(static x => x.Category == "Init" && x.Name == nameof(IInitConfig.BaseDbPath));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(path.CurrentValue, Is.EqualTo("private-db"));
                Assert.That(path.IsSensitiveWhenMasked, Is.True);
                Assert.That(path.ValueForLog(false), Is.EqualTo("private-db"));
                Assert.That(path.ValueForLog(true), Is.EqualTo("[redacted]"));
                Assert.That(nonDefaults, Has.None.Matches<NonDefaultConfigValue>(static x => x.Category == "KeyStore" && x.Name == nameof(IKeyStoreConfig.PasswordFiles)));
            }
        }

        [Test]
        public void Additional_rpc_urls_remain_available_when_masking_is_disabled()
        {
            const string url = "http://127.0.0.1:8546|http|eth";
            IConfigProvider provider = Provider(("JsonRpc.AdditionalRpcUrls", $"[\"{url}\"]"));
            NonDefaultConfigValue value = provider.GetNonDefaultValues()
                .Single(static x => x.Category == "JsonRpc" && x.Name == nameof(IJsonRpcConfig.AdditionalRpcUrls));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(value.ValueForLog(false), Does.Contain(url));
                Assert.That(value.ValueForLog(true), Is.EqualTo("[redacted]"));
                Assert.That(provider.GetRawValue("JsonRpc", nameof(IJsonRpcConfig.AdditionalRpcUrls)), Is.Not.Null);
            }
        }

        [TestCase("JsonRpc")]
        [TestCase("JsonRpcConfig")]
        [TestCase("jsonrpc")]
        public void Raw_value_is_returned_for_short_and_full_category_names(string category)
        {
            IEnvironment environment = Substitute.For<IEnvironment>();
            environment.GetEnvironmentVariable(Arg.Any<string>()).Returns((string?)null);
            environment.GetEnvironmentVariable("NETHERMIND_JSONRPCCONFIG_HOST").Returns("10.0.0.1");

            ConfigProvider provider = new();
            provider.AddSource(new ArgsConfigSource(new Dictionary<string, string> { ["JsonRpc.Port"] = "1234" }));
            provider.AddSource(new EnvConfigSource(environment));
            provider.Initialize();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(provider.GetRawValue(category, nameof(IJsonRpcConfig.Port)), Is.EqualTo("1234"));
                Assert.That(provider.GetRawValue(category, nameof(IJsonRpcConfig.Host)), Is.EqualTo("10.0.0.1"));
                Assert.That(provider.GetRawValue(category, nameof(IJsonRpcConfig.Timeout)), Is.EqualTo(new JsonRpcConfig().Timeout));
            }
        }

        [TestCase("UnsuffixedCategory", "default", 1)]
        [TestCase("UnsuffixedCategoryConfig", "default", 1)]
        [TestCase("unsuffixedcategory", "default", 1)]
        [TestCase("UnsuffixedCategory", "env", "71")]
        [TestCase("UnsuffixedCategoryConfig", "env", "71")]
        [TestCase("UnsuffixedCategory", "args", "72")]
        [TestCase("UnsuffixedCategoryConfig", "args", "72")]
        public void Raw_value_is_returned_for_category_without_config_suffix(string category, string source, object expected)
        {
            IEnvironment environment = Substitute.For<IEnvironment>();
            environment.GetEnvironmentVariable(Arg.Any<string>()).Returns((string?)null);
            ConfigProvider provider = new();
            if (source == "env") environment.GetEnvironmentVariable("NETHERMIND_UNSUFFIXEDCATEGORY_VALUE").Returns("71");
            if (source == "args") provider.AddSource(new ArgsConfigSource(new Dictionary<string, string> { ["UnsuffixedCategory.Value"] = "72" }));
            provider.AddSource(new EnvConfigSource(environment));
            provider.Initialize();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(provider.GetConfig<IUnsuffixedCategory>().Value.ToString(), Is.EqualTo(expected.ToString()));
                Assert.That(provider.GetRawValue(category, nameof(IUnsuffixedCategory.Value)), Is.EqualTo(expected));
            }
        }

        [TestCase("AliasCollision", 3)]
        [TestCase("aliascollision", 3)]
        [TestCase("AliasCollisionConfig", "40")]
        public void Raw_value_prefers_exact_category_over_short_alias(string category, object expected)
        {
            IEnvironment environment = Substitute.For<IEnvironment>();
            environment.GetEnvironmentVariable(Arg.Any<string>()).Returns((string?)null);
            environment.GetEnvironmentVariable("NETHERMIND_ALIASCOLLISIONCONFIG_VALUE").Returns("40");
            ConfigProvider provider = new();
            provider.AddSource(new EnvConfigSource(environment));
            provider.Initialize();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(provider.GetConfig<IAliasCollision>().Value, Is.EqualTo(3));
                Assert.That(provider.GetConfig<IAliasCollisionConfig>().Value, Is.EqualTo(40));
                Assert.That(provider.GetRawValue(category, nameof(IAliasCollision.Value)), Is.EqualTo(expected));
            }
        }

        [TestCase("AliasCollision", null)]
        [TestCase("AliasCollisionConfig", 5)]
        public void Raw_value_is_not_taken_from_short_alias_when_exact_category_lacks_the_item(string category, object? expected)
        {
            ConfigProvider provider = new();
            provider.Initialize();

            Assert.That(provider.GetRawValue(category, nameof(IAliasCollisionConfig.Other)), Is.EqualTo(expected));
        }

        [TestCase("UncategorizedAlias")]
        [TestCase("uncategorizedalias")]
        [TestCase("UncategorizedAliasConfig")]
        public void Raw_value_short_alias_is_not_shadowed_by_uncategorized_config(string category)
        {
            ConfigProvider provider = new();
            provider.Initialize();

            Assert.That(provider.GetRawValue(category, nameof(IUncategorizedAliasConfig.Value)), Is.EqualTo(6));
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

        [TestCase("KeyStore", "TestNodeKey", ExpectedResult = null)]
        [TestCase("KeyStoreConfig", "TestNodeKey", ExpectedResult = null)]
        [TestCase("KEYSTORECONFIG", "testnodekey", ExpectedResult = null)]
        [TestCase("", "KeyStore.TestNodeKey", ExpectedResult = null)] // args source key when the category is empty
        [TestCase("", "KEYSTORECONFIG_TESTNODEKEY", ExpectedResult = null)] // env source variable when the category is empty
        [TestCase("", "KeyStore.Cipher", ExpectedResult = null)] // not a config property, even though the args source has the key
        [TestCase("KeyStore", "Cipher", ExpectedResult = "aes-128-ctr")]
        [TestCase("", "Config", ExpectedResult = "custom.json")]
        public object? Raw_value_is_returned_only_for_non_sensitive_config_items(string category, string name)
        {
            IEnvironment environment = Substitute.For<IEnvironment>();
            environment.GetEnvironmentVariable("NETHERMIND_KEYSTORECONFIG_TESTNODEKEY").Returns("hunter2");

            ConfigProvider provider = new();
            provider.AddSource(new ArgsConfigSource(new Dictionary<string, string>
            {
                ["KeyStore.TestNodeKey"] = "hunter2",
                ["KeyStore.Cipher"] = "aes-128-ctr",
                ["Config"] = "custom.json"
            }));
            provider.AddSource(new EnvConfigSource(environment));
            provider.Initialize();

            return provider.GetRawValue(category, name);
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

    public interface IUnsuffixedCategory : IConfig
    {
        [ConfigItem(Description = "Test item of a category whose name has no Config suffix.", DefaultValue = "1")]
        int Value { get; set; }
    }

    public class UnsuffixedCategory : IUnsuffixedCategory
    {
        public int Value { get; set; } = 1;
    }

    public interface IAliasCollision : IConfig
    {
        [ConfigItem(Description = "Test item of a category whose name is the short alias of another category.", DefaultValue = "3")]
        int Value { get; set; }
    }

    public class AliasCollision : IAliasCollision
    {
        public int Value { get; set; } = 3;
    }

    public interface IAliasCollisionConfig : IConfig
    {
        [ConfigItem(Description = "Test item of a category whose short alias is another category's name.", DefaultValue = "4")]
        int Value { get; set; }

        [ConfigItem(Description = "Test item only the suffixed category declares.", DefaultValue = "5")]
        int Other { get; set; }
    }

    public class AliasCollisionConfig : IAliasCollisionConfig
    {
        public int Value { get; set; } = 4;
        public int Other { get; set; } = 5;
    }

    public interface IUncategorizedAlias : INoCategoryConfig
    {
    }

    public class UncategorizedAlias : IUncategorizedAlias
    {
        public string? Config { get; set; }
        public string? CliSwitchLocal { get; set; }
    }

    public interface IUncategorizedAliasConfig : IConfig
    {
        [ConfigItem(Description = "Test item of a category whose short alias is the name of a config without a category.", DefaultValue = "6")]
        int Value { get; set; }
    }

    public class UncategorizedAliasConfig : IUncategorizedAliasConfig
    {
        public int Value { get; set; } = 6;
    }
}
