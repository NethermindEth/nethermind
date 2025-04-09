// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Logging;

namespace Nethermind.Hive;

public class HivePlugin(IHiveConfig hiveConfig) : INethermindPlugin
{
    private INethermindApi _api = null!;
    private ILogger _logger;
    private readonly CancellationTokenSource _disposeCancellationToken = new();

    public ValueTask DisposeAsync()
    {
        _disposeCancellationToken.Cancel();
        _disposeCancellationToken.Dispose();
        return ValueTask.CompletedTask;
    }

    public string Name => "Hive";

    public string Description => "Plugin used for executing Hive Ethereum Tests";

    public string Author => "Nethermind";

    public Task Init(INethermindApi api)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _logger = _api.LogManager.GetClassLogger();

        return Task.CompletedTask;
    }

    public async Task InitNetworkProtocol()
    {
        ArgumentNullException.ThrowIfNull(_api.BlockTree);
        ArgumentNullException.ThrowIfNull(_api.BlockProcessingQueue);
        ArgumentNullException.ThrowIfNull(_api.ConfigProvider);
        ArgumentNullException.ThrowIfNull(_api.LogManager);
        ArgumentNullException.ThrowIfNull(_api.FileSystem);
        ArgumentNullException.ThrowIfNull(_api.BlockValidator);

        _api.TxPool!.AcceptTxWhenNotSynced = true;

        _api.TxGossipPolicy.Policies.Clear();

        HiveRunner hiveRunner = new(
            _api.BlockTree,
            _api.BlockProcessingQueue,
            _api.ConfigProvider,
            _api.LogManager.GetClassLogger(),
            _api.FileSystem,
            _api.BlockValidator
        );

        if (_logger.IsInfo) _logger.Info("Hive is starting");

        await hiveRunner.Start(_disposeCancellationToken.Token);
    }

    public Task InitRpcModules()
    {
        return Task.CompletedTask;
    }

    public bool Enabled => hiveConfig.Enabled;
}
