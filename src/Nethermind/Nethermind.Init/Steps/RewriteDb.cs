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
    public Task Execute(CancellationToken cancellationToken) =>
        rewriter.Rewrite(GetDbName(commandSelections, initConfig, "rewrite-db"), cancellationToken);

    /// <summary>Reads the single database name a DB maintenance command takes.</summary>
    /// <exception cref="InvalidConfigurationException">The command line does not give exactly one argument.</exception>
    internal static string GetDbName(IEnumerable<StepCommandSelection> commandSelections, IInitConfig initConfig, string command)
    {
        // Resolvable without a command line, so the container can build it even when nothing selected it by name.
        IReadOnlyList<string> arguments = commandSelections.SingleOrDefault()?.Arguments ?? [];
        return arguments.Count == 1
            ? arguments[0]
            : throw new InvalidConfigurationException(
                $"{command} takes exactly one database name, the directory under {initConfig.BaseDbPath}, e.g. `{command} receipts`.",
                ExitCodes.ForbiddenOptionValue);
    }
}
