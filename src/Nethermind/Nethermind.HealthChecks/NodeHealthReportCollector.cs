// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HealthChecks.UI.Core;
using HealthChecks.UI.Core.HostedService;
using HealthChecks.UI.Core.Notifications;
using HealthChecks.UI.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Nethermind.HealthChecks;

internal sealed class NodeHealthReportCollector(
    string endpoint,
    HealthChecksDb db,
    HealthCheckService healthCheckService,
    IHealthCheckFailureNotifier notifier,
    IEnumerable<IHealthCheckCollectorInterceptor> interceptors,
    IConfiguration configuration,
    Func<IHealthCheckReportCollector> httpCollector) : IHealthCheckReportCollector
{
    internal const string HttpCollectorKey = "nethermind-health-http";

    /// <inheritdoc />
    public async Task Collect(CancellationToken cancellationToken)
    {
        List<HealthCheckConfiguration> endpoints = await db.Configurations.ToListAsync(cancellationToken);
        // Retain the library collector for remote endpoints and its optional notification policy.
        if (endpoints.Count != 1 || endpoints[0].Name != "health" || endpoints[0].Uri != endpoint ||
            configuration.GetSectionWithFallBack("HealthChecksUI", "HealthChecks-UI").GetValue<bool>("NotifyUnHealthyOneTimeUntilChange"))
        {
            await httpCollector().Collect(cancellationToken);
            return;
        }

        HealthCheckConfiguration target = endpoints[0];
        foreach (IHealthCheckCollectorInterceptor interceptor in interceptors)
        {
            await interceptor.OnCollectExecuting(target);
        }

        UIHealthReport report = UIHealthReport.CreateFrom(await healthCheckService.CheckHealthAsync(cancellationToken));
        HealthCheckExecution execution = await db.Executions
            .Include(item => item.Entries)
            .Include(item => item.History)
            .SingleOrDefaultAsync(item => item.Name == target.Name, cancellationToken);

        if (report.Status != UIHealthStatus.Healthy)
        {
            await notifier.NotifyDown(target.Name, report);
        }
        else if (execution is not null && execution.Status != UIHealthStatus.Healthy)
        {
            await notifier.NotifyWakeUp(target.Name);
        }

        DateTime now = DateTime.UtcNow;
        if (execution is null)
        {
            execution = new HealthCheckExecution
            {
                Name = target.Name,
                Uri = target.Uri,
                DiscoveryService = target.DiscoveryService,
                Status = report.Status,
                OnStateFrom = now,
                Entries = report.ToExecutionEntries(),
                History = []
            };
            db.Executions.Add(execution);
        }
        else
        {
            foreach (HealthCheckExecutionEntry entry in execution.Entries)
            {
                if (report.Entries.TryGetValue(entry.Name, out UIHealthReportEntry updated) &&
                    execution.Status != report.Status && entry.Status != updated.Status)
                {
                    execution.History.Add(new HealthCheckExecutionHistory
                    {
                        On = now,
                        Name = entry.Name,
                        Status = updated.Status,
                        Description = updated.Description
                    });
                }
            }

            if (execution.Status != report.Status) execution.OnStateFrom = now;
            execution.Status = report.Status;
            foreach (HealthCheckExecutionEntry updated in report.ToExecutionEntries())
            {
                HealthCheckExecutionEntry entry = execution.Entries.Find(item => item.Name == updated.Name);
                if (entry is null)
                {
                    execution.Entries.Add(updated);
                }
                else
                {
                    entry.Status = updated.Status;
                    entry.Description = updated.Description;
                    entry.Duration = updated.Duration;
                    entry.Tags = updated.Tags;
                }
            }

            for (int i = execution.Entries.Count - 1; i >= 0; i--)
            {
                HealthCheckExecutionEntry entry = execution.Entries[i];
                if (!report.Entries.ContainsKey(entry.Name))
                {
                    execution.Entries.RemoveAt(i);
                    db.HealthCheckExecutionEntries.Remove(entry);
                }
            }
        }

        execution.LastExecuted = now;
        await db.SaveChangesAsync(cancellationToken);
        foreach (IHealthCheckCollectorInterceptor interceptor in interceptors)
        {
            await interceptor.OnCollectExecuted(report);
        }
    }
}
