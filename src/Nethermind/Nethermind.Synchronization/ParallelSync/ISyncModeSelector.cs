// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Synchronization.ParallelSync
{
    public interface ISyncModeSelector : IDisposable
    {
        SyncMode Current { get; }

        event EventHandler<SyncModeChangedEventArgs> Preparing;

        event EventHandler<SyncModeChangedEventArgs> Changing;

        event EventHandler<SyncModeChangedEventArgs> Changed;

        void Stop();
    }
}
