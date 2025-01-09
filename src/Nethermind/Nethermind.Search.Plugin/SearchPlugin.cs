
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
using System.IO.Abstractions;

namespace Nethermind.Search.Plugin;
public class SearchPlugin : INethermindPlugin
{
    public string Name => "Search";
    public string Description => "";
    public string Author => "Nethermind";
    private INethermindApi _api = null!;
    private ISearchConfig _config = null!;
    private ILogManager _logManager = null!;
    private ILogger _logger;
    private bool Enabled => _config?.Enabled == true;

    public Task Init(INethermindApi nethermindApi)
    {
        _api = nethermindApi;
        _logManager = _api.LogManager;
        _config = _api.Config<ISearchConfig>();
        _logger = _logManager.GetClassLogger<SearchPlugin>();
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
