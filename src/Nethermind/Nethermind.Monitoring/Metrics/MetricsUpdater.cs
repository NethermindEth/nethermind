//  Copyright (c) 2018 Demerzel Solutions Limited
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
        private Dictionary<Type, PropertyInfo[]> _propertiesCache = new Dictionary<Type, PropertyInfo[]>();
        private Dictionary<Type, FieldInfo[]> _fieldsCache = new Dictionary<Type, FieldInfo[]>();
        private HashSet<Type> _metricTypes = new HashSet<Type>();

        public void RegisterMetrics(Type type)
        {
            EnsurePropertiesCached(type);
            foreach (PropertyInfo propertyInfo in _propertiesCache[type])
            {
                _gauges[string.Concat(type.Name, ".", propertyInfo.Name)] = CreateGauge(BuildGaugeName(propertyInfo.Name));
            }
            
            foreach (FieldInfo fieldInfo in _fieldsCache[type])
            {
                _gauges[string.Concat(type.Name, ".", fieldInfo.Name)] = CreateGauge(BuildGaugeName(fieldInfo.Name));
            }

            _metricTypes.Add(type);
        }

        private void EnsurePropertiesCached(Type type)
        {
            if (!_propertiesCache.ContainsKey(type))
            {
                _propertiesCache[type] = type.GetProperties();
            }
            
            if (!_fieldsCache.ContainsKey(type))
            {
                _fieldsCache[type] = type.GetFields();
            }
        }

        public static string BuildGaugeName(string propertyName)
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

        private void UpdateMetrics(object state)
        {
            foreach (Type metricType in _metricTypes)
            {
                UpdateMetrics(metricType);
            }
        }
        
        private void UpdateMetrics(Type type)
        {
            EnsurePropertiesCached(type);
            
            foreach (PropertyInfo propertyInfo in _propertiesCache[type])
            {
                _gauges[string.Concat(type.Name, ".", propertyInfo.Name)].Set(Convert.ToDouble(propertyInfo.GetValue(null)));
            }
            
            foreach (FieldInfo fieldInfo in _fieldsCache[type])
            {
                _gauges[string.Concat(type.Name, ".", fieldInfo.Name)].Set(Convert.ToDouble(fieldInfo.GetValue(null)));
            }
        }
    }
}