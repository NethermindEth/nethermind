// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Core.Metric;
using Nethermind.Monitoring.Config;
using Prometheus;

[assembly: InternalsVisibleTo("Nethermind.Monitoring.Test")]

namespace Nethermind.Monitoring.Metrics
{
    public partial class MetricsController : IMetricsController
    {
        private readonly int _intervalSeconds;
        private Timer _timer = null!;
        private static bool _staticLabelsInitialized = false;

        private readonly Dictionary<Type, IMetricUpdater[]> _metricUpdaters = new();
        private readonly HashSet<Type> _metricTypes = new();

        // Largely for testing reason
        internal readonly Dictionary<string, IMetricUpdater> _individualUpdater = new();

        private readonly bool _useCounters;
        private readonly bool _enableDetailedMetric;

        private readonly List<Action> _callbacks = new();

        public interface IMetricUpdater
        {
            void Update();
        }

        public class GaugeMetricUpdater(Gauge gauge, Func<double> accessor, params string[] labels) : IMetricUpdater
        {
            public void Update() => SetGauge(accessor(), gauge, labels);
            public Gauge Gauge => gauge;
        }

        public class GaugePerKeyMetricUpdater(IDictionary dict, string dictionaryName) : IMetricUpdater
        {
            private readonly Dictionary<string, Gauge> _gauges = new();
            public IReadOnlyDictionary<string, Gauge> Gauges => _gauges;

            public void Update()
            {
                // Its fine that the key here need to call `ToString()`. Better here then in the metrics, where it might
                // impact the performance of whatever is updating the metrics.
                foreach (object keyObj in dict.Keys) // Different dictionary seems to iterate to different KV type. So need to use `Keys` here.
                {
                    string keyStr = keyObj.ToString()!;
                    double value = Convert.ToDouble(dict[keyObj]);
                    string gaugeName = GetGaugeNameKey(dictionaryName, keyStr);
                    ref Gauge? gauge = ref CollectionsMarshal.GetValueRefOrAddDefault(_gauges, gaugeName, out _);
                    gauge ??= CreateGauge(BuildGaugeName(keyStr));
                    SetGauge(value, gauge);
                }
            }
        }

        public class KeyIsLabelGaugeMetricUpdater(Gauge gauge, IDictionary dict) : IMetricUpdater
        {
            public void Update()
            {
                foreach (object key in dict.Keys)
                {
                    double value = Convert.ToDouble(dict[key]);
                    switch (key)
                    {
                        case IMetricLabels label:
                            Update(value, label.Labels);
                            break;
                        case ITuple keyAsTuple:
                            {
                                using ArrayPoolList<string> labels = new ArrayPoolList<string>(keyAsTuple.Length, keyAsTuple.Length);
                                for (int i = 0; i < keyAsTuple.Length; i++)
                                {
                                    labels[i] = keyAsTuple[i]!.ToString()!;
                                }

                                Update(value, labels.AsSpan());
                                break;
                            }
                        default:
                            Update(value, key.ToString()!);
                            break;
                    }
                }
            }

            private void Update(double value, params ReadOnlySpan<string> labels) => SetGauge(value, gauge, labels);
            public Gauge Gauge => gauge;
        }

        public class SummaryMetricUpdater(Summary summary) : IMetricUpdater, IMetricObserver
        {
            public void Update()
            {
                // Noop: Updated when `Observe` is called.
            }

            public void Observe(double value, IMetricLabels? labels = null)
            {
                if (labels is not null)
                {
                    summary.WithLabels(labels.Labels).Observe(value);
                }
                else
                {
                    summary.Observe(value);
                }
            }
        }

        public class HistogramMetricUpdater(Histogram histogram) : IMetricUpdater, IMetricObserver
        {
            public void Update()
            {
                // Noop: Updated when `Observe` is called.
            }

            public void Observe(double value, IMetricLabels? labels = null)
            {
                if (labels is not null)
                {
                    histogram.WithLabels(labels.Labels).Observe(value);
                }
                else
                {
                    histogram.Observe(value);
                }
            }
        }

        public void RegisterMetrics(Type type)
        {
            if (_metricTypes.Add(type))
            {
                EnsurePropertiesCached(type);
            }
        }

        internal record CommonMetricInfo(string Name, string Description, Dictionary<string, string> Tags);

        private static CommonMetricInfo DetermineMetricInfo(MemberInfo member)
        {
            string name = BuildGaugeName(member);
            string description = member.GetCustomAttribute<DescriptionAttribute>()?.Description!;

            Dictionary<string, string> CreateTags() =>
                member.GetCustomAttributes<MetricsStaticDescriptionTagAttribute>().ToDictionary(
                    attribute => attribute.Label,
                    attribute => GetStaticMemberInfo(attribute.Informer, attribute.Label));

            return new CommonMetricInfo(name, description, CreateTags());
        }

