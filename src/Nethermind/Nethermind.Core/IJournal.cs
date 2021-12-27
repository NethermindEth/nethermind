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

namespace Nethermind.Core
{
    /// <summary>
    /// Journal is a collection-like type that allows saving and restoring a state through snapshots.
    /// </summary>
    /// <typeparam name="TSnapshot">Type representing state snapshot.</typeparam>
    public interface IJournal<TSnapshot>
    {
        /// <summary>
        /// Saves state to potentially restore later.
        /// </summary>
        /// <returns>State to potentially restore later.</returns>
        TSnapshot TakeSnapshot();
        
        /// <summary>
        /// Restores previously saved state.
        /// </summary>
        /// <param name="snapshot">Previously saved state.</param>
        /// <exception cref="InvalidOperationException">Thrown when snapshot cannot be restored. For example previous snapshot was already restored.</exception>
        void Restore(TSnapshot snapshot);
    }
}
