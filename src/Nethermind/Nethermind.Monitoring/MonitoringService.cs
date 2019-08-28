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
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Monitoring.Metrics;
using Prometheus;
using Nethermind.Config;

namespace Nethermind.Monitoring
{
    public class MonitoringService : IMonitoringService
    {
        private readonly IMetricsUpdater _metricsUpdater;
        private readonly string _enode;
        private readonly int _intervalSeconds;
        private readonly string _clientVersion;
        private readonly string _pushGatewayUrl;
        private readonly ILogger _logger;
        private readonly Options _options;

        public MonitoringService(IMetricsUpdater metricsUpdater, string pushGatewayUrl,
            string clientVersion, string enode, int intervalSeconds, ILogManager logManager)
        {
            _metricsUpdater = metricsUpdater ?? throw new ArgumentNullException(nameof(metricsUpdater));
            _pushGatewayUrl = string.IsNullOrWhiteSpace(pushGatewayUrl)
                ? throw new ArgumentNullException(nameof(pushGatewayUrl))
                : pushGatewayUrl;
            _clientVersion = string.IsNullOrWhiteSpace(clientVersion)
                ? throw new ArgumentNullException(nameof(clientVersion))
                : clientVersion;
            _enode = string.IsNullOrWhiteSpace(enode)
                ? throw new ArgumentNullException(nameof(enode))
                : enode;
            _intervalSeconds = intervalSeconds <= 0
                ? throw new ArgumentException($"Invalid monitoring interval: {intervalSeconds}s")
                : intervalSeconds;
            _logger = logManager == null
                ? throw new ArgumentNullException(nameof(logManager))
                : logManager.GetClassLogger();
            _options = GetOptions();
        }

        public async Task StartAsync()
        {
            var metricServer = new MetricPusher(_pushGatewayUrl, _options.Job, _options.Instance,
                _intervalSeconds * 1000, new[]
                {
                    new Tuple<string, string>("nethermind_group", _options.Group),
                });
            metricServer.Start();
            await Task.Factory.StartNew(() => _metricsUpdater.StartUpdating(), TaskCreationOptions.LongRunning);
            if (_logger.IsInfo) _logger.Info($"Started monitoring for the group: {_options.Group}, instance: {_options.Instance}, client: {_clientVersion}");
        }

        public Task StopAsync()
        {
            _metricsUpdater.StopUpdating();

            return Task.CompletedTask;
        }

        private Options GetOptions() 
            => new Options(GetValueFromVariableOrDefault("JOB", "nethermind"), GetGroup(), GetInstance());

        private string GetInstance()
            => _enode.Replace("enode://", string.Empty).Split("@").FirstOrDefault();

        private string GetGroup()
        {
            var group = GetValueFromVariableOrDefault("GROUP", "nethermind");
            var endpoint = _pushGatewayUrl.Split("/").LastOrDefault();
            if (!string.IsNullOrWhiteSpace(endpoint) && endpoint.Contains("-"))
            {
                group = endpoint.Split("-")[0] ?? group;
            }

            return group;
        }

        private static string GetValueFromVariableOrDefault(string variable, string @default)
        {
            var value = Environment.GetEnvironmentVariable($"NETHERMIND_MONITORING_{variable}")?.ToLowerInvariant();

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