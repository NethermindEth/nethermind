// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Autofac;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Config;
using Nethermind.Logging;
using Nethermind.StateDiffArchive.Merging;

namespace Nethermind.StateDiffArchive;

/// <summary>
/// Records the committed per-block state diff to a compressed archive and, on a separate node, replays it
/// to fast-forward state without re-executing transactions. Can also merge several disjoint-range archives.
/// </summary>
/// <remarks>
/// Development/operations use only: the recorder and replayer wrap the main world-state scope and block
/// processor, so this plugin must not be enabled on a validating node.
/// </remarks>
public class StateDiffArchivePlugin(IStateDiffArchiveConfig config) : INethermindPlugin
{
    public string Name => "StateDiffArchive";
    public string Description => "Records committed per-block state diffs and replays them without the EVM";
    public string Author => "Nethermind";

    public bool Enabled => config.RecordingEnabled || config.ReplayEnabled || !string.IsNullOrWhiteSpace(config.MergeSources);

    public IModule Module => new StateDiffArchiveModule(config);

    public Task Init(INethermindApi nethermindApi)
    {
        if (!string.IsNullOrWhiteSpace(config.MergeSources))
        {
            RunMerge(nethermindApi);
        }
        return Task.CompletedTask;
    }

    private void RunMerge(INethermindApi nethermindApi)
    {
        ILogManager logManager = nethermindApi.Context.Resolve<ILogManager>();
        ILogger logger = logManager.GetClassLogger<StateDiffArchivePlugin>();
        IInitConfig initConfig = nethermindApi.Context.Resolve<IInitConfig>();
        IProcessExitSource exitSource = nethermindApi.Context.Resolve<IProcessExitSource>();

        string[] sources = config.MergeSources!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string output = config.ArchivePath.GetApplicationResourcePath(initConfig.BaseDbPath);

        if (logger.IsInfo) logger.Info($"StateDiffArchive: merging {sources.Length} source archives into {output}.");
        StateDiffMerger.Merge(sources, output, logger);

        exitSource.Exit(ExitCodes.Ok);
    }
}
