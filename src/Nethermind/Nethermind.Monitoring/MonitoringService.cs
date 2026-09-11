// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Monitoring.Metrics;
using Nethermind.Monitoring.Config;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Prometheus;

namespace Nethermind.Monitoring;

public class MonitoringService : IMonitoringService, IAsyncDisposable
{
    private readonly IMetricsController _metricsController;
    private readonly ILogger _logger;
    private readonly Options _options;

    private readonly string _exposeHost;
    private readonly int? _exposePort;
    private readonly string _nodeName;
    private readonly string _pushGatewayUrl;
    private readonly string _pushGatewayUsername;
    private readonly string _pushGatewayPassword;
    private readonly int _intervalSeconds;
    private readonly CancellationTokenSource _timerCancellationSource;

    private Task _monitoringTimerTask = Task.CompletedTask;
    private MetricPusher _metricPusher;
    private HttpClient _pusherHttpClient;
    private int _isDisposed = 0;

    public MonitoringService(
        IMetricsController metricsController,
        IMetricsConfig metricsConfig,
        ILogManager logManager
    )
    {
        _timerCancellationSource = new CancellationTokenSource();
        _metricsController = metricsController ?? throw new ArgumentNullException(nameof(metricsController));

        string exposeHost = metricsConfig.ExposeHost;
        int? exposePort = metricsConfig.ExposePort;
        string nodeName = metricsConfig.NodeName;
        string pushGatewayUrl = metricsConfig.PushGatewayUrl;
        int intervalSeconds = metricsConfig.IntervalSeconds;

        _exposeHost = exposeHost;
        _exposePort = exposePort;
        _nodeName = string.IsNullOrWhiteSpace(nodeName)
            ? throw new ArgumentNullException(nameof(nodeName))
            : nodeName;
        _pushGatewayUrl = pushGatewayUrl;
        _pushGatewayUsername = metricsConfig.PushGatewayUsername;
        _pushGatewayPassword = metricsConfig.PushGatewayPassword;
        _intervalSeconds = intervalSeconds <= 0
            ? throw new ArgumentException($"Invalid monitoring push interval: {intervalSeconds}s")
            : intervalSeconds;

        _logger = logManager is null
            ? throw new ArgumentNullException(nameof(logManager))
            : logManager.GetClassLogger<MonitoringService>();
        _options = GetOptions(metricsConfig);
    }

    public Task StartAsync()
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
                    _logger.TraceError(ex.Message, ex); // keeping it at Error severity to log exception details
                }
            };

            bool hasUsername = !string.IsNullOrEmpty(_pushGatewayUsername);
            bool hasPassword = !string.IsNullOrEmpty(_pushGatewayPassword);
            if (hasUsername && hasPassword)
            {
                if (!_pushGatewayUrl.StartsWith(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    if (_logger.IsWarn) _logger.Warn("Pushgateway basic authentication credentials are sent over an unencrypted connection: consider using an HTTPS endpoint.");
                }

                _pusherHttpClient = new HttpClient();
                _pusherHttpClient.DefaultRequestHeaders.Authorization = CreateBasicAuthHeader(_pushGatewayUsername, _pushGatewayPassword);
                pusherOptions.HttpClientProvider = () => _pusherHttpClient;
            }
            else if (hasUsername || hasPassword)
            {
                if (_logger.IsWarn) _logger.Warn("Pushgateway basic authentication is disabled: both the username and password must be set.");
            }

            _metricPusher = new MetricPusher(pusherOptions);

            _metricPusher.Start();
        }

        if (_exposePort is not null)
        {
            new NethermindKestrelMetricServer(_exposeHost, _exposePort.Value).Start();
        }

        _monitoringTimerTask = Task.Run(async () =>
        {
            try
            {
                await _metricsController.RunTimer(_timerCancellationSource.Token);
            }
            catch (Exception ex)
            {
                if (_logger.IsError) _logger.Error($"Monitoring timer failed: {ex}");
            }
        });

        if (_logger.IsInfo) _logger.Info($"Started monitoring for the group: {_options.Group}, instance: {_options.Instance}");
        return Task.CompletedTask;
    }

    internal static AuthenticationHeaderValue CreateBasicAuthHeader(string username, string password) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));

    public void AddMetricsUpdateAction(Action callback) => _metricsController.AddMetricsUpdateAction(callback);

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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) != 0) return;
        await _timerCancellationSource.CancelAsync();
        await _monitoringTimerTask;
        _timerCancellationSource.Dispose();
        if (_metricPusher is not null) await _metricPusher.StopAsync();
        _pusherHttpClient?.Dispose();
    }
}
