// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Logging;

namespace Nethermind.ContractSearch.Plugin;

public class ContractSearchPlugin : INethermindPlugin
{
    public string Name => "ContractSearch";
    public string Description => "Search smart contracts for magic numbers";
    public string Author => "Nethermind";
    private INethermindApi _api = null!;
    private IContractSearchConfig _config = null!;
    private ILogManager _logManager = null!;
    private ILogger _logger;
    private bool Enabled => _config?.Enabled == true;

    public Task Init(INethermindApi nethermindApi)
    {
        _api = nethermindApi;
        _logManager = _api.LogManager;
        _config = _api.Config<IContractSearchConfig>();
        _logger = _logManager.GetClassLogger<ContractSearchPlugin>();
        return Task.CompletedTask;
    }

    public Task InitNetworkProtocol()
    {
        if (Enabled)
        {
            if (_logger.IsInfo) _logger.Info($"Setting up search plugin");

            // Setup Search
        }

        return Task.CompletedTask;
    }

    ValueTask IAsyncDisposable.DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
