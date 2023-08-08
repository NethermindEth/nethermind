// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Config.Test
{
    [Parallelizable(ParallelScope.All)]
    public abstract class ConfigFileTestsBase
    {
        private readonly IDictionary<string, TestConfigProvider> _cachedProviders = new ConcurrentDictionary<string, TestConfigProvider>();
        private readonly Dictionary<string, IEnumerable<string>> _configGroups = new();

        [OneTimeSetUp]
        public void Setup()
        {
            // by pre-caching configs we make the tests do lot less work

            IEnumerable<Type> configTypes = TypeDiscovery.FindNethermindTypes(typeof(IConfig)).Where(t => t.IsInterface).ToArray();

            Parallel.ForEach(Resolve("*"), configFile =>
            {
                TestConfigProvider configProvider = GetConfigProviderFromFile(configFile);
                foreach (Type configType in configTypes)
                {
                    configProvider.GetConfig(configType);
                }

                _cachedProviders.Add(configFile, configProvider);
            });
        }

        [ConfigFileGroup("*")]
        protected abstract IEnumerable<string> Configs { get; }

        [ConfigFileGroup("fast")]
        protected IEnumerable<string> FastSyncConfigs
            => Configs.Where(config => !config.Contains("_") && !config.Contains("spaceneth"));

        [ConfigFileGroup("archive")]
        protected IEnumerable<string> ArchiveConfigs
            => Configs.Where(config => config.Contains("_archive"));

        [ConfigFileGroup("poacore")]
        protected IEnumerable<string> PoaCoreConfigs
            => Configs.Where(config => config.Contains("poacore"));

        [ConfigFileGroup("volta")]
        protected IEnumerable<string> VoltaConfigs
            => Configs.Where(config => config.Contains("volta"));

        [ConfigFileGroup("energy")]
        protected IEnumerable<string> EnergyConfigs
            => Configs.Where(config => config.Contains("energy"));

        [ConfigFileGroup("gnosis")]
        protected IEnumerable<string> GnosisConfigs
            => Configs.Where(config => config.Contains("gnosis"));

        [ConfigFileGroup("sepolia")]
        protected IEnumerable<string> SepoliaConfigs
            => Configs.Where(config => config.Contains("sepolia"));

        [ConfigFileGroup("chiado")]
        protected IEnumerable<string> ChiadoConfigs
            => Configs.Where(config => config.Contains("chiado"));

        [ConfigFileGroup("goerli")]
        protected IEnumerable<string> GoerliConfigs
            => Configs.Where(config => config.Contains("goerli"));

        [ConfigFileGroup("rinkeby")]
        protected IEnumerable<string> RinkebyConfigs
            => Configs.Where(config => config.Contains("rinkeby"));

        [ConfigFileGroup("kovan")]
        protected IEnumerable<string> KovanConfigs
            => Configs.Where(config => config.Contains("kovan"));

        [ConfigFileGroup("spaceneth")]
        protected IEnumerable<string> SpacenethConfigs
            => Configs.Where(config => config.Contains("spaceneth"));

        [ConfigFileGroup("mainnet")]
        protected IEnumerable<string> MainnetConfigs
            => Configs.Where(config => config.Contains("mainnet"));

        [ConfigFileGroup("validators")]
        protected IEnumerable<string> ValidatorConfigs
            => Configs.Where(config => config.Contains("validator"));

        [ConfigFileGroup("aura")]
        protected IEnumerable<string> AuraConfigs
            => PoaCoreConfigs
                .Union(GnosisConfigs)
                .Union(ChiadoConfigs)
                .Union(VoltaConfigs)
                .Union(EnergyConfigs)
                .Union(KovanConfigs);

        [ConfigFileGroup("aura_non_validating")]
        protected IEnumerable<string> AuraNonValidatingConfigs
            => AuraConfigs.Where(c => !c.Contains("validator"));

        [ConfigFileGroup("clique")]
        protected IEnumerable<string> CliqueConfigs
            => RinkebyConfigs.Union(GoerliConfigs);

        protected IEnumerable<string> Resolve(string configWildcard)
        {
            Dictionary<string, IEnumerable<string>> groups = BuildConfigGroups();
            string[] configWildcards = configWildcard.Split(" ");

            List<IEnumerable<string>> toIntersect = new();
            foreach (string singleWildcard in configWildcards)
            {
                string singleWildcardBase = singleWildcard.Replace("^", string.Empty);
                IEnumerable<string> result = groups.TryGetValue(singleWildcardBase, out IEnumerable<string>? value) ? value : Enumerable.Repeat(singleWildcardBase, 1);

                if (singleWildcard.StartsWith("^"))
                {
                    result = Configs.Except(result);
                }

                toIntersect.Add(result);
            }

            IEnumerable<string> intersection = toIntersect.First();
            foreach (IEnumerable<string> next in toIntersect.Skip(1))
            {
                intersection = intersection.Intersect(next);
            }

            return intersection;
        }

        protected void Test<T, TProperty>(string configWildcard, Expression<Func<T, TProperty>> getter, TProperty expectedValue) where T : IConfig
        {
            Test(configWildcard, getter, (s, propertyValue) => propertyValue.Should().Be(expectedValue, s + ": " + typeof(T).Name + "." + getter.GetName()));
        }

        protected void Test<T, TProperty>(string configWildcard, Expression<Func<T, TProperty>> getter, Action<string, TProperty> expectedValue) where T : IConfig
        {
            foreach (TestConfigProvider configProvider in GetConfigProviders(configWildcard))
            {
                T config = configProvider.GetConfig<T>();
                expectedValue(configProvider.FileName, getter.Compile()(config));
            }
        }

        protected IEnumerable<TestConfigProvider> GetConfigProviders(string configWildcard)
        {
            foreach (string configFile in Resolve(configWildcard))
            {
                if (!_cachedProviders.TryGetValue(configFile, out TestConfigProvider? configProvider))
                {
                    configProvider = GetConfigProviderFromFile(configFile);
                }

                yield return configProvider;
            }
        }

        protected class TestConfigProvider : ConfigProvider
        {
            public string FileName { get; }

            public TestConfigProvider(string fileName)
            {
                FileName = fileName;
            }
        }

        private static TestConfigProvider GetConfigProviderFromFile(string configFile)
        {
            try
            {
                TestConfigProvider configProvider = new(configFile);
                string configPath = Path.Combine(TestContext.CurrentContext.TestDirectory, "configs", configFile);
                configProvider.AddSource(new JsonConfigSource(configPath));
                return configProvider;
            }
            catch (Exception e)
            {
                throw new ConfigurationErrorsException($"Cannot load config file {configFile}", e);
            }
        }

        private Dictionary<string, IEnumerable<string>> BuildConfigGroups()
        {
            lock (_configGroups)
            {
                if (_configGroups.Count == 0)
                {
                    PropertyInfo[] propertyInfos = GetType()
                        .GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                    foreach (PropertyInfo propertyInfo in propertyInfos)
                    {
                        ConfigFileGroup? groupAttribute = propertyInfo.GetCustomAttribute<ConfigFileGroup>();
                        if (groupAttribute is not null)
                        {
                            _configGroups.Add(groupAttribute.Name, (IEnumerable<string>)propertyInfo.GetValue(this)!);
                        }
                    }
                }

                return _configGroups;
            }
        }

        protected class ConfigFileGroup : Attribute
        {
            public ConfigFileGroup(string name)
            {
                Name = name;
            }

            public string Name { get; }
        }
    }
}
