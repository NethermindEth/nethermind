// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Flashbots.Handlers;
using Nethermind.Flashbots.Modules.Flashbots;
using Nethermind.Consensus.Processing;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.Flashbots;

public class Flashbots : INethermindPlugin
{
    private INethermindApi _api = null!;

    private IFlashbotsConfig _flashbotsConfig = null!;

    private IJsonRpcConfig _jsonRpcConfig = null!;

    public virtual string Name => "Flashbots";
    public virtual string Description => "Flashbots";
    public string Author => "Nethermind";
    public Task InitRpcModules()
    {
        ReadOnlyTxProcessingEnv readOnlyTxProcessingEnv = new ReadOnlyTxProcessingEnv(
            _api.WorldStateManager ?? throw new ArgumentNullException(nameof(_api.WorldStateManager)),
            _api.BlockTree ?? throw new ArgumentNullException(nameof(_api.BlockTree)),
            _api.SpecProvider,
            _api.LogManager
        );
        ValidateSubmissionHandler validateSubmissionHandler = new ValidateSubmissionHandler(
            _api.HeaderValidator ?? throw new ArgumentNullException(nameof(_api.HeaderValidator)),
            _api.BlockValidator ?? throw new ArgumentNullException(nameof(_api.BlockValidator)),
            readOnlyTxProcessingEnv,
            _flashbotsConfig
        );

        ModuleFactoryBase<IFlashbotsRpcModule> flashbotsRpcModule = new FlashbotsRpcModuleFactory(validateSubmissionHandler);

        ArgumentNullException.ThrowIfNull(_api.RpcModuleProvider);
        _api.RpcModuleProvider.RegisterBounded(flashbotsRpcModule,
            _jsonRpcConfig.EthModuleConcurrentInstances ?? Environment.ProcessorCount, _jsonRpcConfig.Timeout);

        return Task.CompletedTask;
    }

    public Task Init(INethermindApi api)
    {
        _api = api;
        _flashbotsConfig = api.Config<IFlashbotsConfig>();
        _jsonRpcConfig = api.Config<IJsonRpcConfig>();
        if (_flashbotsConfig.Enabled)
        {
            _jsonRpcConfig.EnabledModules = _jsonRpcConfig.EnabledModules.Append(ModuleType.Flashbots).ToArray();
        }
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
