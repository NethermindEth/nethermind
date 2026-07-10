// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using Autofac;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Resettables;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.StatsAnalyzer.Plugin.Analyzer;
using Nethermind.StatsAnalyzer.Plugin.Analyzer.Call;
using Nethermind.StatsAnalyzer.Plugin.Analyzer.Pattern;
using Nethermind.StatsAnalyzer.Plugin.Types;
using Nethermind.StatsAnalyzer.Plugin.Tracer.Call;
using PatternAnalyzerFileTracer = Nethermind.StatsAnalyzer.Plugin.Tracer.Pattern.PatternAnalyzerFileTracer;

namespace Nethermind.StatsAnalyzer.Plugin;

public class StatsAnalyzerPlugin(IPatternAnalyzerConfig patternAnalyzerConfig, ICallAnalyzerConfig callAnalyzerConfig)
    : INethermindPlugin, IAsyncDisposable
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private INethermindApi _api = null!;
    private IBlocksConfig _blocksConfig = null!;
    private ILogger _logger;
    private ILogManager _logManager = null!;
    public string Name => "StatsAnalyzer";
    public string Description => "Allows to serve traces of stats over blocks, by saving them to a file.";
    public string Author => "Nethermind";

    public bool Enabled => patternAnalyzerConfig.Enabled || callAnalyzerConfig.Enabled;

    public IModule Module => new StatsAnalyzerPluginModule(patternAnalyzerConfig, callAnalyzerConfig, _cancellationTokenSource.Token);

    public Task Init(INethermindApi nethermindApi)
    {
        _api = nethermindApi;
        _logManager = _api.LogManager;
        _logger = _logManager.GetClassLogger<StatsAnalyzerPlugin>();
        _blocksConfig = _api.Config<IBlocksConfig>();

        // Analyzers share mutable state across transactions and are unsafe
        // under parallel BAL execution; the block tracer skips recording on
        // such blocks, so warn once that coverage will be partial.
        if (Enabled && _blocksConfig.ParallelExecution && _logger.IsWarn)
        {
            _logger.Warn(
                "Blocks.ParallelExecution=true: StatsAnalyzer Pattern/Call analyzers " +
                "will skip recording on blocks executed with parallel BAL. " +
                "Pre-Amsterdam and sequentially-executed blocks will still be recorded. " +
                "Set Blocks.ParallelExecution=false to capture every block. " +
                "See tools/StatsAnalyzer/EIP-7928-references.md for details.");
        }

        return Task.CompletedTask;
    }

    ValueTask IAsyncDisposable.DisposeAsync()
    {
        _cancellationTokenSource.Cancel();
        _cancellationTokenSource.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class StatsAnalyzerPluginModule(
        IPatternAnalyzerConfig patternAnalyzerConfig,
        ICallAnalyzerConfig callAnalyzerConfig,
        CancellationToken cancellationToken) : Module
    {
        protected override void Load(ContainerBuilder builder) => builder
            .AddSingleton<IMainProcessingModule>(
                new StatsAnalyzerMainProcessingModule(patternAnalyzerConfig, callAnalyzerConfig, cancellationToken));
    }

    // Contributes the Call/Pattern analyzer block tracers to the main block processor only.
    private sealed class StatsAnalyzerMainProcessingModule(
        IPatternAnalyzerConfig patternAnalyzerConfig,
        ICallAnalyzerConfig callAnalyzerConfig,
        CancellationToken cancellationToken) : Module, IMainProcessingModule
    {
        protected override void Load(ContainerBuilder builder)
        {
            if (patternAnalyzerConfig.Enabled) builder.AddSingleton<IBlockTracer>(CreatePatternTracer);
            if (callAnalyzerConfig.Enabled) builder.AddSingleton<IBlockTracer>(CreateCallTracer);
        }

        private IBlockTracer CreateCallTracer(IComponentContext ctx)
        {
            ILogger logger = ctx.Resolve<ILogManager>().GetClassLogger<StatsAnalyzerPlugin>();
            if (logger.IsInfo) logger.Info("Setting up Call Analyzer tracer");

            CallStatsAnalyzer analyzer = new(callAnalyzerConfig.TopN);
            return new CallAnalyzerFileTracer(
                [],
                callAnalyzerConfig.ProcessingQueueSize,
                analyzer,
                ctx.Resolve<IFileSystem>(),
                logger,
                callAnalyzerConfig.WriteFrequency,
                (ProcessingMode)Enum.Parse(typeof(ProcessingMode), callAnalyzerConfig.ProcessingMode, ignoreCase: true),
                (SortOrder)Enum.Parse(typeof(SortOrder), callAnalyzerConfig.Sort, ignoreCase: true),
                callAnalyzerConfig.File!,
                cancellationToken,
                ctx.Resolve<IBlocksConfig>());
        }

        private IBlockTracer CreatePatternTracer(IComponentContext ctx)
        {
            ILogger logger = ctx.Resolve<ILogManager>().GetClassLogger<StatsAnalyzerPlugin>();
            if (logger.IsInfo) logger.Info("Setting up Pattern Analyzer tracer");

            PatternStatsAnalyzer analyzer = new(patternAnalyzerConfig.GetStatsAnalyzerConfig());
            return new PatternAnalyzerFileTracer(
                [],
                patternAnalyzerConfig.ProcessingQueueSize,
                patternAnalyzerConfig.InstructionsQueueSize,
                analyzer,
                patternAnalyzerConfig.GetIgnoreSet(),
                ctx.Resolve<IFileSystem>(),
                logger,
                patternAnalyzerConfig.WriteFrequency,
                (ProcessingMode)Enum.Parse(typeof(ProcessingMode), patternAnalyzerConfig.ProcessingMode, ignoreCase: true),
                (SortOrder)Enum.Parse(typeof(SortOrder), patternAnalyzerConfig.Sort, ignoreCase: true),
                patternAnalyzerConfig.File!,
                cancellationToken,
                ctx.Resolve<IBlocksConfig>());
        }
    }
}
