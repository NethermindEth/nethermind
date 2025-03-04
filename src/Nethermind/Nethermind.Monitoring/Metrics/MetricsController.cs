// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
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
        private Timer _timer = null!;
        private static bool _staticLabelsInitialized = false;

        private readonly Dictionary<Type, IMetricUpdater[]> _metricUpdaters = new();
        private readonly HashSet<Type> _metricTypes = new();

        // Largely for testing reason
        public readonly Dictionary<string, IMetricUpdater> _individualUpdater = new();

        private readonly bool _useCounters;

        private readonly List<Action> _callbacks = new();

        public interface IMetricUpdater
        {
            void Update();
        }

        public class GaugeMetricUpdater(Gauge gauge, Func<double> accessor, params string[] labels) : IMetricUpdater
        {
            public void Update()
            {
                double value = accessor();
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

            public Gauge Gauge => gauge;
        }

        public class GaugePerKeyMetricUpdater(IDictionary dict, string dictionaryName) : IMetricUpdater
        {
            public readonly Dictionary<string, Gauge> _gauges = new();

            public void Update()
            {
                // Its fine that the key here need to call `ToString()`. Better here then in the metrics, where it might
                // impact the performance of whatever is updating the metrics.
                foreach (object keyObj in dict.Keys) // Different dictionary seems to iterate to different KV type. So need to use `Keys` here.
                {
                    string keyStr = keyObj.ToString()!;
                    double value = Convert.ToDouble(dict[keyObj]);
                    string gaugeName = GetGaugeNameKey(dictionaryName, keyStr);

                    if (ReplaceValueIfChanged(value, gaugeName) is null)
                    {
                        // Don't know why it does not prefix with dictionary name or class name. Not gonna change behaviour now.
                        Gauge gauge = CreateGauge(BuildGaugeName(keyStr));
                        _gauges[gaugeName] = gauge;
                        gauge.Set(value);
                    }
                }
            }

            Gauge? ReplaceValueIfChanged(double value, string gaugeName, params string[] labels)
            {
                if (_gauges.TryGetValue(gaugeName, out Gauge? gauge))
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
                            string[] labels = new string[keyAsTuple.Length];
                            for (int i = 0; i < keyAsTuple.Length; i++)
                            {
                                labels[i] = keyAsTuple[i]!.ToString()!;
                            }

                            Update(value, labels);
                            break;
                        default:
                            Update(value, key.ToString()!);
                            break;
                    }
                }
            }

            private void Update(double value, params string[] labels)
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

            public Gauge Gauge => gauge;
        }

        public void RegisterMetrics(Type type)
        {
            if (_metricTypes.Add(type) == false)
            {
                return;
            }

            EnsurePropertiesCached(type);
        }

        private static Gauge CreateMemberInfoMetricsGauge(MemberInfo member, params string[] labels)
        {
            string name = BuildGaugeName(member);
            string description = member.GetCustomAttribute<DescriptionAttribute>()?.Description!;

            bool haveTagAttributes = member.GetCustomAttributes<MetricsStaticDescriptionTagAttribute>().Any();
            if (!haveTagAttributes)
            {
                return CreateGauge(name, description, null, labels);
            }

            Dictionary<string, string> tags = new();
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
            string description = member.GetCustomAttribute<DescriptionAttribute>()?.Description!;
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
                foreach (var propertyInfo in type.GetProperties())
                {
                    if (TryCreateMetricUpdater(type, meter, propertyInfo, out IMetricUpdater updater))
                    {
                        metricUpdaters.Add(updater);
                    }
                }
                foreach (var fieldInfo in type.GetFields())
                {
                    if (TryCreateMetricUpdater(type, meter, fieldInfo, out IMetricUpdater updater))
                    {
                        metricUpdaters.Add(updater);
                    }
                }

                _metricUpdaters[type] = metricUpdaters.ToArray();
            }
        }

        private bool TryCreateMetricUpdater(Type type, Meter? meter, MemberInfo memberInfo, out IMetricUpdater metricUpdater)
        {
            Type memberType;
            if (memberInfo is PropertyInfo property)
            {
                memberType = property.PropertyType;
            }
            else if (memberInfo is FieldInfo field)
            {
                memberType = field.FieldType;
            }
            else
            {
                throw new UnreachableException();
            }

            static bool NotEnumerable(Type t) => !t.IsAssignableTo(typeof(IEnumerable));
            if (NotEnumerable(memberType))
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

            if (memberType.IsGenericType &&
                (memberType.GetGenericTypeDefinition().IsAssignableTo(typeof(IDictionary)) ||
                 memberType.GetGenericTypeDefinition().IsAssignableTo(typeof(IDictionary<,>)))
               )
            {
                IDictionary dict;
                if (memberInfo is PropertyInfo p)
                {
                    dict = (IDictionary)p.GetValue(null)!;
                }
                else if (memberInfo is FieldInfo f)
                {
                    dict = (IDictionary)f.GetValue(null)!;
                }
                else
                {
                    throw new UnreachableException();
                }

                string[]? labelNames = memberInfo.GetCustomAttribute<KeyIsLabelAttribute>()?.LabelNames;
                metricUpdater = labelNames is null || labelNames.Length == 0
                    ? new GaugePerKeyMetricUpdater(dict, memberInfo.Name)
                    : new KeyIsLabelGaugeMetricUpdater(CreateMemberInfoMetricsGauge(memberInfo, labelNames), dict);
                _individualUpdater.Add(GetGaugeNameKey(type.Name, memberInfo.Name), metricUpdater);
                return true;
                }
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
            if (!_staticLabelsInitialized)
            {
                _staticLabelsInitialized = true;
                Prometheus.Metrics.DefaultRegistry.SetStaticLabels(_commonStaticTags);
            }

            _intervalSeconds = metricsConfig.IntervalSeconds == 0 ? 5 : metricsConfig.IntervalSeconds;
            _useCounters = metricsConfig.CountersEnabled;
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

        private static Func<double> GetValueAccessor(MemberInfo member)
        {
            if (member is PropertyInfo property)
            {
                return () => Convert.ToDouble(property.GetValue(null));
            }

            if (member is FieldInfo field)
            {
                return () => Convert.ToDouble(field.GetValue(null));
            }

            throw new InvalidOperationException($"Type of {member} is not handled");
        }

        private static string GetGaugeNameKey(params string[] par) => string.Join('.', par);

        [GeneratedRegex("(\\p{Ll})(\\p{Lu})")]
        private static partial Regex GetGaugeNameRegex();
    }
}
