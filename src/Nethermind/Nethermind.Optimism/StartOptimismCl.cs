// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.Optimism.CL;

namespace Nethermind.Optimism;

/// <summary>
/// Starts the Optimism Consensus Layer driver. Registered only when <see cref="IOptimismConfig.ClEnabled"/> is set,
/// so <see cref="OptimismCL"/> and its component graph are guaranteed to be present in the container.
/// </summary>
/// <remarks>
/// <see cref="OptimismCL"/> is a long-running background service; it is started fire-and-forget here and disposed by
/// the container. A step is used (rather than an activation hook) so the driver only starts once the blockchain,
/// block producer and network are initialized.
/// </remarks>
[RunnerStepDependencies(typeof(InitializeBlockchain), typeof(InitializeBlockProducer), typeof(InitializeNetwork))]
public class StartOptimismCl(OptimismCL optimismCl, ILogManager logManager) : IStep
{
    public Task Execute(CancellationToken cancellationToken)
    {
        _ = optimismCl.Start(); // NOTE: Fire and forget, exception handling must be done inside `Start`

        ILogger logger = logManager.GetClassLogger<StartOptimismCl>();
        if (logger.IsInfo) logger.Info("Optimism CL has been enabled and started");

        return Task.CompletedTask;
    }
}
