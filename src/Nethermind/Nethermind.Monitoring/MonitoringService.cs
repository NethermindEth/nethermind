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
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Monitoring.Metrics;
using Prometheus;
using Nethermind.Monitoring.Config;

namespace Nethermind.Monitoring
{
    public class MonitoringService : IMonitoringService
    {
        private readonly IMetricsUpdater _metricsUpdater;
        private readonly ILogger _logger;
        private readonly Options _options;
        
        private readonly int? _exposePort;
        private readonly string _nodeName;
        private readonly bool _pushEnabled;
        private readonly string _pushGatewayUrl;
        private readonly int _intervalSeconds;

        public MonitoringService(IMetricsUpdater metricsUpdater, IMetricsConfig metricsConfig, ILogManager logManager)
        {
            _metricsUpdater = metricsUpdater ?? throw new ArgumentNullException(nameof(metricsUpdater));

            int? exposePort = metricsConfig.ExposePort;
            string nodeName = metricsConfig.NodeName;
            string pushGatewayUrl = metricsConfig.PushGatewayUrl;
            bool pushEnabled = metricsConfig.Enabled;
            int intervalSeconds = metricsConfig.IntervalSeconds;

            _exposePort = exposePort;
            _nodeName = string.IsNullOrWhiteSpace(nodeName)
                ? throw new ArgumentNullException(nameof(nodeName))
                : nodeName;
            _pushGatewayUrl = string.IsNullOrWhiteSpace(pushGatewayUrl)
                ? throw new ArgumentNullException(nameof(pushGatewayUrl))
                : pushGatewayUrl;
            _pushEnabled = pushEnabled;
            _intervalSeconds = intervalSeconds <= 0
                ? throw new ArgumentException($"Invalid monitoring push interval: {intervalSeconds}s")
                : intervalSeconds;
            
            _logger = logManager == null
                ? throw new ArgumentNullException(nameof(logManager))
                : logManager.GetClassLogger();
            _options = GetOptions();
        }

        public async Task StartAsync()
        {
            if (!string.IsNullOrWhiteSpace(_pushGatewayUrl))
            {
                MetricPusher metricPusher = new MetricPusher(_pushGatewayUrl, _options.Job, _options.Instance,
                    _intervalSeconds * 1000, new[]
                    {
                        new Tuple<string, string>("nethermind_group", _options.Group),
                    });
                metricPusher.Start();
            }
            if (_exposePort != null)
            {
                IMetricServer metricServer = new MetricServer(_exposePort.Value, "metrics/");
                metricServer.Start();
            }
            await Task.Factory.StartNew(() => _metricsUpdater.StartUpdating(), TaskCreationOptions.LongRunning);
            if (_logger.IsInfo) _logger.Info($"Started monitoring for the group: {_options.Group}, instance: {_options.Instance}");
        }

        public void RegisterMetrics(Type type)
        {
            _metricsUpdater.RegisterMetrics(type);
        }
        
        public Task StopAsync()
        {
            _metricsUpdater.StopUpdating();

            return Task.CompletedTask;
        }

        private Options GetOptions() 
            => new Options(GetValueFromVariableOrDefault("JOB", "nethermind"), GetGroup(), GetInstance());

        private string GetInstance()
            => _nodeName.Replace("enode://", string.Empty).Split("@").FirstOrDefault();

        private string GetGroup()
        {
            string group = GetValueFromVariableOrDefault("GROUP", "nethermind");
            string endpoint = _pushGatewayUrl.Split("/").LastOrDefault();
            if (!string.IsNullOrWhiteSpace(endpoint) && endpoint.Contains("-"))
            {
                group = endpoint.Split("-")[0] ?? group;
            }

            return group;
        }

        private static string GetValueFromVariableOrDefault(string variable, string @default)
        {
            string value = Environment.GetEnvironmentVariable($"NETHERMIND_MONITORING_{variable}")?.ToLowerInvariant();

            return string.IsNullOrWhiteSpace(value) ? @default : value;
        }

        private class Options
        {
            public string Job { get; }
            public string Instance { get; }
            public string Group { get; }
            public Options(string job, string @group, string instance)
            {
                Job = job;
                Group = @group;
                Instance = instance;
            }
        }
    }
}
