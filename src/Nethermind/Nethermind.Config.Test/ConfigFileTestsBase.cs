//  Copyright (c) 2021 Demerzel Solutions Limited
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
// 

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        private readonly IDictionary<string, ConfigProvider> _cachedProviders = new ConcurrentDictionary<string, ConfigProvider>();
        private readonly Dictionary<string, IEnumerable<string>> _configGroups = new Dictionary<string, IEnumerable<string>>();
        
        [OneTimeSetUp]
        public void Setup()
        {
            // by pre-caching configs we make the tests do lot less work

            IEnumerable<Type> configTypes = new TypeDiscovery().FindNethermindTypes(typeof(IConfig)).Where(t => t.IsInterface).ToArray();

            Parallel.ForEach(Resolve("*"), configFile =>
            {
                ConfigProvider configProvider = GetConfigProviderFromFile(configFile);
                foreach (Type configType in configTypes)
                {
                    configProvider.GetConfig(configType);
                }

                _cachedProviders.Add(configFile, configProvider);
            });
        }
        
        [ConfigFileGroup("*")]
        protected abstract IEnumerable<string> Configs { get; }
        
        [ConfigFileGroup("beam")]
        protected IEnumerable<string> BeamConfigs
            => Configs.Where(config => config.Contains("_beam"));

        [ConfigFileGroup("fast")]
        protected IEnumerable<string> FastSyncConfigs
            => Configs.Where(config => !config.Contains("_") && !config.Contains("spaceneth"));

        [ConfigFileGroup("archive")]
        protected IEnumerable<string> ArchiveConfigs
            => Configs.Where(config => config.Contains("_archive"));

        [ConfigFileGroup("ropsten")]
        protected IEnumerable<string> RopstenConfigs
            => Configs.Where(config => config.Contains("ropsten"));

        [ConfigFileGroup("poacore")]
        protected IEnumerable<string> PoaCoreConfigs
            => Configs.Where(config => config.Contains("poacore"));

        [ConfigFileGroup("sokol")]
        protected IEnumerable<string> SokolConfigs
            => Configs.Where(config => config.Contains("sokol"));

        [ConfigFileGroup("volta")]
        protected IEnumerable<string> VoltaConfigs
            => Configs.Where(config => config.Contains("volta"));

        [ConfigFileGroup("energy")]
        protected IEnumerable<string> EnergyConfigs
            => Configs.Where(config => config.Contains("energy"));

        [ConfigFileGroup("xdai")]
        protected IEnumerable<string> XDaiConfigs
            => Configs.Where(config => config.Contains("xdai"));

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

        [ConfigFileGroup("baseline")]
        protected IEnumerable<string> BaselineConfigs
            => Configs.Where(config => config.Contains("baseline"));

        [ConfigFileGroup("mainnet")]
        protected IEnumerable<string> MainnetConfigs
            => Configs.Where(config => config.Contains("mainnet"));

        [ConfigFileGroup("validators")]
        protected IEnumerable<string> ValidatorConfigs
            => Configs.Where(config => config.Contains("validator"));

        [ConfigFileGroup("ndm")]
        protected IEnumerable<string> NdmConfigs
            => Configs.Where(config => config.Contains("ndm"));

        [ConfigFileGroup("aura")]
        protected IEnumerable<string> AuraConfigs
            => PoaCoreConfigs
                .Union(SokolConfigs)
                .Union(XDaiConfigs)
                .Union(VoltaConfigs)
                .Union(EnergyConfigs)
                .Union(KovanConfigs);

        [ConfigFileGroup("aura_non_validating")]
        protected IEnumerable<string> AuraNonValidatingConfigs
            => AuraConfigs.Where(c => !c.Contains("validator"));

        [ConfigFileGroup("clique")]
        protected IEnumerable<string> CliqueConfigs
            => RinkebyConfigs.Union(GoerliConfigs);

        [ConfigFileGroup("ethhash")]
        protected IEnumerable<string> EthashConfigs
            => MainnetConfigs.Union(RopstenConfigs);

        private IEnumerable<string> Resolve(string configWildcard)
        {
            Dictionary<string, IEnumerable<string>> groups = BuildConfigGroups();
            string[] configWildcards = configWildcard.Split(" ");

            List<IEnumerable<string>> toIntersect = new List<IEnumerable<string>>();
            foreach (string singleWildcard in configWildcards)
            {
                string singleWildcardBase = singleWildcard.Replace("^", "");
                var result = groups.ContainsKey(singleWildcardBase)
                    ? groups[singleWildcardBase]
                    : Enumerable.Repeat(singleWildcardBase, 1);

                if (singleWildcard.StartsWith("^"))
                {
                    result = Configs.Except(result);
                }

                toIntersect.Add(result);
            }

            var intersection = toIntersect.First();
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
            foreach (string configFile in Resolve(configWildcard))
            {
                Console.WriteLine("Testing " + configFile);
                if (!_cachedProviders.TryGetValue(configFile, out ConfigProvider configProvider))
                {
                    configProvider = GetConfigProviderFromFile(configFile);
                }

                T config = configProvider.GetConfig<T>();
                expectedValue(configFile, getter.Compile()(config));
            }
        }
        
        private static ConfigProvider GetConfigProviderFromFile(string configFile)
        {
            ConfigProvider configProvider = new ConfigProvider();
            var configPath = Path.Combine(TestContext.CurrentContext.TestDirectory, "configs", configFile);
            configProvider.AddSource(new JsonConfigSource(configPath));
            return configProvider;
        }
        
        private Dictionary<string, IEnumerable<string>> BuildConfigGroups()
        {
            if (_configGroups.Count == 0)
            {
                lock (_configGroups)
                {
                    if (_configGroups.Count == 0)
                    {
                        PropertyInfo[] propertyInfos = GetType().GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                        foreach (PropertyInfo propertyInfo in propertyInfos)
                        {
                            ConfigFileGroup groupAttribute = propertyInfo.GetCustomAttribute<ConfigFileGroup>();
                            if (groupAttribute != null)
                            {
                                _configGroups.Add(groupAttribute.Name, (IEnumerable<string>)propertyInfo.GetValue(this));
                            }
                        }
                    }
                }
            }
            
            return _configGroups;
        }
        
        protected class ConfigFileGroup : Attribute
        {
            public ConfigFileGroup(string name)
            {
                Name = name;
            }

            public string Name { get; private set; }
        }
    }
}
