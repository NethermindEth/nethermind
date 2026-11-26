// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;

namespace Nethermind.Consensus.Producers
{
    public class BuildBlocksInALoop : IBlockProductionTrigger, IAsyncDisposable
    {
        private readonly CancellationTokenSource _loopCancellationTokenSource = new();
        private readonly Lock _loopTaskLock = new();
        private Task? _loopTask;
        private Task? _disposeTask;
        protected ILogger Logger { get; }

        public event EventHandler<BlockProductionEventArgs>? TriggerBlockProduction;

        public BuildBlocksInALoop(ILogManager logManager, bool autoStart = true)
        {
            Logger = logManager.GetClassLogger<BuildBlocksInALoop>();
            if (autoStart)
            {
                StartLoop();
            }
        }

        public void StartLoop()
        {
            lock (_loopTaskLock)
            {
                if (_disposeTask is not null) return;
                _loopTask ??= Task.Run(ProducerLoop, _loopCancellationTokenSource.Token).ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        if (Logger.IsError) Logger.Error($"Block producer encountered an exception.", t.Exception);
                    }
                    else if (t.IsCanceled)
                    {
                        if (Logger.IsDebug) Logger.Debug($"Block producer stopped.");
                    }
                    else if (t.IsCompleted)
                    {
                        if (Logger.IsDebug) Logger.Debug($"Block producer complete.");
                    }
                });
            }
        }

        private async Task ProducerLoop()
        {
            while (!_loopCancellationTokenSource.IsCancellationRequested)
            {
                await ProducerLoopStep(_loopCancellationTokenSource.Token);
            }
        }

        protected virtual Task ProducerLoopStep(CancellationToken token)
        {
            BlockProductionEventArgs args = new(cancellationToken: token);
            TriggerBlockProduction?.Invoke(this, args);
            return args.BlockProductionTask;
        }

        public ValueTask DisposeAsync()
        {
            lock (_loopTaskLock)
            {
                return new(_disposeTask ??= StopLoopAsync());
            }
        }

        private async Task StopLoopAsync()
        {
            using (_loopCancellationTokenSource)
            {
                await Task.WhenAll(_loopCancellationTokenSource.CancelAsync(), _loopTask ?? Task.CompletedTask);
            }
        }
    }
}
