// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Config;
using Nethermind.Logging;

namespace Nethermind.Fuzzer.Plugin;

public class FuzzerPlugin(IInitConfig initConfig, IFuzzerConfig config) : INethermindPlugin
{
    private readonly IInitConfig _initConfig = initConfig;
    private readonly IFuzzerConfig _config = config;
    private ILogger _logger = NullLogger.Instance;
    private FuzzerRuntime? _runtime;

    public string Name => "Fuzzer";
    public string Description => "Terminates Nethermind when new log entries appear after the configured readiness threshold.";
    public string Author => "Nethermind";

    public bool Enabled => _config.Enabled;

    public async Task Init(INethermindApi nethermindApi)
    {
        if (!Enabled)
        {
            return;
        }

        _logger = nethermindApi.LogManager.GetClassLogger<FuzzerPlugin>();
        IProcessExitSource processExit = nethermindApi.ProcessExit ?? throw new InvalidOperationException("Process exit source not available.");
        _runtime = new FuzzerRuntime(_config, _initConfig, _logger, processExit);

        if (!_runtime.AttachToLoggingPipeline())
        {
            if (_logger.IsWarn)
            {
                _logger.Warn("Fuzzer plugin failed to attach to the logging pipeline and will remain inactive.");
            }
        }

        await Task.CompletedTask;
    }
}
