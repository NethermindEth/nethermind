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
        private Task? _loopTask;
        protected ILogger Logger { get; }

        public event EventHandler<BlockProductionEventArgs>? TriggerBlockProduction;

        public BuildBlocksInALoop(ILogManager logManager, bool autoStart = true)
        {
            Logger = logManager.GetClassLogger();
            if (autoStart)
            {
                StartLoop();
            }
        }

        public void StartLoop()
        {
            if (_loopTask is null)
            {
                lock (_loopCancellationTokenSource)
                {
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
        }

        private async Task ProducerLoop()
        {
            while (!_loopCancellationTokenSource.IsCancellationRequested)
            {
                await ProducerLoopStep(_loopCancellationTokenSource.Token);
            }
        }

        protected virtual async Task ProducerLoopStep(CancellationToken token)
        {
            BlockProductionEventArgs args = new(cancellationToken: token);
            TriggerBlockProduction?.Invoke(this, args);
            await args.BlockProductionTask;
        }

        public async ValueTask DisposeAsync()
        {
            _loopCancellationTokenSource.Cancel();
            if (_loopTask is not null)
            {
                await _loopTask;
            }
        }
    }
}
