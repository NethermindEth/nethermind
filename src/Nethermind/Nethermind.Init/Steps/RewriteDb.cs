// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Config;
using Nethermind.Core.Exceptions;
using Nethermind.Db.Rocks;

namespace Nethermind.Init.Steps;

[StepCommand("rewrite-db", "Rewrite a database with the current RocksDB configuration, e.g. `rewrite-db receipts`.")]
[RunnerStepDependencies(typeof(StartMonitoring))]
public class RewriteDb(
    IEnumerable<StepCommandSelection> commandSelections,
    IInitConfig initConfig,
    RocksDbRewriter rewriter) : IStep
{
    /// <inheritdoc/>
    /// <exception cref="InvalidConfigurationException">
    /// The arguments do not name exactly one existing RocksDB database, or the rewrite cannot proceed safely.
    /// </exception>
    public Task Execute(CancellationToken cancellationToken)
    {
        // Resolvable without a command line, so the container can build it even when nothing selected it by name.
        IReadOnlyList<string> arguments = commandSelections.SingleOrDefault()?.Arguments ?? [];
        if (arguments.Count != 1)
            throw new InvalidConfigurationException(
                $"rewrite-db takes exactly one database name, the directory under {initConfig.BaseDbPath}, e.g. `rewrite-db receipts`.",
                ExitCodes.ForbiddenOptionValue);

        return rewriter.Rewrite(arguments[0], cancellationToken);
    }
}
