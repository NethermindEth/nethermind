// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Init.Steps;
using Nethermind.JsonRpc.Modules;
using Nethermind.Consensus;
using Nethermind.Init.Steps.Migrations;

namespace Nethermind.Taiko.Rpc;

public class RegisterTaikoRpcModules : RegisterRpcModules
{
    private readonly TaikoNethermindApi _api;
    private readonly IPoSSwitcher _poSSwitcher;

    public RegisterTaikoRpcModules(INethermindApi api, IPoSSwitcher poSSwitcher) : base(api, poSSwitcher)
    {
        _api = (TaikoNethermindApi)api;
        _poSSwitcher = poSSwitcher;
    }

    protected override void RegisterProofRpcModule(IRpcModuleProvider rpcModuleProvider)
    {
        StepDependencyException.ThrowIfNull(_api.WorldStateManager);
        StepDependencyException.ThrowIfNull(_api.BlockTree);
        StepDependencyException.ThrowIfNull(_api.ReceiptFinder);
        StepDependencyException.ThrowIfNull(_api.SpecProvider);
        TaikoProofModuleFactory proofModuleFactory = new(
            _api.WorldStateManager,
            _api.ReadOnlyTxProcessingEnvFactory,
            _api.BlockTree,
            _api.BlockPreprocessor,
            _api.ReceiptFinder,
            _api.SpecProvider,
            _api.LogManager);
        rpcModuleProvider.RegisterBounded(proofModuleFactory, 2, JsonRpcConfig.Timeout);
    }

    protected override void RegisterTraceRpcModule(IRpcModuleProvider rpcModuleProvider)
    {
        StepDependencyException.ThrowIfNull(_api.WorldStateManager);
        StepDependencyException.ThrowIfNull(_api.DbProvider);
        StepDependencyException.ThrowIfNull(_api.BlockTree);
        StepDependencyException.ThrowIfNull(_api.ReceiptStorage);
        StepDependencyException.ThrowIfNull(_api.RewardCalculatorSource);
        StepDependencyException.ThrowIfNull(_api.SpecProvider);

        IBlocksConfig blockConfig = _api.Config<IBlocksConfig>();
        ulong secondsPerSlot = blockConfig.SecondsPerSlot;

        TaikoTraceModuleFactory traceModuleFactory = new(
            _api.WorldStateManager,
            _api.BlockTree.AsReadOnly(),
            JsonRpcConfig,
            _api.CreateBlockchainBridge(),
            secondsPerSlot,
            _api.BlockPreprocessor,
            _api.RewardCalculatorSource,
            _api.ReceiptStorage,
            _api.SpecProvider,
            _poSSwitcher,
            _api.LogManager);

        rpcModuleProvider.RegisterBoundedByCpuCount(traceModuleFactory, JsonRpcConfig.Timeout);
    }

    protected override void RegisterDebugRpcModule(IRpcModuleProvider rpcModuleProvider)
    {
        StepDependencyException.ThrowIfNull(_api.DbProvider);
        StepDependencyException.ThrowIfNull(_api.BlockPreprocessor);
        StepDependencyException.ThrowIfNull(_api.BlockValidator);
        StepDependencyException.ThrowIfNull(_api.RewardCalculatorSource);
        StepDependencyException.ThrowIfNull(_api.KeyStore);
        StepDependencyException.ThrowIfNull(_api.BadBlocksStore);
        StepDependencyException.ThrowIfNull(_api.WorldStateManager);
        StepDependencyException.ThrowIfNull(_api.BlockTree);
        StepDependencyException.ThrowIfNull(_api.ReceiptStorage);
        StepDependencyException.ThrowIfNull(_api.SpecProvider);

        TaikoDebugModuleFactory debugModuleFactory = new(
            _api.WorldStateManager,
            _api.DbProvider,
            _api.BlockTree,
            JsonRpcConfig,
            _api.CreateBlockchainBridge(),
            _api.Config<IBlocksConfig>().SecondsPerSlot,
            _api.BlockValidator,
            _api.BlockPreprocessor,
            _api.RewardCalculatorSource,
            _api.ReceiptStorage,
            new ReceiptMigration(_api),
            _api.ConfigProvider,
            _api.SpecProvider,
            _api.SyncModeSelector,
            _api.BadBlocksStore,
            _api.FileSystem,
            _api.LogManager);

        rpcModuleProvider.RegisterBoundedByCpuCount(debugModuleFactory, JsonRpcConfig.Timeout);
    }
}
