// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Metric;
using Nethermind.Monitoring.Config;
using Nethermind.Monitoring.Metrics;
using NUnit.Framework;

namespace Nethermind.Monitoring.Test;

public class MetricsTests
{
    private const string TestNodeName = "enode://abcdef@127.0.0.1:30303";
    private const string TestInstance = "abcdef";
    private const string TestNetwork = "test-network";
    private const string TestSyncType = "Snap";

    // Registry static labels can only be set once per process and only before the first export.
    // Claim them here, deterministically, before any test runs so the exported label set is known.
    // PruningMode is intentionally left empty to assert that empty values are omitted from the export.
    [OneTimeSetUp]
    public void InitializeStaticLabelsDeterministically()
    {
        ProductInfo.Network = TestNetwork;
        ProductInfo.SyncType = TestSyncType;
        _ = new MetricsController(new MetricsConfig { Enabled = true, NodeName = TestNodeName });
    }

    public static class TestMetrics
    {
        [System.ComponentModel.Description("A test description")]
        public static long OneTwoThree { get; set; }

        [System.ComponentModel.Description("Another test description.")]
        [DataMember(Name = "one_two_three")]
        public static long OneTwoThreeSpecial { get; set; }

        [System.ComponentModel.Description("Another test description.")]
        [KeyIsLabel("some_label")]
        public static ConcurrentDictionary<SomeEnum, long> WithLabelledDictionary { get; set; } = new();

        [KeyIsLabel("label1", "label2", "label3")]
        public static ConcurrentDictionary<CustomLabelType, long> WithCustomLabelType { get; set; } = new();

        public static IDictionary<string, long> OldDictionaryMetrics { get; set; } = new ConcurrentDictionary<string, long>();

        [System.ComponentModel.Description("summary metric")]
        [SummaryMetric]
        public static IMetricObserver SomeObservation { get; set; } = NoopMetricObserver.Instance;

        [System.ComponentModel.Description("Histogram metric")]
        [ExponentialPowerHistogramMetric(Start = 1, Factor = 2, Count = 10)]
        public static IMetricObserver HistogramObservation { get; set; } = NoopMetricObserver.Instance;

        [System.ComponentModel.Description("Explicit histogram metric")]
        [HistogramMetric(Buckets = [1, 2, 5])]
        public static IMetricObserver ExplicitHistogramObservation { get; set; } = NoopMetricObserver.Instance;

        [System.ComponentModel.Description("A test description")]
        [DetailedMetric]
        public static long DetailedMetric { get; set; }

        [DetailedMetricOnFlag]
        public static bool DetailedMetricsEnabled { get; set; }
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

    private sealed class RpcMetricLabels(string method, string status) : IMetricLabels
    {
        public string[] Labels { get; } = [method, status];
    }

    [Test]
    public async Task Test_update_correct_gauge()
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

        Dictionary<string, MetricsController.IMetricUpdater> updater = metricsController._individualUpdater;
        string keyDefault = $"{nameof(TestMetrics)}.{nameof(TestMetrics.OneTwoThree)}";
        string keySpecial = $"{nameof(TestMetrics)}.{nameof(TestMetrics.OneTwoThreeSpecial)}";
        string keyDictionary = $"{nameof(TestMetrics)}.{nameof(TestMetrics.WithLabelledDictionary)}";
        string keyDictionary2 = $"{nameof(TestMetrics)}.{nameof(TestMetrics.WithCustomLabelType)}";
        string keyOldDictionary = $"{nameof(TestMetrics)}.{nameof(TestMetrics.OldDictionaryMetrics)}";
        string keyOldDictionary0 = $"{nameof(TestMetrics.OldDictionaryMetrics)}.metrics0";
        string keyOldDictionary1 = $"{nameof(TestMetrics.OldDictionaryMetrics)}.metrics1";
        string keySummary = $"{nameof(TestMetrics)}.{nameof(TestMetrics.SomeObservation)}";
        string keyHistogram = $"{nameof(TestMetrics)}.{nameof(TestMetrics.HistogramObservation)}";
        string keyExplicitHistogram = $"{nameof(TestMetrics)}.{nameof(TestMetrics.ExplicitHistogramObservation)}";

