// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Monitoring.Metrics;
using Nethermind.Monitoring.Config;
using System.Net.Sockets;
using Nethermind.Core.ServiceStopper;
using Prometheus;

namespace Nethermind.Monitoring;

public class MonitoringService : IMonitoringService, IStoppableService
{
    private readonly IMetricsController _metricsController;
    private readonly ILogger _logger;
    private readonly Options _options;

    private readonly string _exposeHost;
    private readonly int? _exposePort;
    private readonly string _nodeName;
    private readonly bool _pushEnabled;
    private readonly string _pushGatewayUrl;
    private readonly int _intervalSeconds;

    public MonitoringService(IMetricsController metricsController, IMetricsConfig metricsConfig, ILogManager logManager)
    {
        _metricsController = metricsController ?? throw new ArgumentNullException(nameof(metricsController));

        string exposeHost = metricsConfig.ExposeHost;
        int? exposePort = metricsConfig.ExposePort;
        string nodeName = metricsConfig.NodeName;
        string pushGatewayUrl = metricsConfig.PushGatewayUrl;
        bool pushEnabled = metricsConfig.Enabled;
        int intervalSeconds = metricsConfig.IntervalSeconds;

        _exposeHost = exposeHost;
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
        _options = GetOptions(metricsConfig);
    }

    public async Task StartAsync()
    {
        if (_pushGatewayUrl is not null)
        {
            MetricPusherOptions pusherOptions = new()
            {
                Endpoint = _pushGatewayUrl,
                Job = _options.Job,
                Instance = _options.Instance,
                IntervalMilliseconds = _intervalSeconds * 1000,
                AdditionalLabels = [new Tuple<string, string>("nethermind_group", _options.Group)],
                OnError = ex =>
                {
                    if (ex.InnerException is SocketException)
                    {
                        if (_logger.IsError) _logger.Error($"Cannot reach Pushgateway at {_pushGatewayUrl}", ex);
                        return;
                    }
                    if (_logger.IsTrace) _logger.Error(ex.Message, ex); // keeping it as Error to log the exception details with it.
                }
            };
            MetricPusher metricPusher = new(pusherOptions);

            metricPusher.Start();
        }

        if (_exposePort is not null)
        {
            new NethermindKestrelMetricServer(_exposeHost, _exposePort.Value).Start();
        }

        await Task.Factory.StartNew(_metricsController.StartUpdating, TaskCreationOptions.LongRunning);

        if (_logger.IsInfo) _logger.Info($"Started monitoring for the group: {_options.Group}, instance: {_options.Instance}");
    }

    public void AddMetricsUpdateAction(Action callback)
    {
        _metricsController.AddMetricsUpdateAction(callback);
    }

    public Task StopAsync()
    {
        _metricsController.StopUpdating();

        return Task.CompletedTask;
    }

    public string Description => "Monitoring service";

    private Options GetOptions(IMetricsConfig config)
    {
        string endpoint = _pushGatewayUrl?.Split("/").Last();
        string group = endpoint?.Contains('-', StringComparison.Ordinal) == true
            ? endpoint.Split("-")[0] : config.MonitoringGroup;
        string instance = _nodeName.Replace("enode://", string.Empty).Split("@")[0];

        return new(config.MonitoringJob, group, instance);
    }

    private class Options(string job, string group, string instance)
    {
        public string Job { get; } = job;
        public string Instance { get; } = instance;
        public string Group { get; } = group;
    }
}
