// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Core.Exceptions;
using Nethermind.Db.Rocks;

namespace Nethermind.Init.Steps;

[StepCommand("compact-db", "Force a compaction of every range of a database with the current RocksDB configuration, e.g. `compact-db receipts`.")]
[RunnerStepDependencies(typeof(StartMonitoring))]
public class CompactDb(
    IEnumerable<StepCommandSelection> commandSelections,
    IInitConfig initConfig,
    RocksDbRewriter rewriter) : IStep
{
    /// <inheritdoc/>
    /// <exception cref="InvalidConfigurationException">The arguments do not name exactly one existing RocksDB database.</exception>
    public Task Execute(CancellationToken cancellationToken)
    {
        rewriter.Compact(RewriteDb.GetDbName(commandSelections, initConfig, "compact-db"), cancellationToken);
        return Task.CompletedTask;
    }
}
