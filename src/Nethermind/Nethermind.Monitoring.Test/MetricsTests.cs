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

    [Test]
    [NonParallelizable]
    public async Task Tx_pool_retry_metrics_have_stable_prometheus_contract()
    {
        const string client = "metrics-contract-test";
        MetricsConfig metricsConfig = new()
        {
            Enabled = true
        };
        MetricsController metricsController = new(metricsConfig);
        metricsController.RegisterMetrics(typeof(TxPool.Metrics));

        TxPool.Metrics.AddNewPooledTransactionsAnnouncedByClient(client, 7);
        TxPool.Metrics.AddNewPooledTransactionsRequestedByClient(client, 5, TxPool.PooledTransactionRequestReason.Retry);
        TxPool.Metrics.AddPendingTransactionRetryHandlersSkippedOnReceived(3);
        metricsController.UpdateAllMetrics();

        using MemoryStream stream = new();
        await Prometheus.Metrics.DefaultRegistry.CollectAndExportAsTextAsync(stream);
        string scrape = Encoding.UTF8.GetString(stream.ToArray());
        string announced = GetMetricLine(scrape, "nethermind_new_pooled_transactions_announced_by_client", client);
        string requested = GetMetricLine(scrape, "nethermind_new_pooled_transactions_requested_by_client_and_reason", client);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scrape, Does.Contain("# TYPE nethermind_pending_transaction_retry_handlers_skipped_on_received gauge"));
            Assert.That(announced, Does.Contain($"client=\"{client}\""));
            Assert.That(announced, Does.EndWith(" 7"));
            Assert.That(requested, Does.Contain($"client=\"{client}\""));
            Assert.That(requested, Does.Contain("reason=\"retry\""));
            Assert.That(requested, Does.EndWith(" 5"));
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
    public void All_config_items_have_descriptions() => ValidateMetricsDescriptions();

    private static string GetMetricLine(string scrape, string metricName, string client) => scrape
        .Split('\n')
        .Single(line => line.StartsWith(metricName, StringComparison.Ordinal) && line.Contains($"client=\"{client}\"", StringComparison.Ordinal));

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
