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
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Merge.Plugin.Synchronization
{
    public class MergeSyncModeSelector : ISyncModeSelector
    {
        private readonly ISyncModeSelector _preMergeSyncModeSelector;

        public MergeSyncModeSelector(
            ISyncModeSelector preMergeSyncModeSelector)
        {
            _preMergeSyncModeSelector = preMergeSyncModeSelector;
            
            _preMergeSyncModeSelector!.Preparing += OnPreparing;
            _preMergeSyncModeSelector!.Changing += OnChanging;
            _preMergeSyncModeSelector!.Changed += OnChanged;
        }
        
        private void OnPreparing(object? sender, SyncModeChangedEventArgs e)
        {
            Preparing?.Invoke(this, e);
        }
        
        private void OnChanging(object? sender, SyncModeChangedEventArgs e)
        {
            Changing?.Invoke(this, e);
        }
        
        private void OnChanged(object? sender, SyncModeChangedEventArgs e)
        {
            Changed?.Invoke(this, e);
        }

        public SyncMode Current => _preMergeSyncModeSelector.Current;
        public event EventHandler<SyncModeChangedEventArgs>? Preparing;
        public event EventHandler<SyncModeChangedEventArgs>? Changing;
        public event EventHandler<SyncModeChangedEventArgs>? Changed;
    }
}
