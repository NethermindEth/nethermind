// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Api.Extensions;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Monitoring.Config;
using Nethermind.Network;

namespace Nethermind.HealthChecks;

public class HealthCheckJsonRpcConfigurer(
    INodeHealthService nodeHealthService,
    IHealthChecksConfig healthChecksConfig,
    IIPResolver ipResolver,
    IMetricsConfig metricsConfig,
    IJsonRpcConfig jsonRpcConfig,
    ILogManager logManager
) : IJsonRpcServiceConfigurer
{
    private readonly ILogger _logger = logManager.GetClassLogger<HealthCheckJsonRpcConfigurer>();

    public void Configure(IServiceCollection service)
    {
        service.AddHealthChecks()
            .AddTypeActivatedCheck<NodeHealthCheck>(
                "node-health",
                args: new object[] { nodeHealthService, logManager });
        if (healthChecksConfig.UIEnabled)
        {
            if (!healthChecksConfig.Enabled)
            {
                if (_logger.IsWarn) _logger.Warn("To use HealthChecksUI please enable HealthChecks. (--HealthChecks.Enabled=true)");
                return;
            }

            service.AddHealthChecksUI(setup =>
                {
                    setup.AddHealthCheckEndpoint("health", BuildEndpointForUi());
                    setup.SetEvaluationTimeInSeconds(healthChecksConfig.PollingInterval);
                    setup.SetHeaderText("Nethermind Node Health");
                    if (healthChecksConfig.WebhooksEnabled)
                    {
                        setup.AddWebhookNotification("webhook",
                            uri: healthChecksConfig.WebhooksUri,
                            payload: healthChecksConfig.WebhooksPayload,
                            restorePayload: healthChecksConfig.WebhooksRestorePayload,
                            customDescriptionFunc: (livenessName, report) =>
                            {
                                string description = report.Entries["node-health"].Description;

                                string hostname = Dns.GetHostName();

                                HealthChecksWebhookInfo info = new(description, ipResolver, metricsConfig, hostname);
                                return info.GetFullInfo();
                            }
                        );
                    }
                })
                .AddInMemoryStorage();
        }
    }

    private string BuildEndpointForUi()
    {
        string host = jsonRpcConfig.Host.Replace("0.0.0.0", "localhost");
        host = host.Replace("[::]", "localhost");
        return new UriBuilder("http", host, jsonRpcConfig.Port, healthChecksConfig.Slug).ToString();
    }
}
