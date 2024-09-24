// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
using System;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Core;
using Nethermind.JsonRpc.Client;
using Nethermind.JsonRpc;
using Nethermind.Network;
using Nethermind.Consensus;
using Nethermind.KeyStore.Config;
using System.Configuration;
using Nethermind.Logging;
using Nethermind.Evm.CodeAnalysis.StatsAnalyzer;
using Nethermind.Evm.Tracing.OpcodeStats;
using System.IO.Abstractions;


namespace Nethermind.OpcodeStats.Plugin;
public class OpcodeStatsPlugin : INethermindPlugin
{
    public string Name => "OpcodeStats";
    public string Description => "Allows to serve traces of n-gram stats over blocks, by saving them to a file.";
    public string Author => "Nethermind";
    private INethermindApi _api = null!;
    private IStatsConfig _config = null!;
    private ILogManager _logManager = null!;
    private ILogger _logger;
    private bool Enabled => _config?.Enabled == true;

    public Task Init(INethermindApi nethermindApi)
    {
        _api = nethermindApi;
        _logManager = _api.LogManager;
        _config = _api.Config<IStatsConfig>();
        _logger = _logManager.GetClassLogger<OpcodeStatsPlugin>();
        return Task.CompletedTask;
    }

    public Task InitNetworkProtocol()
    {
        if (Enabled)
        {
            if (_logger.IsInfo) _logger.Info($"Setting up OpcodeStats tracer");

            // Setup tracing
            var analyzer = new StatsAnalyzer(_config.GetStatsAnalyzerConfig());
            OpcodeStatsFileTracer opcodeStatsFileTracer = new(_config.ProcessingQueueSize, _config.InstructionsQueueSize, analyzer, new FileSystem(), _logger, _config.File);
            _api.BlockchainProcessor!.Tracers.Add(opcodeStatsFileTracer);
        }

        return Task.CompletedTask;
    }

    ValueTask IAsyncDisposable.DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
