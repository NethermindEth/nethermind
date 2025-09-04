// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;

namespace Nethermind.ContractSearch.Plugin;

public class ContractSearchPlugin(IContractSearchConfig contractSearchConfig) : INethermindPlugin
{
    public string Name => "ContractSearch";
    public string Description => "Search smart contracts for bytecode patterns.";
    public string Author => "Nethermind";
    private INethermindApi _api = null!;
    private IContractSearchConfig _config = null!;
    private ILogManager _logManager = null!;
    private ILogger _logger;
    public bool Enabled => contractSearchConfig.Enabled == true;

    private IContractSearchRpcModule? _rpcModule;

    public Task Init(INethermindApi nethermindApi)
    {
        _api = nethermindApi;
        _logManager = _api.LogManager;
        _config = _api.Config<IContractSearchConfig>();
        _logger = _logManager.GetClassLogger<ContractSearchPlugin>();
        return Task.CompletedTask;
    }

    public Task InitRpcModules()
    {
        if (Enabled)
        {
            if (_logger.IsInfo) _logger.Info("Setting up contract search plugin");

            _rpcModule = new ContractSearchRpcModule(_api.BlockTree!, _api.WorldStateManager!, _logManager);
            _api.RpcModuleProvider!.Register(new SingletonModulePool<IContractSearchRpcModule>(_rpcModule, true));
        }

        return Task.CompletedTask;
    }

    ValueTask IAsyncDisposable.DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
