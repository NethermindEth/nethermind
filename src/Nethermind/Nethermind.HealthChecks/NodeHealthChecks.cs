//  Copyright (c) 2020 Demerzel Solutions Limited
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
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Nethermind.Logging;

namespace Nethermind.HealthChecks
{
    public class NodeHealthCheck : IHealthCheck
    {
        private readonly INodeHealthService _nodeHealthService;
        private readonly ILogger _logger;

        public NodeHealthCheck(
            INodeHealthService nodeHealthService,
            ILogManager logManager)
        {
            _nodeHealthService = nodeHealthService ?? throw new ArgumentNullException(nameof(nodeHealthService));
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
                    return Task.FromResult(HealthCheckResult.Healthy(description));

                return Task.FromResult(HealthCheckResult.Unhealthy(description));
            }
            catch (Exception ex)
            {
                return Task.FromResult(new HealthCheckResult(context.Registration.FailureStatus, exception: ex));
            }
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