        private static Gauge CreateMemberInfoMetricsGauge(MemberInfo member, params string[] labels)
        {
            var metricInfo = DetermineMetricInfo(member);
            return CreateGauge(metricInfo.Name, metricInfo.Description, metricInfo.Tags, labels);
        }

        // Tags that all metrics share
        private static readonly Dictionary<string, string> _commonStaticTags = new()
        {
            { nameof(ProductInfo.Instance), ProductInfo.Instance },
            { nameof(ProductInfo.Network), ProductInfo.Network },
            { nameof(ProductInfo.SyncType), ProductInfo.SyncType },
            { nameof(ProductInfo.PruningMode), ProductInfo.PruningMode },
            { nameof(ProductInfo.Version), ProductInfo.Version },
            { nameof(ProductInfo.Commit), ProductInfo.Commit },
            { nameof(ProductInfo.Runtime), ProductInfo.Runtime },
            { nameof(ProductInfo.BuildTimestamp), ProductInfo.BuildTimestamp.ToUnixTimeSeconds().ToString() },
        };

        private static ObservableInstrument<double> CreateDiagnosticsMetricsObservableGauge(Meter meter, MemberInfo member, Func<double> observer)
        {
            string description = member.GetCustomAttribute<DescriptionAttribute>()?.Description!;
            string name = member.GetCustomAttribute<DataMemberAttribute>()?.Name ?? member.Name;

            return member.GetCustomAttribute<CounterMetricAttribute>() is not null
                ? meter.CreateObservableCounter(name, observer, description: description)
                : meter.CreateObservableGauge(name, observer, description: description);
        }

        private static string GetStaticMemberInfo(Type givenInformer, string givenName)
        {
            Type type = givenInformer;
            PropertyInfo[] tagsData = type.GetProperties(BindingFlags.Static | BindingFlags.Public);
            PropertyInfo info = tagsData.FirstOrDefault(info => info.Name == givenName) ?? throw new NotSupportedException("Developer error: a requested static description field was not implemented!");
            object value = info.GetValue(null) ?? throw new NotSupportedException("Developer error: a requested static description field was not initialised!");
            return value.ToString()!;
        }

        private void EnsurePropertiesCached(Type type)
        {
            if (!_metricUpdaters.ContainsKey(type))
            {
                Meter? meter = null;
                if (_useCounters)
                {
                    meter = new(type.Namespace!);
                }

                IList<IMetricUpdater> metricUpdaters = new List<IMetricUpdater>();
                IEnumerable<MemberInfo> members = type.GetProperties().Concat<MemberInfo>(type.GetFields());
                foreach (var member in members)
                {
                    if (member.GetCustomAttribute<DetailedMetricAttribute>() is not null && !_enableDetailedMetric) continue;
                    if (TryCreateMetricUpdater(type, meter, member, out IMetricUpdater updater))
                    {
                        metricUpdaters.Add(updater);
                    }
                }
                _metricUpdaters[type] = metricUpdaters.ToArray();
            }
        }

