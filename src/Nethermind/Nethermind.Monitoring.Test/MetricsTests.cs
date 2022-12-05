// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Monitoring.Config;
using Nethermind.Monitoring.Metrics;
using NUnit.Framework;

namespace Nethermind.Monitoring.Test
{
    [TestFixture]
    public class MetricsTests
    {
        public static class TestMetrics
        {
            [System.ComponentModel.Description("A test description")]
            public static long OneTwoThree { get; set; }

            [System.ComponentModel.Description("Another test description.")]
            [DataMember(Name = "one_two_three")]
            public static long OneTwoThreeSpecial { get; set; }
        }

        [Test]
        public void Test_gauge_names()
        {
            MetricsConfig metricsConfig = new()
            {
                Enabled = true
            };
            MetricsController metricsController = new(metricsConfig);
            metricsController.RegisterMetrics(typeof(TestMetrics));
            var gauges = metricsController._gauges;
            var keyDefault = $"{nameof(TestMetrics)}.{nameof(TestMetrics.OneTwoThree)}";
            var keySpecial = $"{nameof(TestMetrics)}.{nameof(TestMetrics.OneTwoThreeSpecial)}";

            Assert.Contains(keyDefault, gauges.Keys);
            Assert.Contains(keySpecial, gauges.Keys);

            Assert.AreEqual(gauges[keyDefault].Name, "nethermind_one_two_three");
            Assert.AreEqual(gauges[keySpecial].Name, "one_two_three");
        }

        [Test]
        public void Register_and_update_metrics_should_not_throw_exception()
        {
            MetricsConfig metricsConfig = new()
            {
                Enabled = true
            };
            List<Type> knownMetricsTypes = new()
            {
                typeof(Nethermind.Mev.Metrics),
                typeof(Nethermind.TxPool.Metrics),
                typeof(Nethermind.Blockchain.Metrics),
                typeof(Nethermind.Consensus.AuRa.Metrics),
                typeof(Nethermind.Evm.Metrics),
                typeof(Nethermind.JsonRpc.Metrics),
                typeof(Nethermind.Db.Metrics),
                typeof(Nethermind.Network.Metrics),
                typeof(Init.Metrics),
                typeof(Nethermind.Synchronization.Metrics),
                typeof(Nethermind.Trie.Metrics),
                typeof(Nethermind.Trie.Pruning.Metrics),
            };
            MetricsController metricsController = new(metricsConfig);
            MonitoringService monitoringService = new(metricsController, metricsConfig, LimboLogs.Instance);
            List<Type> metrics = new TypeDiscovery().FindNethermindTypes(nameof(Metrics)).ToList();
            metrics.AddRange(knownMetricsTypes);

            Assert.DoesNotThrow(() =>
            {
                foreach (Type metric in metrics)
                {
                    monitoringService.RegisterMetrics(metric);
                }

                metricsController.UpdateMetrics(null);
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
                Assembly assembly = Assembly.LoadFile(dll);
                Type[] configs = assembly.GetExportedTypes().Where(t => t.Name == "Metrics").ToArray();

                foreach (Type metricsType in configs)
                {
                    PropertyInfo[] properties = metricsType.GetProperties(BindingFlags.Static | BindingFlags.Public);
                    foreach (PropertyInfo property in properties)
                    {
                        try
                        {
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
