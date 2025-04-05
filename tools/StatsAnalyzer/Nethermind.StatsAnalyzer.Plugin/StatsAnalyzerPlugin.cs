// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Core;
using Nethermind.Core.Resettables;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.PatternAnalyzer.Plugin.Analyzer.Call;
using Nethermind.PatternAnalyzer.Plugin.Analyzer.Pattern;
using Nethermind.PatternAnalyzer.Plugin.Types;
using Nethermind.StatsAnalyzer.Plugin.Tracer.Call;
using PatternAnalyzerFileTracer = Nethermind.StatsAnalyzer.Plugin.Tracer.Pattern.PatternAnalyzerFileTracer;

namespace Nethermind.StatsAnalyzer.Plugin;

public class StatsAnalyzerPlugin(IPatternAnalyzerConfig patternAnalyzerConfig, ICallAnalyzerConfig callAnalyzerConfig)
    : INethermindPlugin
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private INethermindApi _api = null!;
    private ILogger _logger;
    private ILogManager _logManager = null!;
    public string Name => "StatsAnalyzer";
    public string Description => "Allows to serve traces of stats over blocks, by saving them to a file.";
    public string Author => "Nethermind";

    public bool Enabled => patternAnalyzerConfig.Enabled || callAnalyzerConfig.Enabled;

    public Task Init(INethermindApi nethermindApi)
    {
        _api = nethermindApi;
        _logManager = _api.LogManager;
        _logger = _logManager.GetClassLogger<StatsAnalyzerPlugin>();
        return Task.CompletedTask;
    }

    public Task InitNetworkProtocol()
    {
        if (Enabled)
        {
            if (_logger.IsInfo) _logger.Info("Setting up OpcodeStats tracer");

            if (patternAnalyzerConfig.Enabled) SetupPatternAnalyzer();
            if (callAnalyzerConfig.Enabled) SetupCallAnalyzer();
        }

        return Task.CompletedTask;
    }

    ValueTask IAsyncDisposable.DisposeAsync()
    {
        _cancellationTokenSource.Cancel();
        _cancellationTokenSource.Dispose();
        return ValueTask.CompletedTask;
    }

    private void SetupCallAnalyzer()
    {
        if (_logger.IsInfo) _logger.Info("Setting up OpcodeStats tracer");

        var analyzer = new CallStatsAnalyzer(callAnalyzerConfig.TopN);
        CallAnalyzerFileTracer callAnalyzerFileTracer = new(new ResettableList<Address>(),
            callAnalyzerConfig.ProcessingQueueSize,
            analyzer,
            _api.FileSystem, _logger,
            callAnalyzerConfig.WriteFrequency, ProcessingModeParser.Parse(callAnalyzerConfig.ProcessingMode),
            SortOrderParser.Parse(callAnalyzerConfig.Sort),
            callAnalyzerConfig.File!, _cancellationTokenSource.Token);
        _api.MainProcessingContext!.BlockchainProcessor!.Tracers.Add(callAnalyzerFileTracer);
    }

    private void SetupPatternAnalyzer()
    {
        if (_logger.IsInfo) _logger.Info("Setting up OpcodeStats tracer");

        var analyzer = new PatternStatsAnalyzer(patternAnalyzerConfig.GetStatsAnalyzerConfig());
        PatternAnalyzerFileTracer patternAnalyzerFileTracer = new(new ResettableList<Instruction>(),
            patternAnalyzerConfig.ProcessingQueueSize,
            patternAnalyzerConfig.InstructionsQueueSize, analyzer, patternAnalyzerConfig.GetIgnoreSet(),
            _api.FileSystem, _logger,
            patternAnalyzerConfig.WriteFrequency, ProcessingModeParser.Parse(patternAnalyzerConfig.ProcessingMode),
            SortOrderParser.Parse(patternAnalyzerConfig.Sort),
            patternAnalyzerConfig.File!, _cancellationTokenSource.Token);
        _api.MainProcessingContext!.BlockchainProcessor!.Tracers.Add(patternAnalyzerFileTracer);
    }
}
