// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Collections;
using Nethermind.Core.Metric;
using Nethermind.Monitoring.Config;
using Prometheus;

namespace Nethermind.Monitoring.Metrics
{
    public partial class MetricsController : IMetricsController
    {
        private readonly int _intervalSeconds;
        private CancellationTokenSource _cts;
        private readonly SemaphoreSlim _wait = new(0);
        private readonly Dictionary<Type, (MemberInfo, string, Func<double>)[]> _membersCache = new();
        private readonly Dictionary<Type, DictionaryMetricInfo[]> _dictionaryCache = new();
        private readonly HashSet<Type> _metricTypes = new();

        public readonly Dictionary<string, Gauge> _gauges = new();
        private readonly bool _useCounters;

        private readonly List<Action> _callbacks = new();

        class DictionaryMetricInfo
        {
            internal MemberInfo MemberInfo;
            internal string DictionaryName;
            internal string[] LabelNames;
            internal string GaugeName;
            internal IDictionary Dictionary;
        }

        public void RegisterMetrics(Type type)
        {
            if (_metricTypes.Add(type) == false)
            {
                return;
            }

            Meter meter = new(type.Namespace);

            EnsurePropertiesCached(type);
            foreach ((MemberInfo member, string gaugeName, Func<double> observer) in _membersCache[type])
            {
                if (_useCounters)
                {
                    CreateDiagnosticsMetricsObservableGauge(meter, member, observer);
                }

                _gauges[gaugeName] = CreateMemberInfoMetricsGauge(member);
            }

            foreach (DictionaryMetricInfo info in _dictionaryCache[type])
            {
                if (info.LabelNames is null || info.LabelNames.Length == 0) continue; // Old behaviour creates new metric as it is created
                _gauges[info.GaugeName] = CreateMemberInfoMetricsGauge(info.MemberInfo, info.LabelNames);
            }
        }

        private static Gauge CreateMemberInfoMetricsGauge(MemberInfo member, params string[] labels)
        {
            string name = BuildGaugeName(member);
            string description = member.GetCustomAttribute<DescriptionAttribute>()?.Description;

            bool haveTagAttributes = member.GetCustomAttributes<MetricsStaticDescriptionTagAttribute>().Any();
            if (!haveTagAttributes)
            {
                return CreateGauge(name, description, _commonStaticTags, labels);
            }

            Dictionary<string, string> tags = new(_commonStaticTags);
            member.GetCustomAttributes<MetricsStaticDescriptionTagAttribute>().ForEach(attribute =>
                tags.Add(attribute.Label, GetStaticMemberInfo(attribute.Informer, attribute.Label)));
            return CreateGauge(name, description, tags, labels);
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
            string description = member.GetCustomAttribute<DescriptionAttribute>()?.Description;
            string name = member.GetCustomAttribute<DataMemberAttribute>()?.Name ?? member.Name;

            if (member.GetCustomAttribute<CounterMetricAttribute>() is not null)
            {
                return meter.CreateObservableCounter(name, observer, description: description);
            }

            return meter.CreateObservableGauge(name, observer, description: description);
        }

        private static string GetStaticMemberInfo(Type givenInformer, string givenName)
        {
            Type type = givenInformer;
            PropertyInfo[] tagsData = type.GetProperties(BindingFlags.Static | BindingFlags.Public);
            PropertyInfo info = tagsData.FirstOrDefault(info => info.Name == givenName);
            if (info is null)
                throw new NotSupportedException("Developer error: a requested static description field was not implemented!");

            object value = info.GetValue(null);
            if (value is null)
                throw new NotSupportedException("Developer error: a requested static description field was not initialised!");

            return value.ToString();
        }

        private void EnsurePropertiesCached(Type type)
        {
            static bool NotEnumerable(Type t) => !t.IsAssignableTo(typeof(System.Collections.IEnumerable));

            static Func<double> GetValueAccessor(MemberInfo member)
            {
                if (member is PropertyInfo property)
                {
                    return () => Convert.ToDouble(property.GetValue(null));
                }

                if (member is FieldInfo field)
                {
                    return () => Convert.ToDouble(field.GetValue(null));
                }

                throw new NotImplementedException($"Type of {member} is not handled");
            }

            if (!_membersCache.ContainsKey(type))
            {
                _membersCache[type] = type.GetProperties()
                    .Where(p => NotEnumerable(p.PropertyType))
                    .Concat<MemberInfo>(type.GetFields().Where(f => NotEnumerable(f.FieldType)))
                    .Select(member => (member, GetGaugeNameKey(type.Name, member.Name), GetValueAccessor(member)))
                    .ToArray();
            }

            if (!_dictionaryCache.ContainsKey(type))
            {
                _dictionaryCache[type] = type.GetProperties()
                    .Where(p =>
                        p.PropertyType.IsGenericType &&
                        (
                            p.PropertyType.GetGenericTypeDefinition().IsAssignableTo(typeof(IDictionary))
                            || p.PropertyType.GetGenericTypeDefinition().IsAssignableTo(typeof(IDictionary<,>))
                        ))
                    .Select(p => new DictionaryMetricInfo()
                    {
                        MemberInfo = p,
                        DictionaryName = p.Name,
                        LabelNames = p.GetCustomAttribute<KeyIsLabelAttribute>()?.LabelNames,
                        GaugeName = GetGaugeNameKey(type.Name, p.Name),
                        Dictionary = (IDictionary)p.GetValue(null)
                    })
                    .ToArray();
            }
        }

