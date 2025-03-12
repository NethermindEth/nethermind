// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Logging;
using Nethermind.PatternAnalyzer.Plugin.Analyzer;
using Nethermind.PatternAnalyzer.Plugin.Tracer;

namespace Nethermind.PatternAnalyzer.Plugin;

public class PatternAnalyzer(IPatternAnalyzerConfig patternAnalyzerConfig) : INethermindPlugin
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private INethermindApi _api = null!;
    private ILogger _logger;
    private ILogManager _logManager = null!;
    public string Name => "OpcodeStats";
    public string Description => "Allows to serve traces of n-gram stats over blocks, by saving them to a file.";
    public string Author => "Nethermind";

    public bool Enabled => patternAnalyzerConfig.Enabled;

    public Task Init(INethermindApi nethermindApi)
    {
        _api = nethermindApi;
        _logManager = _api.LogManager;
        _logger = _logManager.GetClassLogger<PatternAnalyzer>();
        return Task.CompletedTask;
    }

    public Task InitNetworkProtocol()
    {
        if (Enabled)
        {
            if (_logger.IsInfo) _logger.Info("Setting up OpcodeStats tracer");

            // Setup tracing
            var analyzer = new StatsAnalyzer(patternAnalyzerConfig.GetStatsAnalyzerConfig());
            PatternAnalyzerFileTracer patternAnalyzerFileTracer = new(patternAnalyzerConfig.ProcessingQueueSize,
                patternAnalyzerConfig.InstructionsQueueSize, analyzer, patternAnalyzerConfig.GetIgnoreSet(),
                _api.FileSystem, _logger,
                patternAnalyzerConfig.WriteFrequency, SortOrderParser.Parse(patternAnalyzerConfig.Sort),
                patternAnalyzerConfig.File!, _cancellationTokenSource.Token);
            _api.MainProcessingContext!.BlockchainProcessor!.Tracers.Add(patternAnalyzerFileTracer);
        }

        return Task.CompletedTask;
    }

    ValueTask IAsyncDisposable.DisposeAsync()
    {
        _cancellationTokenSource.Cancel();
        _cancellationTokenSource.Dispose();
        return ValueTask.CompletedTask;
    }
}
