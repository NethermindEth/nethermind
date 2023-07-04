// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;

namespace Nethermind.Synchronization
{
    public interface ISynchronizer : IDisposable
    {
        event EventHandler<SyncEventArgs> SyncEvent;

        void Start();

        Task StopAsync();
    }
}
