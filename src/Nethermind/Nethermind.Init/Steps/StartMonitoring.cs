// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Logging;
using Nethermind.Monitoring;
using Nethermind.Monitoring.Config;

namespace Nethermind.Init.Steps;

[RunnerStepDependencies()]
public class StartMonitoring(
    IMonitoringService monitoringService,
    ILogManager logManager,
    IMetricsConfig metricsConfig
) : IStep
{
    private readonly ILogger _logger = logManager.GetClassLogger();

    public async Task Execute(CancellationToken cancellationToken)
    {
        // hacky
        if (!string.IsNullOrEmpty(metricsConfig.NodeName))
        {
            logManager.SetGlobalVariable("nodeName", metricsConfig.NodeName);
        }

        if (metricsConfig.Enabled)
        {
            await monitoringService.StartAsync().ContinueWith(x =>
            {
                if (x.IsFaulted && _logger.IsError)
                    _logger.Error("Error during starting a monitoring.", x.Exception);
            }, cancellationToken);
        }
        else
        {
            if (_logger.IsInfo)
                _logger.Info("Grafana / Prometheus metrics are disabled in configuration");
        }

        if (_logger.IsInfo)
        {
            _logger.Info(metricsConfig.CountersEnabled
                ? "System.Diagnostics.Metrics enabled and will be collectable with dotnet-counters"
                : "System.Diagnostics.Metrics disabled");
        }
    }

    public bool MustInitialize => false;
}
