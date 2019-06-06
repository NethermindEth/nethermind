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
            PropertyInfo[] propertyInfos = type.GetProperties();
            foreach (PropertyInfo propertyInfo in propertyInfos)
            {
                _gauges[string.Concat(type.Name, ".", propertyInfo.Name)] = CreateGauge(BuildGaugeName(propertyInfo.Name));
            }
        }
        
        public void UpdateMetrics(Type type)
        {
            PropertyInfo[] propertyInfos = type.GetProperties();
            foreach (PropertyInfo propertyInfo in propertyInfos)
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