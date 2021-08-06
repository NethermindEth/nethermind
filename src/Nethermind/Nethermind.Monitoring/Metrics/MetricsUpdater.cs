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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using Nethermind.Monitoring.Config;
using Prometheus;

namespace Nethermind.Monitoring.Metrics
{
    public class MetricsUpdater : IMetricsUpdater
    {
        private readonly int _intervalSeconds;
        private Timer _timer;
        private Dictionary<string, Gauge> _gauges = new Dictionary<string, Gauge>();
        private Dictionary<Type, (PropertyInfo, string)[]> _propertiesCache = new Dictionary<Type, (PropertyInfo, string)[]>();
        private Dictionary<Type, (FieldInfo, string)[]> _fieldsCache = new Dictionary<Type, (FieldInfo, string)[]>();
        private Dictionary<Type, (string DictName, IDictionary<string, long> Dict)> _dynamicPropCache = new Dictionary<Type, (string DictName, IDictionary<string, long> Dict)>();
        private HashSet<Type> _metricTypes = new HashSet<Type>();

        public void RegisterMetrics(Type type)
        {
            EnsurePropertiesCached(type);
            foreach ((PropertyInfo propertyInfo, string gaugeName) in _propertiesCache[type])
            {
                _gauges[gaugeName] = CreateGauge(BuildGaugeName(propertyInfo.Name));
            }
            
            foreach ((FieldInfo fieldInfo, string gaugeName) in _fieldsCache[type])
            {
                _gauges[gaugeName] = CreateGauge(BuildGaugeName(fieldInfo.Name));
            }

            _metricTypes.Add(type);
        }

        private void EnsurePropertiesCached(Type type)
        {
            if (!_propertiesCache.ContainsKey(type))
            {
                _propertiesCache[type] = type.GetProperties().Where(p => !p.PropertyType.IsAssignableTo(typeof(System.Collections.IEnumerable))).Select(
                    p => (p, GetGaugeNameKey(type.Name, p.Name))).ToArray();
            }
            
            if (!_fieldsCache.ContainsKey(type))
            {
                _fieldsCache[type] = type.GetFields().Where(f => !f.FieldType.IsAssignableTo(typeof(System.Collections.IEnumerable))).Select(
                    f => (f, GetGaugeNameKey(type.Name, f.Name))).ToArray();
            }

            if(!_dynamicPropCache.ContainsKey(type))
            {
                var p = type.GetProperties().Where(p => p.PropertyType.IsAssignableTo(typeof(IDictionary<string, long>))).FirstOrDefault();
                if(p != null)
                {
                    _dynamicPropCache[type] = (p.Name, (IDictionary<string, long>)p.GetValue(null));
                }              
            }
        }

        private static string BuildGaugeName(string propertyName)
        {
            return Regex.Replace(propertyName, @"(\p{Ll})(\p{Lu})", "$1_$2").ToLowerInvariant();
        }

        private static Gauge CreateGauge(string name, string help = "")
            => Prometheus.Metrics.CreateGauge($"nethermind_{name}", help);

        public MetricsUpdater(IMetricsConfig metricsConfig)
        {
            _intervalSeconds = metricsConfig.IntervalSeconds == 0 ? 5 : metricsConfig.IntervalSeconds;
        }

        public void StartUpdating()
        {
            _timer = new Timer(UpdateMetrics, null, TimeSpan.Zero, TimeSpan.FromSeconds(_intervalSeconds));
        }

        public void StopUpdating()
        {
            _timer?.Change(Timeout.Infinite, 0);
        }

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
            
            foreach ((PropertyInfo propertyInfo, string gaugeName) in _propertiesCache[type])
            {
                double value = Convert.ToDouble(propertyInfo.GetValue(null));
                ReplaceValueIfChanged(value, gaugeName);
            }
            
            foreach ((FieldInfo fieldInfo, string gaugeName) in _fieldsCache[type])
            {
                double value = Convert.ToDouble(fieldInfo.GetValue(null));
                ReplaceValueIfChanged(value, gaugeName);
            }

            if (_dynamicPropCache.TryGetValue(type, out var dict))
            {
                foreach (var kvp in dict.Dict)
                {
                    double value = Convert.ToDouble(kvp.Value);
                    var gaugeName = GetGaugeNameKey(dict.DictName, kvp.Key);

                    if(ReplaceValueIfChanged(value, gaugeName) == null)
                    {
                        var gauge = CreateGauge(BuildGaugeName(kvp.Key));
                        _gauges[gaugeName] = gauge;
                        gauge.Set(value);
                    }
                }
            }

            Gauge ReplaceValueIfChanged(double value, string gaugeName)
            {
                if (_gauges.TryGetValue(gaugeName, out var gauge))
                {
                    if (Math.Abs(gauge.Value - value) > double.Epsilon)
                    {
                        gauge.Set(value);
                    }
                }

                return gauge;
            }
        }

        private string GetGaugeNameKey(params string[] par)
        {
            return string.Join('.', par);
        }
    }
}
