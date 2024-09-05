// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.BlockValidation.Handlers;
using Nethermind.Consensus.Processing;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.BlockValidation;

public class BlockValidation : INethermindPlugin
{
    private INethermindApi _api = null!;

    private IBlockValidationConfig _blockValidationConfig = null!;

    public virtual string Name => "BlockValidation";
    public virtual string Description => "BlockValidation";
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
            _api.BlockValidator ?? throw new ArgumentNullException(nameof(_api.BlockValidator)),
            readOnlyTxProcessingEnv,
            _api.GasLimitCalculator ?? throw new ArgumentNullException(nameof(_api.GasLimitCalculator)),
            _blockValidationConfig
        );
        IFlashbotsRpcModule flashbotsRpcModule = new FlashbotsRpcModule(validateSubmissionHandler);

        ArgumentNullException.ThrowIfNull(_api.RpcModuleProvider);
        _api.RpcModuleProvider.RegisterSingle(flashbotsRpcModule);

        return Task.CompletedTask;
    }

    public Task Init(INethermindApi api)
    {
        _api = api;
        _blockValidationConfig = api.Config<IBlockValidationConfig>();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