        using (Assert.EnterMultipleScope())
        {
            Assert.That(updater.Keys, Has.Member(keyDefault));
            Assert.That(updater.Keys, Has.Member(keySpecial));

            Assert.That((updater[keyDefault] as MetricsController.GaugeMetricUpdater).Gauge.Name, Is.EqualTo("nethermind_one_two_three"));
            Assert.That((updater[keySpecial] as MetricsController.GaugeMetricUpdater).Gauge.Name, Is.EqualTo("one_two_three"));
            Assert.That((updater[keyDictionary] as MetricsController.KeyIsLabelGaugeMetricUpdater).Gauge.Name, Is.EqualTo("nethermind_with_labelled_dictionary"));
            Assert.That((updater[keyOldDictionary] as MetricsController.GaugePerKeyMetricUpdater).Gauges[keyOldDictionary0].Name, Is.EqualTo("nethermind_metrics0"));
            Assert.That((updater[keyOldDictionary] as MetricsController.GaugePerKeyMetricUpdater).Gauges[keyOldDictionary1].Name, Is.EqualTo("nethermind_metrics1"));
            Assert.That(updater[keySummary], Is.TypeOf<MetricsController.SummaryMetricUpdater>());
            Assert.That(updater[keyHistogram], Is.TypeOf<MetricsController.HistogramMetricUpdater>());
            Assert.That(updater[keyExplicitHistogram], Is.TypeOf<MetricsController.HistogramMetricUpdater>());
            Assert.That(TestMetrics.SomeObservation, Is.TypeOf<MetricsController.SummaryMetricUpdater>());
            Assert.That(TestMetrics.HistogramObservation, Is.TypeOf<MetricsController.HistogramMetricUpdater>());
            Assert.That(TestMetrics.ExplicitHistogramObservation, Is.TypeOf<MetricsController.HistogramMetricUpdater>());

            Assert.That((updater[keyDefault] as MetricsController.GaugeMetricUpdater).Gauge.Value, Is.EqualTo(123));
            Assert.That((updater[keySpecial] as MetricsController.GaugeMetricUpdater).Gauge.Value, Is.EqualTo(1234));
            Assert.That((updater[keyDictionary] as MetricsController.KeyIsLabelGaugeMetricUpdater).Gauge.WithLabels(SomeEnum.Option1.ToString()).Value, Is.EqualTo(2));
            Assert.That((updater[keyDictionary] as MetricsController.KeyIsLabelGaugeMetricUpdater).Gauge.WithLabels(SomeEnum.Option2.ToString()).Value, Is.EqualTo(3));
            Assert.That((updater[keyDictionary2] as MetricsController.KeyIsLabelGaugeMetricUpdater).Gauge.WithLabels("1", "11", "111").Value, Is.EqualTo(1111));
            Assert.That((updater[keyOldDictionary] as MetricsController.GaugePerKeyMetricUpdater).Gauges[keyOldDictionary0].Value, Is.EqualTo(4));
            Assert.That((updater[keyOldDictionary] as MetricsController.GaugePerKeyMetricUpdater).Gauges[keyOldDictionary1].Value, Is.EqualTo(5));
        }

