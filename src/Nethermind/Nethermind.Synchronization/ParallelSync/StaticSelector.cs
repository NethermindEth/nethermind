//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;

namespace Nethermind.Synchronization.ParallelSync
{
    public class StaticSelector : ISyncModeSelector
    {
        public StaticSelector(SyncMode syncMode)
        {
            Current = syncMode;
        }

        public static StaticSelector Beam { get; } = new(SyncMode.Beam);
        
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
        
        public event EventHandler<SyncModeChangedEventArgs> Changing
        {
            add { }
            remove { }
        }
    }
}