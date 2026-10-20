// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Nodes;
using Nethermind.TestSelection.GitHub;
using Nethermind.TestSelection.Journal;

namespace Nethermind.TestSelection.Collection;

/// <summary>The failures of one finished run, read from its failed jobs and their logs.</summary>
public sealed class RunFailures(ActionsApi actions)
{
    public async Task<Failures> ReadAsync(JsonNode run, CancellationToken cancellation)
    {
        if (run["conclusion"]!.GetValue<string>() != "failure")
        {
            return Failures.None;
        }

        List<FailedJob> failed = [.. (await actions.JobsAsync(run["id"]!.GetValue<long>(), cancellation))
            .Where(static job => job["conclusion"]?.GetValue<string>() == "failure")
            .Select(static job => new FailedJob(job["id"]!.GetValue<long>(), job["name"]!.GetValue<string>()))
            .Where(static job => JobLogs.UnitOf(job.Name) is not null)];
        return await FailureClassifier.ClassifyAsync(failed, job => actions.JobLogAsync(job.Id, cancellation));
    }
}
