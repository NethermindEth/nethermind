// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat;

namespace Nethermind.Init.Steps;

[RunnerStepDependencies(
    dependencies: [typeof(InitializeBlockTree)],
    dependents: [typeof(InitializeBlockchain)]
)]
public class SweepFlatOrphanStorage(
    FlatStateActivationPolicy activationPolicy,
    IFlatDbConfig config,
    OrphanStorageSweep sweep,
    ILogManager logManager) : IStep
{
    private readonly ILogger _logger = logManager.GetClassLogger<SweepFlatOrphanStorage>();

    public Task Execute(CancellationToken cancellationToken)
    {
        if (!activationPolicy.ShouldTurnOnFlatDb()) return Task.CompletedTask;

        bool repair = !sweep.AlreadySwept || config.SweepOrphanStorage;
        if (!repair && !config.VerifyOrphanStorage) return Task.CompletedTask;

        if (config.Layout != FlatLayout.Flat)
        {
            if (_logger.IsWarn) _logger.Warn($"Flat orphan storage sweep skipped: it supports the {FlatLayout.Flat} layout only, this database uses {config.Layout}.");
            return Task.CompletedTask;
        }

        if (_logger.IsInfo) _logger.Info(repair
            ? "Flat orphan storage sweep starting: every storage slot is checked against its account, and slots of accounts that are absent or have an empty storage root are deleted. This runs once per database and can take a while on a large state."
            : "Flat orphan storage check starting: every storage slot is checked against its account; nothing is deleted.");
        long started = Stopwatch.GetTimestamp();
        OrphanStorageReport report = sweep.Sweep(repair, cancellationToken);
        string outcome = $"{report.SlotsScanned:N0} slots scanned, {report.OrphanSlots:N0} orphaned slots under {report.OrphanAccounts:N0} accounts ({report.MissingAccounts:N0} absent, {report.EmptyRootAccounts:N0} with an empty storage root) in {Stopwatch.GetElapsedTime(started)}.";
        if (repair)
        {
            if (_logger.IsInfo) _logger.Info($"Flat orphan storage sweep done, orphaned slots deleted: {outcome}");
        }
        else if (report.OrphanAccounts > 0)
        {
            if (_logger.IsWarn) _logger.Warn($"Flat orphan storage check FAILED: {outcome} Set FlatDb.SweepOrphanStorage to delete them on the next start.");
        }
        else if (_logger.IsInfo) _logger.Info($"Flat orphan storage check passed: {outcome}");

        return Task.CompletedTask;
    }
}
