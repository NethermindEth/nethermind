// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Serialization;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Metric;
using Nethermind.Logging;
using Nethermind.Monitoring.Config;
using Nethermind.Monitoring.Metrics;
using NUnit.Framework;

namespace Nethermind.Monitoring.Test;

public class MetricsTests
{
    public static class TestMetrics
    {
        [System.ComponentModel.Description("A test description")]
        public static long OneTwoThree { get; set; }

        [System.ComponentModel.Description("Another test description.")]
        [DataMember(Name = "one_two_three")]
        public static long OneTwoThreeSpecial { get; set; }

        [System.ComponentModel.Description("Another test description.")]
        [KeyIsLabel("somelabel")]
        public static ConcurrentDictionary<SomeEnum, long> WithLabelledDictionary { get; set; } = new();

        [KeyIsLabel("label1", "label2", "label3")]
        public static ConcurrentDictionary<CustomLabelType, long> WithCustomLabelType { get; set; } = new();

        public static IDictionary<string, long> OldDictionaryMetrics { get; set; } = new ConcurrentDictionary<string, long>();

        [System.ComponentModel.Description("summary metric")]
        [SummaryMetric]
        public static IMetricObserver SomeObservation { get; set; } = NoopMetricObserver.Instance;

        [System.ComponentModel.Description("Histograrm metric")]
        [ExponentialPowerHistogramMetric(Start = 1, Factor = 2, Count = 10)]
        public static IMetricObserver HistogramObservation { get; set; } = NoopMetricObserver.Instance;

        [System.ComponentModel.Description("A test description")]
        [DetailedMetric]
        public static long DetailedMetric { get; set; }
    }

    public enum SomeEnum
    {
        Option1,
        Option2,
    }

    public struct CustomLabelType(int num1, int num2, int num3) : IMetricLabels
    {
        public readonly string[] Labels => [num1.ToString(), num2.ToString(), num3.ToString()];
    }

