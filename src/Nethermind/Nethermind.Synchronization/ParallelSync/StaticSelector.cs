// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Synchronization.ParallelSync
{
    public class StaticSelector : ISyncModeSelector
    {
        public StaticSelector(SyncMode syncMode)
        {
            Current = syncMode;
        }

        public static StaticSelector Full { get; } = new(SyncMode.Full);

        public static StaticSelector FastSync { get; } = new(SyncMode.FastSync);

        public static StaticSelector FastBlocks { get; } = new(SyncMode.FastBlocks);

        public static StaticSelector FastSyncWithFastBlocks { get; } = new(SyncMode.FastSync | SyncMode.FastBlocks);

        public static StaticSelector StateNodesWithFastBlocks { get; } = new(SyncMode.StateNodes | SyncMode.FastBlocks);

        public static StaticSelector FullWithFastBlocks { get; } = new(SyncMode.Full | SyncMode.FastBlocks);

        public SyncMode Current { get; }

        public event EventHandler<SyncModeChangedEventArgs> Preparing
        {
            add { }
            remove { }
        }

        public event EventHandler<SyncModeChangedEventArgs> Changed
        {
            add { }
            remove { }
        }

        public void Stop() { }

        public event EventHandler<SyncModeChangedEventArgs> Changing
        {
            add { }
            remove { }
        }

        public void Dispose() { }
    }
}