        private static string BuildGaugeName(MemberInfo propertyInfo) =>
            propertyInfo.GetCustomAttribute<DataMemberAttribute>()?.Name ?? BuildGaugeName(propertyInfo.Name);

        private static string BuildGaugeName(string propertyName) =>
            $"nethermind_{GetGaugeNameRegex().Replace(propertyName, "$1_$2").ToLowerInvariant()}";

        private static Gauge CreateGauge(string name, string help = null, IDictionary<string, string> staticLabels = null, params string[] labels) => staticLabels is null
            ? Prometheus.Metrics.CreateGauge(name, help ?? string.Empty, labels)
            : Prometheus.Metrics.WithLabels(staticLabels).CreateGauge(name, help ?? string.Empty, labels);

        public MetricsController(IMetricsConfig metricsConfig)
        {
            _intervalSeconds = metricsConfig.IntervalSeconds == 0 ? 5 : metricsConfig.IntervalSeconds;
            _useCounters = metricsConfig.CountersEnabled;
        }

        public void StartUpdating(Action onForced)
        {
            _cts = new CancellationTokenSource();
            Task.Run(() => RunLoop(_cts.Token));

            return;
            async Task RunLoop(CancellationToken ct)
            {
                while (ct.IsCancellationRequested == false)
                {
                    bool forced = false;

                    try
                    {
                        forced = await _wait.WaitAsync(TimeSpan.FromSeconds(_intervalSeconds), ct);
                    }
                    catch (OperationCanceledException)
                    {
                    }

                    UpdateMetrics();

                    if (forced && onForced != null)
                    {
                        // The update was forced and there's a onForced delegate. Execute it.
                        onForced();
                    }
                }
            }
        }

        public void ForceUpdate() => _wait.Release();

        public void StopUpdating() => _cts.Cancel();

        public void UpdateMetrics()
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

            foreach ((MemberInfo _, string gaugeName, Func<double> accessor) in _membersCache[type])
            {
                ReplaceValueIfChanged(accessor(), gaugeName);
            }

            foreach (DictionaryMetricInfo info in _dictionaryCache[type])
            {
                if (info.LabelNames is null)
                {
                    IDictionary dict = info.Dictionary;
                    // It's fine that the key here need to call `ToString()`.
                    // Better here then in the metrics, where it might impact the performance of whatever is updating the metrics.
                    foreach (DictionaryEntry kvp in dict)
                    {
                        string keyStr = kvp.Key.ToString();
                        double value = Convert.ToDouble(kvp.Value);
                        string gaugeName = GetGaugeNameKey(info.DictionaryName, keyStr);

                        if (ReplaceValueIfChanged(value, gaugeName) is null)
                        {
                            // Don't know why it does not prefix with dictionary name or class name. Not gonna change behaviour now.
                            Gauge gauge = CreateGauge(BuildGaugeName(keyStr));
                            _gauges[gaugeName] = gauge;
                            gauge.Set(value);
                        }
                    }
                }
                else
                {
                    IDictionary dict = info.Dictionary;
                    string gaugeName = info.GaugeName;
                    foreach (DictionaryEntry kvp in dict)
                    {
                        double value = Convert.ToDouble(kvp.Value);
                        switch (kvp.Key)
                        {
                            case IMetricLabels label:
                                ReplaceValueIfChanged(value, gaugeName, label.Labels);
                                break;
                            case ITuple keyAsTuple:
                                {
                                    string[] labels = new string[keyAsTuple.Length];
                                    for (int i = 0; i < keyAsTuple.Length; i++)
                                    {
                                        labels[i] = keyAsTuple[i].ToString();
                                    }

                                    ReplaceValueIfChanged(value, gaugeName, labels);
                                    break;
                                }
                            default:
                                ReplaceValueIfChanged(value, gaugeName, kvp.Key.ToString());
                                break;
                        }
                    }
                }
            }

            Gauge ReplaceValueIfChanged(double value, string gaugeName, params string[] labels)
            {
                if (_gauges.TryGetValue(gaugeName, out Gauge gauge))
                {
                    if (labels.Length > 0)
                    {
                        Gauge.Child ch = gauge.WithLabels(labels);
                        if (Math.Abs(ch.Value - value) > double.Epsilon)
                            ch.Set(value);
                    }
                    else
                    {
                        if (Math.Abs(gauge.Value - value) > double.Epsilon)
                            gauge.Set(value);
                    }
                }

                return gauge;
            }
        }

        private static string GetGaugeNameKey(params string[] par) => string.Join('.', par);

        [GeneratedRegex("(\\p{Ll})(\\p{Lu})")]
        private static partial Regex GetGaugeNameRegex();
    }
}