    [Test]
    public void Test_update_correct_gauge()
    {
        MetricsConfig metricsConfig = new()
        {
            Enabled = true
        };
        MetricsController metricsController = new(metricsConfig);
        metricsController.RegisterMetrics(typeof(TestMetrics));

        TestMetrics.OneTwoThree = 123;
        TestMetrics.OneTwoThreeSpecial = 1234;
        TestMetrics.WithLabelledDictionary[SomeEnum.Option1] = 2;
        TestMetrics.WithLabelledDictionary[SomeEnum.Option2] = 3;
        TestMetrics.WithCustomLabelType[new CustomLabelType(1, 11, 111)] = 1111;
        TestMetrics.OldDictionaryMetrics["metrics0"] = 4;
        TestMetrics.OldDictionaryMetrics["metrics1"] = 5;
        metricsController.UpdateAllMetrics();

        var updater = metricsController._individualUpdater;
        var keyDefault = $"{nameof(TestMetrics)}.{nameof(TestMetrics.OneTwoThree)}";
        var keySpecial = $"{nameof(TestMetrics)}.{nameof(TestMetrics.OneTwoThreeSpecial)}";
        var keyDictionary = $"{nameof(TestMetrics)}.{nameof(TestMetrics.WithLabelledDictionary)}";
        var keyDictionary2 = $"{nameof(TestMetrics)}.{nameof(TestMetrics.WithCustomLabelType)}";
        var keyOldDictionary = $"{nameof(TestMetrics)}.{nameof(TestMetrics.OldDictionaryMetrics)}";
        var keyOldDictionary0 = $"{nameof(TestMetrics.OldDictionaryMetrics)}.metrics0";
        var keyOldDictionary1 = $"{nameof(TestMetrics.OldDictionaryMetrics)}.metrics1";
        var keySummary = $"{nameof(TestMetrics)}.{nameof(TestMetrics.SomeObservation)}";
        var keyHistogram = $"{nameof(TestMetrics)}.{nameof(TestMetrics.HistogramObservation)}";

        Assert.That(updater.Keys, Has.Member(keyDefault));
        Assert.That(updater.Keys, Has.Member(keySpecial));

        Assert.That((updater[keyDefault] as MetricsController.GaugeMetricUpdater).Gauge.Name, Is.EqualTo("nethermind_one_two_three"));
        Assert.That((updater[keySpecial] as MetricsController.GaugeMetricUpdater).Gauge.Name, Is.EqualTo("one_two_three"));
        Assert.That((updater[keyDictionary] as MetricsController.KeyIsLabelGaugeMetricUpdater).Gauge.Name, Is.EqualTo("nethermind_with_labelled_dictionary"));
        Assert.That((updater[keyOldDictionary] as MetricsController.GaugePerKeyMetricUpdater).Gauges[keyOldDictionary0].Name, Is.EqualTo("nethermind_metrics0"));
        Assert.That((updater[keyOldDictionary] as MetricsController.GaugePerKeyMetricUpdater).Gauges[keyOldDictionary1].Name, Is.EqualTo("nethermind_metrics1"));
        Assert.That(updater[keySummary], Is.TypeOf<MetricsController.SummaryMetricUpdater>());
        Assert.That(updater[keyHistogram], Is.TypeOf<MetricsController.HistogramMetricUpdater>());
        Assert.That(TestMetrics.SomeObservation, Is.TypeOf<MetricsController.SummaryMetricUpdater>());
        Assert.That(TestMetrics.HistogramObservation, Is.TypeOf<MetricsController.HistogramMetricUpdater>());

        Assert.That((updater[keyDefault] as MetricsController.GaugeMetricUpdater).Gauge.Value, Is.EqualTo(123));
        Assert.That((updater[keySpecial] as MetricsController.GaugeMetricUpdater).Gauge.Value, Is.EqualTo(1234));
        Assert.That((updater[keyDictionary] as MetricsController.KeyIsLabelGaugeMetricUpdater).Gauge.WithLabels(SomeEnum.Option1.ToString()).Value, Is.EqualTo(2));
        Assert.That((updater[keyDictionary] as MetricsController.KeyIsLabelGaugeMetricUpdater).Gauge.WithLabels(SomeEnum.Option2.ToString()).Value, Is.EqualTo(3));
        Assert.That((updater[keyDictionary2] as MetricsController.KeyIsLabelGaugeMetricUpdater).Gauge.WithLabels("1", "11", "111").Value, Is.EqualTo(1111));
        Assert.That((updater[keyOldDictionary] as MetricsController.GaugePerKeyMetricUpdater).Gauges[keyOldDictionary0].Value, Is.EqualTo(4));
        Assert.That((updater[keyOldDictionary] as MetricsController.GaugePerKeyMetricUpdater).Gauges[keyOldDictionary1].Value, Is.EqualTo(5));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Load_DetailedMetric(bool enableDetailedMetric)
    {
        MetricsConfig metricsConfig = new()
        {
            Enabled = true,
            EnableDetailedMetric = enableDetailedMetric
        };
        MetricsController metricsController = new(metricsConfig);
        metricsController.RegisterMetrics(typeof(TestMetrics));
        metricsController.UpdateAllMetrics();

        var updater = metricsController._individualUpdater;
        var metricName = "TestMetrics.DetailedMetric";
        Assert.That(updater.ContainsKey(metricName), Is.EqualTo(enableDetailedMetric));
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
            typeof(TxPool.Metrics),
            typeof(Blockchain.Metrics),
            typeof(Consensus.AuRa.Metrics),
            typeof(Evm.Metrics),
            typeof(JsonRpc.Metrics),
            typeof(Db.Metrics),
            typeof(Network.Metrics),
            typeof(Init.Metrics),
            typeof(Synchronization.Metrics),
            typeof(Trie.Metrics),
            typeof(Trie.Pruning.Metrics),
            typeof(Shutter.Metrics)
        };
        MetricsController metricsController = new(metricsConfig);
        MonitoringService monitoringService = new(metricsController, metricsConfig, LimboLogs.Instance);
        List<Type> metrics = TypeDiscovery.FindNethermindBasedTypes(nameof(Metrics)).ToList();
        metrics.AddRange(knownMetricsTypes);

        Assert.DoesNotThrow(() =>
        {
            foreach (Type metric in metrics)
            {
                metricsController.RegisterMetrics(metric);
            }

            metricsController.UpdateAllMetrics();
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
            Type[] configs = assembly.GetExportedTypes().Where(static t => t.Name == "Metrics").ToArray();

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
