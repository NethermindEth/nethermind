// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.ServiceStopper;

namespace Nethermind.Synchronization.ParallelSync
{
    public interface ISyncModeSelector : IDisposable, IStoppableService
    {
        SyncMode Current { get; }

        event EventHandler<SyncModeChangedEventArgs> Preparing;

        event EventHandler<SyncModeChangedEventArgs> Changing;

        event EventHandler<SyncModeChangedEventArgs> Changed;

        Task StartAsync();

        void Update();

        async Task WaitUntilMode(Func<SyncMode, bool> predicate, CancellationToken cancellationToken)
        {
            TaskCompletionSource completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnChanged(object? _, SyncModeChangedEventArgs args)
            {
                if (predicate(args.Current))
                {
                    completionSource.TrySetResult();
                }
            }

            Changed += OnChanged;
            try
            {
                if (predicate(Current)) return;
                await completionSource.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                Changed -= OnChanged;
            }
        }
    }
}
