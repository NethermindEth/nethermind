// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Events;

namespace Nethermind.Synchronization.ParallelSync
{
    public interface ISyncModeSelector : IDisposable
    {
        SyncMode Current { get; }

        event EventHandler<SyncModeChangedEventArgs> Preparing;

        event EventHandler<SyncModeChangedEventArgs> Changing;

        event EventHandler<SyncModeChangedEventArgs> Changed;

        void Stop();
        void Update();

        async Task WaitUntilMode(Func<SyncMode, bool> predicate, CancellationToken cancellationToken)
        {
            if (predicate(Current)) return;

            await Wait.ForEventCondition<SyncModeChangedEventArgs>(
                cancellationToken,
                (e) => Changed += e,
                (e) => Changed -= e,
                (arg) => predicate(arg.Current));
        }
    }
}
