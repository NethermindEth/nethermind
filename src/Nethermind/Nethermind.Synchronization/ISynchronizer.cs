// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.SnapSync;

namespace Nethermind.Synchronization
{
    public interface ISynchronizer : IDisposable
    {
        event EventHandler<SyncEventArgs> SyncEvent;

        void Start();

        Task StopAsync();

        ISyncProgressResolver SyncProgressResolver { get; }
        ISyncModeSelector SyncModeSelector { get; }
    }
}
