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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Monitoring.Config;
using Nethermind.Monitoring.Metrics;
using Nethermind.Runner;
using NUnit.Framework;

namespace Nethermind.Monitoring.Test
{
    [TestFixture]
    public class MetricsTests
    {
        [Test]
        public void Register_and_update_metrics_should_not_throw_exception()
        {
            MetricsConfig metricsConfig = new()
            {
                Enabled = true
            };
            List<Type> knownMetricsTypes = new()
            {
                typeof(Nethermind.Mev.Metrics), typeof(Nethermind.TxPool.Metrics), typeof(Nethermind.Blockchain.Metrics),
                typeof(Nethermind.Consensus.AuRa.Metrics), typeof(Nethermind.Evm.Metrics), typeof(Nethermind.JsonRpc.Metrics),
                typeof(Nethermind.Db.Metrics), typeof(Nethermind.Network.Metrics), typeof(Init.Metrics), 
                typeof(Nethermind.Synchronization.Metrics), typeof(Nethermind.Trie.Metrics), typeof(Nethermind.Trie.Pruning.Metrics), 
            };
            MetricsUpdater metricsUpdater = new(metricsConfig);
            MonitoringService monitoringService = new(metricsUpdater, metricsConfig, LimboLogs.Instance);
            List<Type> metrics = new TypeDiscovery().FindNethermindTypes(nameof(Metrics)).ToList();
            metrics.AddRange(knownMetricsTypes);

            Assert.DoesNotThrow(() =>
            {
                foreach (Type metric in metrics)
                {
                    monitoringService.RegisterMetrics(metric);
                }

                metricsUpdater.UpdateMetrics(null);
            });
        }
        
        [Test]
        public void All_config_items_have_descriptions()
        {
            ValidateMetricsDescriptions();
        }

        public static void ValidateMetricsDescriptions()
        {
            ForEachProperty(CheckDescribedOrHidden);
        }

        private static void CheckDescribedOrHidden(PropertyInfo property)
        {
            System.ComponentModel.DescriptionAttribute attribute = property.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>();
            attribute.Should().NotBeNull();
        }

        private static void ForEachProperty(Action<PropertyInfo> verifier)
        {
            string[] dlls = Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, "Nethermind.*.dll");
            foreach (string dll in dlls)
            {
                TestContext.WriteLine($"Verify {nameof(MetricsTests)} on {Path.GetFileName(dll)}");
                Assembly assembly = Assembly.LoadFile(dll);
                Type[] configs = assembly.GetExportedTypes().Where(t => t.Name == "Metrics").ToArray();

                foreach (Type metricsType in configs)
                {
                    TestContext.WriteLine($"  Verifying type {metricsType.FullName}");
                    PropertyInfo[] properties = metricsType.GetProperties(BindingFlags.Static | BindingFlags.Public);
                    foreach (PropertyInfo property in properties)
                    {
                        try
                        {
                            TestContext.WriteLine($"    Verifying property {property.Name}");
                            verifier(property);
                        }
                        catch (Exception e)
                        {
                            throw new Exception(property.Name, e);
                        }
                    }
                }
            }
        }
    }
}
