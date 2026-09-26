// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;

namespace Nethermind.Runner.Ethereum;

/// <summary>Completes optional startup work and cleanup before RPC starts.</summary>
internal static class StartupWarmupTask
{
    internal static readonly TimeSpan SafetyTimeout = TimeSpan.FromSeconds(30);

    internal static async Task RunAsync(Func<CancellationToken, Task> warmup, ILogger logger, TimeSpan budget, CancellationToken cancellationToken)
    {
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Container construction and JIT compilation can run synchronously before the first await.
        Task work = Task.Run(async () =>
        {
            try
            {
                cancellation.Token.ThrowIfCancellationRequested();
                await warmup(cancellation.Token);
                if (!cancellation.IsCancellationRequested && logger.IsInfo)
                    logger.Info("Startup payload pipeline warmup complete.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                if (logger.IsDebug) logger.Debug("Startup payload pipeline warmup cancelled.");
            }
            catch (Exception exception)
            {
                if (logger.IsWarn) logger.Warn($"Startup payload pipeline warmup failed; RPC startup will continue. {exception}");
            }
        });
        try
        {
            await work.WaitAsync(budget, cancellationToken);
        }
        catch (TimeoutException)
        {
            await cancellation.CancelAsync();
            if (logger.IsWarn) logger.Warn($"Startup payload pipeline warmup exceeded its {budget.TotalSeconds:g} second budget; waiting for warmup cleanup before RPC startup.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await cancellation.CancelAsync();
            throw;
        }
        finally
        {
            await work;
        }
    }
}
