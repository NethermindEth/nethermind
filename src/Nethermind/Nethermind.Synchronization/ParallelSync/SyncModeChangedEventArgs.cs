// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Synchronization.ParallelSync
{
    public class SyncModeChangedEventArgs : EventArgs
    {
        public SyncModeChangedEventArgs(SyncMode previous, SyncMode current)
        {
            Previous = previous;
            Current = current;
        }

        public SyncMode Previous { get; }
        public SyncMode Current { get; }
    }
}
