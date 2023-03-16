// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using Nethermind.Monitoring.Config;
using Prometheus;

namespace Nethermind.Monitoring.Metrics
{
    public partial class MetricsController : IMetricsController
    {
        private readonly int _intervalSeconds;
        private Timer _timer;
        private readonly Dictionary<Type, (MemberInfo, string, Func<double>)[]> _membersCache = new();
        private readonly Dictionary<Type, (string DictName, IDictionary<string, long> Dict)> _dynamicPropCache = new();
        private readonly HashSet<Type> _metricTypes = new();

        public readonly Dictionary<string, Gauge> _gauges = new();

        public void RegisterMetrics(Type type)
        {
            EnsurePropertiesCached(type);
            foreach ((MemberInfo member, string gaugeName, _) in _membersCache[type])
            {
                _gauges[gaugeName] = CreateMemberInfoMetricsGauge(member);
            }

            _metricTypes.Add(type);
        }

        private static Gauge CreateMemberInfoMetricsGauge(MemberInfo propertyInfo)
        {
            Dictionary<string, string> staticLabels = propertyInfo
                .GetCustomAttributes<MetricsStaticDescriptionTagAttribute>()
                .ToDictionary(
                    attribute => attribute.Label,
                    attribute => GetStaticMemberInfo(attribute.Informer, attribute.Label));

            string description = propertyInfo.GetCustomAttribute<DescriptionAttribute>()?.Description;
            string name = BuildGaugeName(propertyInfo);

            return CreateGauge(name, description, staticLabels);
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

            if (!_dynamicPropCache.ContainsKey(type))
            {
                var p = type.GetProperties().FirstOrDefault(p => p.PropertyType.IsAssignableTo(typeof(IDictionary<string, long>)));
                if (p != null)
                {
                    _dynamicPropCache[type] = (p.Name, (IDictionary<string, long>)p.GetValue(null));
                }
            }
        }

        private static string BuildGaugeName(MemberInfo propertyInfo) =>
            propertyInfo.GetCustomAttribute<DataMemberAttribute>()?.Name ?? BuildGaugeName(propertyInfo.Name);

        private static string BuildGaugeName(string propertyName) =>
            $"nethermind_{GetGaugeNameRegex().Replace(propertyName, "$1_$2").ToLowerInvariant()}";

        private static Gauge CreateGauge(string name, string help = null, IDictionary<string, string> labels = null) => labels is null
            ? Prometheus.Metrics.CreateGauge(name, help ?? string.Empty)
            : Prometheus.Metrics.WithLabels(labels).CreateGauge(name, help ?? string.Empty);

        public MetricsController(IMetricsConfig metricsConfig)
        {
            _intervalSeconds = metricsConfig.IntervalSeconds == 0 ? 5 : metricsConfig.IntervalSeconds;
        }

        public void StartUpdating() => _timer = new Timer(UpdateMetrics, null, TimeSpan.Zero, TimeSpan.FromSeconds(_intervalSeconds));

        public void StopUpdating() => _timer?.Change(Timeout.Infinite, 0);

        public void UpdateMetrics(object state)
        {
            foreach (Type metricType in _metricTypes)
            {
                UpdateMetrics(metricType);
            }
        }

        private void UpdateMetrics(Type type)
        {
            EnsurePropertiesCached(type);

            foreach ((MemberInfo _, string gaugeName, Func<double> accessor) in _membersCache[type])
            {
                ReplaceValueIfChanged(accessor(), gaugeName);
            }

            if (_dynamicPropCache.TryGetValue(type, out var dict))
            {
                foreach (var kvp in dict.Dict)
                {
                    double value = Convert.ToDouble(kvp.Value);
                    var gaugeName = GetGaugeNameKey(dict.DictName, kvp.Key);

                    if (ReplaceValueIfChanged(value, gaugeName) is null)
                    {
                        Gauge gauge = CreateGauge(BuildGaugeName(kvp.Key));
                        _gauges[gaugeName] = gauge;
                        gauge.Set(value);
                    }
                }
            }

            Gauge ReplaceValueIfChanged(double value, string gaugeName)
            {
                if (_gauges.TryGetValue(gaugeName, out Gauge gauge))
                {
                    if (Math.Abs(gauge.Value - value) > double.Epsilon)
                        gauge.Set(value);
                }

                return gauge;
            }
        }

        private static string GetGaugeNameKey(params string[] par) => string.Join('.', par);

        [GeneratedRegex("(\\p{Ll})(\\p{Lu})")]
        private static partial Regex GetGaugeNameRegex();
    }
}
