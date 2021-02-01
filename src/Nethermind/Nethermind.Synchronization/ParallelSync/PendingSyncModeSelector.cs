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
// 

using System;

namespace Nethermind.Synchronization.ParallelSync
{
    public class PendingSyncModeSelector : ISyncModeSelector
    {
        private ISyncModeSelector? _syncModeSelector;

        public void SetActual(ISyncModeSelector syncModeSelector)
        {
            _syncModeSelector = syncModeSelector ?? throw new ArgumentNullException(nameof(syncModeSelector));
            _syncModeSelector.Preparing += SyncModeSelectorOnPreparing;
            _syncModeSelector.Changing += SyncModeSelectorOnChanging;
            _syncModeSelector.Changed += SyncModeSelectorOnChanged;
        }

        private void SyncModeSelectorOnPreparing(object? sender, SyncModeChangedEventArgs e)
        {
            Preparing?.Invoke(this, e);
        }

        private void SyncModeSelectorOnChanging(object? sender, SyncModeChangedEventArgs e)
        {
            Changing?.Invoke(this, e);
        }

        private void SyncModeSelectorOnChanged(object? sender, SyncModeChangedEventArgs e)
        {
            Changed?.Invoke(this, e);
        }

        public SyncMode Current => _syncModeSelector?.Current ?? SyncMode.WaitingForBlock;
        public event EventHandler<SyncModeChangedEventArgs>? Preparing;
        public event EventHandler<SyncModeChangedEventArgs>? Changing;
        public event EventHandler<SyncModeChangedEventArgs>? Changed;
    }
}
