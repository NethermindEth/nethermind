// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Logging;
using System.IO.Abstractions;
using Nethermind.PatternAnalyzer.Plugin.Tracer;
using Nethermind.PatternAnalyzer.Plugin.Analyzer;

namespace Nethermind.PatternAnalyzer.Plugin;

public class PatternAnalyzer : INethermindPlugin
{
    public string Name => "OpcodeStats";
    public string Description => "Allows to serve traces of n-gram stats over blocks, by saving them to a file.";
    public string Author => "Nethermind";
    private INethermindApi _api = null!;
    private IPatternAnalyzerConfig _config = null!;
    private ILogManager _logManager = null!;
    private ILogger _logger;
    private bool Enabled => _config?.Enabled == true;

    bool INethermindPlugin.Enabled => throw new NotImplementedException();

    public Task Init(INethermindApi nethermindApi)
    {
        _api = nethermindApi;
        _logManager = _api.LogManager;
        _config = _api.Config<IPatternAnalyzerConfig>();
        _logger = _logManager.GetClassLogger<PatternAnalyzer>();
        return Task.CompletedTask;
    }

    public Task InitNetworkProtocol()
    {
        if (Enabled)
        {
            if (_logger.IsInfo) _logger.Info($"Setting up OpcodeStats tracer");

            // Setup tracing
            var analyzer = new StatsAnalyzer(_config.GetStatsAnalyzerConfig());
            PatternAnalyzerFileTracer patternAnalyzerFileTracer = new(_config.ProcessingQueueSize, _config.InstructionsQueueSize, analyzer, _config.GetIgnoreSet(),  new FileSystem(), _logger,_config.WriteFrequency, _config.File);
            _api.MainProcessingContext!.BlockchainProcessor!.Tracers.Add(patternAnalyzerFileTracer);


        }

        return Task.CompletedTask;
    }

    ValueTask IAsyncDisposable.DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
