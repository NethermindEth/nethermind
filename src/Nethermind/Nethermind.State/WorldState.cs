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

namespace Nethermind.State
{
    public class WorldState : IWorldState
    {
        public IStateProvider StateProvider { get; }

        public Snapshot TakeSnapshot(bool newTransactionStart = false)
        {
            Snapshot.Storage storageSnapshot = StorageProvider.TakeSnapshot(newTransactionStart);
            return new(StateProvider.TakeSnapshot(), storageSnapshot);
        }

        public void Restore(Snapshot snapshot)
        {
            StateProvider.Restore(snapshot.StateSnapshot);
            StorageProvider.Restore(snapshot.StorageSnapshot);
        }

        public IStorageProvider StorageProvider { get; }

        public WorldState(IStateProvider stateProvider, IStorageProvider storageProvider)
        {
            StateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
            StorageProvider = storageProvider ?? throw new ArgumentNullException(nameof(storageProvider));
        }
    }
}
