// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Nethermind.Api;
using Nethermind.Logging;

namespace Nethermind.HealthChecks
{
    public class NodeHealthCheck : IHealthCheck
    {
        private readonly INethermindApi _api;
        private readonly INodeHealthService _nodeHealthService;
        private readonly ILogger _logger;

        public NodeHealthCheck(
            INodeHealthService nodeHealthService,
            INethermindApi api,
            ILogManager logManager)
        {
            _nodeHealthService = nodeHealthService ?? throw new ArgumentNullException(nameof(nodeHealthService));
            _api = api;
            _logger = logManager.GetClassLogger();
        }

        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                CheckHealthResult healthResult = _nodeHealthService.CheckHealth();
                if (_logger.IsTrace) _logger.Trace($"Checked health result. Healthy: {healthResult.Healthy}");
                string description = FormatMessages(healthResult.Messages.Select(x => x.LongMessage));
                if (healthResult.Healthy)
                    return Task.FromResult(HealthCheckResult.Healthy(description, CreateData(healthResult)));

                return Task.FromResult(HealthCheckResult.Unhealthy(description, null, CreateData(healthResult)));
            }
            catch (Exception ex)
            {
                return Task.FromResult(new HealthCheckResult(context.Registration.FailureStatus, exception: ex));
            }
        }

        private static IReadOnlyDictionary<string, object> CreateData(CheckHealthResult healthResult)
        {
            return new Dictionary<string, object>
            {
                { nameof(healthResult.IsSyncing), healthResult.IsSyncing },
                { nameof(healthResult.Errors), healthResult.Errors }
            };
        }

        private static string FormatMessages(IEnumerable<string> messages)
        {
            if (messages.Any(x => !string.IsNullOrWhiteSpace(x)))
            {
                var joined = string.Join(". ", messages.Where(x => !string.IsNullOrWhiteSpace(x)));
                if (!string.IsNullOrWhiteSpace(joined))
                {
                    return joined + ".";
                }
            }

            return string.Empty;
        }
    }
}
