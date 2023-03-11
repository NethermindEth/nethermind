// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Monitoring.Metrics;
using Nethermind.Monitoring.Config;
using System.Net.Http;
using System.IO;
using System.Net.Sockets;
using Prometheus;

namespace Nethermind.Monitoring
{
    public class MonitoringService : IMonitoringService
    {
        private readonly IMetricsController _metricsController;
        private readonly ILogger _logger;
        private readonly Options _options;

        private readonly int? _exposePort;
        private readonly string _nodeName;
        private readonly bool _pushEnabled;
        private readonly string _pushGatewayUrl;
        private readonly int _intervalSeconds;

        public MonitoringService(IMetricsController metricsController, IMetricsConfig metricsConfig, ILogManager logManager)
        {
            _metricsController = metricsController ?? throw new ArgumentNullException(nameof(metricsController));

            int? exposePort = metricsConfig.ExposePort;
            string nodeName = metricsConfig.NodeName;
            string pushGatewayUrl = metricsConfig.PushGatewayUrl;
            bool pushEnabled = metricsConfig.Enabled;
            int intervalSeconds = metricsConfig.IntervalSeconds;

            _exposePort = exposePort;
            _nodeName = string.IsNullOrWhiteSpace(nodeName)
                ? throw new ArgumentNullException(nameof(nodeName))
                : nodeName;
            _pushGatewayUrl = pushGatewayUrl;
            _pushEnabled = pushEnabled;
            _intervalSeconds = intervalSeconds <= 0
                ? throw new ArgumentException($"Invalid monitoring push interval: {intervalSeconds}s")
                : intervalSeconds;

            _logger = logManager is null
                ? throw new ArgumentNullException(nameof(logManager))
                : logManager.GetClassLogger();
            _options = GetOptions();
        }

        public async Task StartAsync()
        {
            if (!string.IsNullOrWhiteSpace(_pushGatewayUrl))
            {
                MetricPusherOptions pusherOptions = new MetricPusherOptions
                {
                    Endpoint = _pushGatewayUrl,
                    Job = _options.Job,
                    Instance = _options.Instance,
                    IntervalMilliseconds = _intervalSeconds * 1000,
                    AdditionalLabels = new[]
                    {
                        new Tuple<string, string>("nethermind_group", _options.Group),
                    },
                    OnError = ex =>
                    {
                        if (ex.InnerException is SocketException)
                        {
                            if (_logger.IsError) _logger.Error("Could not reach PushGatewayUrl, Please make sure you have set the correct endpoint in the configurations.", ex);
                            return;
                        }
                        if (_logger.IsTrace) _logger.Error(ex.Message, ex); // keeping it as Error to log the exception details with it.
                    }
                };
                MetricPusher metricPusher = new MetricPusher(pusherOptions);

                metricPusher.Start();


            }
            if (_exposePort is not null)
            {
                IMetricServer metricServer = new KestrelMetricServer(_exposePort.Value);
                metricServer.Start();
            }
            await Task.Factory.StartNew(() => _metricsController.StartUpdating(), TaskCreationOptions.LongRunning);
            if (_logger.IsInfo) _logger.Info($"Started monitoring for the group: {_options.Group}, instance: {_options.Instance}");
        }

        public void RegisterMetrics(Type type)
        {
            _metricsController.RegisterMetrics(type);
        }

        public Task StopAsync()
        {
            _metricsController.StopUpdating();

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