        TestMetrics.ExplicitHistogramObservation.Observe(2);
        using MemoryStream stream = new();
        await Prometheus.Metrics.DefaultRegistry.CollectAndExportAsTextAsync(stream);
        string scrape = Encoding.UTF8.GetString(stream.ToArray());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(scrape, Does.Contain("nethermind_explicit_histogram_observation_bucket"));
            Assert.That(scrape, Does.Contain("nethermind_explicit_histogram_observation_sum"));
            Assert.That(scrape, Does.Contain("nethermind_explicit_histogram_observation_count"));
            Assert.That(scrape, Does.Not.Contain("nethermind_explicit_histogram_observation{quantile="));
        }
    }

    [Test]
    public async Task Json_rpc_duration_histogram_uses_distinct_metric_name()
    {
        MetricsConfig metricsConfig = new()
        {
            Enabled = true
        };
        MetricsController metricsController = new(metricsConfig);
        metricsController.RegisterMetrics(typeof(JsonRpc.Metrics));

        JsonRpc.Metrics.JsonRpcCallDurationMicros.Observe(100, new RpcMetricLabels("eth_call", "success"));

        using MemoryStream stream = new();
        await Prometheus.Metrics.DefaultRegistry.CollectAndExportAsTextAsync(stream);
        string scrape = Encoding.UTF8.GetString(stream.ToArray());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(scrape, Does.Contain("nethermind_json_rpc_call_duration_micros_bucket"));
            Assert.That(scrape, Does.Contain("nethermind_json_rpc_call_duration_micros_sum"));
            Assert.That(scrape, Does.Contain("nethermind_json_rpc_call_duration_micros_count"));
            Assert.That(scrape, Does.Not.Contain("nethermind_json_rpc_call_latency_micros"));
        }
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

        Dictionary<string, MetricsController.IMetricUpdater> updater = metricsController._individualUpdater;
        string metricName = "TestMetrics.DetailedMetric";
        using (Assert.EnterMultipleScope())
        {
            Assert.That(updater.ContainsKey(metricName), Is.EqualTo(enableDetailedMetric));
            Assert.That(TestMetrics.DetailedMetricsEnabled, Is.EqualTo(enableDetailedMetric));
        }
    }

    [Test]
    public void Register_and_update_metrics_should_not_throw_exception()
    {
        MetricsConfig metricsConfig = new()
        {
            Enabled = true
        };
        List<Type> knownMetricsTypes =
        [
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
            typeof(Shutter.Metrics),
            typeof(History.Metrics)
        ];
        MetricsController metricsController = new(metricsConfig);
        List<Type> metrics = [.. TypeDiscovery.FindNethermindBasedTypes(nameof(Metrics))];
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
    public void UpdateAllMetrics_does_not_throw_when_registration_is_concurrent()
    {
        MetricsConfig metricsConfig = new() { Enabled = true };
        MetricsController metricsController = new(metricsConfig);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(2));
        CancellationToken ct = cts.Token;

        // Continuously call UpdateAllMetrics on one thread while registering metrics on another
        Task updater = Task.Run(() =>
        {
            while (!ct.IsCancellationRequested)
            {
                metricsController.UpdateAllMetrics();
            }
        });

        Task registrar = Task.Run(() =>
        {
            Type[] types =
            [
                typeof(TestMetrics),
                typeof(Blockchain.Metrics),
                typeof(Evm.Metrics),
                typeof(Network.Metrics),
                typeof(Db.Metrics),
            ];

            for (int i = 0; !ct.IsCancellationRequested; i++)
            {
                metricsController.RegisterMetrics(types[i % types.Length]);
                metricsController.AddMetricsUpdateAction(() => { });
            }
        });

        Assert.DoesNotThrowAsync(() => Task.WhenAll(updater, registrar));
    }

    [Test]
    public async Task Static_labels_are_exported_for_nethermind_and_default_runtime_metrics()
    {
        MetricsController metricsController = new(new MetricsConfig { Enabled = true, NodeName = TestNodeName });
        metricsController.RegisterMetrics(typeof(TestMetrics));
        TestMetrics.OneTwoThree = 1;
        metricsController.UpdateAllMetrics();

        using MemoryStream stream = new();
        await Prometheus.Metrics.DefaultRegistry.CollectAndExportAsTextAsync(stream);
        string scrape = Encoding.UTF8.GetString(stream.ToArray());
        string[] lines = scrape.Split('\n');

        string nethermindSample = lines.First(l => l.StartsWith("nethermind_one_two_three{", StringComparison.Ordinal));
        bool runtimeMetricCarriesInstance = lines.Any(l =>
            (l.StartsWith("dotnet_", StringComparison.Ordinal) || l.StartsWith("process_", StringComparison.Ordinal))
            && l.Contains($"Instance=\"{TestInstance}\"", StringComparison.Ordinal));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(nethermindSample, Does.Contain($"Instance=\"{TestInstance}\""));
            Assert.That(nethermindSample, Does.Contain($"Network=\"{TestNetwork}\""));
            Assert.That(nethermindSample, Does.Contain($"SyncType=\"{TestSyncType}\""));
            Assert.That(nethermindSample, Does.Contain("nethermind_group=\"nethermind\""));
            Assert.That(runtimeMetricCarriesInstance, Is.True, "Default dotnet_/process_ metrics must carry the same static labels as nethermind_ metrics.");
            Assert.That(scrape, Does.Not.Contain("PruningMode="), "Empty static label values must be omitted from the export.");
        }
    }

    [Test]
    public void Common_static_tags_skip_empty_values()
    {
        Dictionary<string, string> tags = MetricsController.BuildCommonStaticTags(new MetricsConfig { NodeName = string.Empty });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tags, Does.Not.ContainKey(nameof(ProductInfo.PruningMode)));
            Assert.That(tags, Does.Not.ContainKey(nameof(ProductInfo.Instance)));
            Assert.That(tags.Values, Has.None.Empty, "No static label may be exported with an empty value.");
            Assert.That(tags, Does.ContainKey(nameof(ProductInfo.Version)));
            Assert.That(tags, Does.ContainKey(nameof(ProductInfo.Runtime)));
        }
    }

    [TestCase(TestNodeName, TestInstance)]
    [TestCase("MyNode", "MyNode")]
    public void Pusher_instance_grouping_equals_exported_instance_label(string nodeName, string expectedInstance)
    {
        MetricsConfig config = new() { NodeName = nodeName };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(MonitoringOptions.FromConfig(config).Instance, Is.EqualTo(expectedInstance));
            Assert.That(MetricsController.BuildCommonStaticTags(config)[nameof(ProductInfo.Instance)], Is.EqualTo(expectedInstance));
        }
    }

    [TestCase("http://host:9091/metrics/nethermind-mainnet", "custom", "nethermind")]
    [TestCase("http://host:9091/metrics", "custom", "custom")]
    [TestCase(null, "custom", "custom")]
    public void Group_is_derived_consistently_for_push_and_scrape(string pushGatewayUrl, string monitoringGroup, string expectedGroup)
    {
        MetricsConfig config = new() { PushGatewayUrl = pushGatewayUrl, MonitoringGroup = monitoringGroup };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(MonitoringOptions.FromConfig(config).Group, Is.EqualTo(expectedGroup));
            Assert.That(MetricsController.BuildCommonStaticTags(config)["nethermind_group"], Is.EqualTo(expectedGroup));
        }
    }

    [Test]
    public void All_config_items_have_descriptions() => ValidateMetricsDescriptions();

    public static void ValidateMetricsDescriptions() => ForEachProperty(CheckDescribedOrHidden);

    private static void CheckDescribedOrHidden(PropertyInfo property)
    {
        System.ComponentModel.DescriptionAttribute attribute = property.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>();
        Assert.That(attribute, Is.Not.Null);
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
                    if (property.GetCustomAttribute<DetailedMetricOnFlagAttribute>() is not null) continue;
                    try
                    {
                        verifier(property);
                    }
                    catch (AssertionException e)
                    {
                        throw new AssertionException($"{property.Name}: {e.Message}", e);
                    }
                }
            }
        }
    }
}