        private bool TryCreateMetricUpdater(Type type, Meter? meter, MemberInfo memberInfo, out IMetricUpdater metricUpdater)
        {
            Type memberType = memberInfo.GetMemberType();

            if (memberType.IsAssignableTo(typeof(IMetricObserver)) && memberInfo.GetCustomAttribute<SummaryMetricAttribute>() is SummaryMetricAttribute summaryAttribute)
            {
                CommonMetricInfo metricInfo = DetermineMetricInfo(memberInfo);

                Summary summary = Prometheus.Metrics.WithLabels(metricInfo.Tags).CreateSummary(metricInfo.Name, metricInfo.Description,
                    new SummaryConfiguration()
                    {
                        LabelNames = summaryAttribute.LabelNames,
                        Objectives = summaryAttribute.ObjectiveQuantile.Zip(summaryAttribute.ObjectiveEpsilon).Select(o => new QuantileEpsilonPair(o.Item1, o.Item2)).ToArray(),
                    });

                metricUpdater = new SummaryMetricUpdater(summary);
                memberInfo.SetValue(metricUpdater);

                _individualUpdater.Add(GetGaugeNameKey(type.Name, memberInfo.Name), metricUpdater);
                return true;
            }

            if (memberType.IsAssignableTo(typeof(IMetricObserver)) && memberInfo.GetCustomAttribute<ExponentialPowerHistogramMetric>() is ExponentialPowerHistogramMetric histogramAttribute)
            {
                CommonMetricInfo metricInfo = DetermineMetricInfo(memberInfo);

                Histogram histogram = Prometheus.Metrics.WithLabels(metricInfo.Tags).CreateHistogram(metricInfo.Name, metricInfo.Description,
                    new HistogramConfiguration()
                    {
                        LabelNames = histogramAttribute.LabelNames,
                        Buckets = Histogram.ExponentialBuckets(histogramAttribute.Start, histogramAttribute.Factor, histogramAttribute.Count)
                    });

                metricUpdater = new HistogramMetricUpdater(histogram);
                memberInfo.SetValue(metricUpdater);

                _individualUpdater.Add(GetGaugeNameKey(type.Name, memberInfo.Name), metricUpdater);
                return true;
            }

            if (!memberType.IsEnumerable())
            {
                Func<double> accessor = GetValueAccessor(memberInfo);

                if (meter is not null)
                {
                    CreateDiagnosticsMetricsObservableGauge(meter, memberInfo, accessor);
                }

                Gauge gauge = CreateMemberInfoMetricsGauge(memberInfo);
                metricUpdater = new GaugeMetricUpdater(gauge, accessor);
                _individualUpdater.Add(GetGaugeNameKey(type.Name, memberInfo.Name), metricUpdater);
                return true;
            }

            if (memberType.IsDictionary())
            {
                IDictionary dict = memberInfo.GetValue<IDictionary>();
                string[]? labelNames = memberInfo.GetCustomAttribute<KeyIsLabelAttribute>()?.LabelNames;
                metricUpdater = labelNames?.Length > 0
                    ? new KeyIsLabelGaugeMetricUpdater(CreateMemberInfoMetricsGauge(memberInfo, labelNames), dict)
                    : new GaugePerKeyMetricUpdater(dict, memberInfo.Name);
                _individualUpdater.Add(GetGaugeNameKey(type.Name, memberInfo.Name), metricUpdater);
                return true;
            }

            metricUpdater = null!;
            return false;
        }

        private static string BuildGaugeName(MemberInfo propertyInfo) =>
            propertyInfo.GetCustomAttribute<DataMemberAttribute>()?.Name ?? BuildGaugeName(propertyInfo.Name);

        private static string BuildGaugeName(string propertyName) =>
            $"nethermind_{GetGaugeNameRegex().Replace(propertyName, "$1_$2").ToLowerInvariant()}";

        private static Gauge CreateGauge(string name, string? help = null, IDictionary<string, string>? staticLabels = null, params string[] labels) => staticLabels is null
            ? Prometheus.Metrics.CreateGauge(name, help ?? string.Empty, labels)
            : Prometheus.Metrics.WithLabels(staticLabels).CreateGauge(name, help ?? string.Empty, labels);

        public MetricsController(IMetricsConfig metricsConfig)
        {
            if (metricsConfig.InitializeStaticLabels && !_staticLabelsInitialized)
            {
                _staticLabelsInitialized = true;
                Prometheus.Metrics.DefaultRegistry.SetStaticLabels(_commonStaticTags);
            }

            _intervalSeconds = metricsConfig.IntervalSeconds == 0 ? 5 : metricsConfig.IntervalSeconds;
            _useCounters = metricsConfig.CountersEnabled;
            _enableDetailedMetric = metricsConfig.EnableDetailedMetric;
        }

        public void StartUpdating() => _timer = new Timer(UpdateAllMetrics, null, TimeSpan.Zero, TimeSpan.FromSeconds(_intervalSeconds));

        public void StopUpdating() => _timer?.Change(Timeout.Infinite, 0);

        private void UpdateAllMetrics(object? state) => UpdateAllMetrics();

        public void UpdateAllMetrics()
        {
            foreach (Action callback in _callbacks)
            {
                callback();
            }

            foreach (Type metricType in _metricTypes)
            {
                UpdateMetrics(metricType);
            }
        }

        public void AddMetricsUpdateAction(Action callback)
        {
            _callbacks.Add(callback);
        }

        private void UpdateMetrics(Type type)
        {
            EnsurePropertiesCached(type);

            foreach (IMetricUpdater metricUpdater in _metricUpdaters[type])
            {
                metricUpdater.Update();
            }
        }

        private static Func<double> GetValueAccessor(MemberInfo member) => () => Convert.ToDouble(member.GetValue<object>());

        private static string GetGaugeNameKey(params string[] par) => string.Join('.', par);

        [GeneratedRegex("(\\p{Ll})(\\p{Lu})")]
        private static partial Regex GetGaugeNameRegex();

        private static void SetGauge(double value, Gauge gauge, params ReadOnlySpan<string> labels)
        {
            IGauge gaugeToSet = labels.Length > 0 ? gauge.WithLabels(labels) : gauge;
            if (Math.Abs(gaugeToSet.Value - value) > double.Epsilon)
                gaugeToSet.Set(value);
        }
    }
}
