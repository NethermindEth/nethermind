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

using System.Runtime.CompilerServices;

[assembly:InternalsVisibleTo("Nethermind.Evm.Test")]
namespace Nethermind.State
{
    /// <summary>
    /// Stores state and storage snapshots (as the change index that we can revert to)
    /// At the beginning and after each commit the snapshot is set to the value of EmptyPosition
    /// </summary>
    public readonly struct Snapshot
    {
        public Snapshot(int stateSnapshot, Storage storageSnapshot)
        {
            StateSnapshot = stateSnapshot;
            StorageSnapshot = storageSnapshot;
        }

        public struct Storage
        {

            public int PersistentStorageSnapshot { get; }
            public int TransientStorageSnapshot { get; }

            public Storage(int storageSnapshot, int transientStorageSnapshot)
            {
                PersistentStorageSnapshot = storageSnapshot;
                TransientStorageSnapshot = transientStorageSnapshot;
            }
        }
        
        public int StateSnapshot { get; }
        public Storage StorageSnapshot { get; }
        
        public const int EmptyPosition = -1;
    }
}
