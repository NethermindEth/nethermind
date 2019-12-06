/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using Prometheus;

namespace Nethermind.Monitoring.Metrics
{
    internal class MetricsRegistry
    {
        private Dictionary<string, Gauge> _gauges = new Dictionary<string, Gauge>();
        
        private Dictionary<Type, PropertyInfo[]> _propertiesCache = new Dictionary<Type, PropertyInfo[]>();

        public MetricsRegistry()
        {
            RegisterMetrics(typeof(Blockchain.Metrics));
            RegisterMetrics(typeof(Store.Metrics));
            RegisterMetrics(typeof(Evm.Metrics));
            RegisterMetrics(typeof(Network.Metrics));
            RegisterMetrics(typeof(JsonRpc.Metrics));
        }
        
        private void RegisterMetrics(Type type)
        {
            EnsurePropertiesCached(type);
            foreach (PropertyInfo propertyInfo in _propertiesCache[type])
            {
                _gauges[string.Concat(type.Name, ".", propertyInfo.Name)] = CreateGauge(BuildGaugeName(propertyInfo.Name));
            }
        }

        private void EnsurePropertiesCached(Type type)
        {
            if (!_propertiesCache.ContainsKey(type))
            {
                _propertiesCache[type] = type.GetProperties();
            }
        }

        public void UpdateMetrics(Type type)
        {
            EnsurePropertiesCached(type);
            foreach (PropertyInfo propertyInfo in _propertiesCache[type])
            {
                _gauges[string.Concat(type.Name, ".", propertyInfo.Name)].Set(Convert.ToDouble(propertyInfo.GetValue(null)));
            }
        }

        private string BuildGaugeName(string propertyName)
        {
            return Regex.Replace(propertyName, @"(\p{Ll})(\p{Lu})", "$1_$2").ToLowerInvariant();
        }

        private static Gauge CreateGauge(string name, string help = "")
            => Prometheus.Metrics.CreateGauge($"nethermind_{name}", help);
    }
}