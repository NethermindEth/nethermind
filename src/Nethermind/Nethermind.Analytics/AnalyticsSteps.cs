// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Analytics;
using Nethermind.Api.Steps;
using Nethermind.Core.PubSub;
using Nethermind.Init.Steps;
using Nethermind.Sockets;
using Nethermind.TxPool;

/// <summary>
/// Publish txpool `NewDiscovered` event to all `IPublisher`.
/// </summary>
/// <param name="txPool"></param>
/// <param name="webSocketsModule"></param>
/// <param name="webSocketsManager"></param>
/// <param name="analyticsConfig"></param>
/// <param name="publishers"></param>
[RunnerStepDependencies(typeof(InitializeBlockchain))]
public class AnalyticsSteps(
    ITxPool txPool,
    AnalyticsWebSocketsModule webSocketsModule,
    IWebSocketsManager webSocketsManager,
    IAnalyticsConfig analyticsConfig,
    IPublisher[] publishers
) : IStep
{
    public Task Execute(CancellationToken cancellationToken)
    {
        txPool.NewDiscovered += TxPoolOnNewDiscovered;
        webSocketsManager!.AddModule(webSocketsModule, true);
        return Task.CompletedTask;
    }

    private void TxPoolOnNewDiscovered(object sender, TxEventArgs e)
    {
        if (analyticsConfig.StreamTransactions)
        {
            foreach (IPublisher publisher in publishers)
            {
                // TODO: probably need to serialize first
                publisher.PublishAsync(e.Transaction);
            }
        }
    }
}
