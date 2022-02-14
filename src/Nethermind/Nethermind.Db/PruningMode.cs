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

namespace Nethermind.Db
{
    /// <summary>
    /// Defines pruning mode.
    /// </summary>
    [Flags]
    public enum PruningMode
    {
        /// <summary>
        /// No pruning - full archive.
        /// </summary>
        None = 0,
        
        /// <summary>
        /// In memory pruning.
        /// </summary>
        Memory = 1,
        
        /// <summary>
        /// Full pruning.
        /// </summary>
        Full = 2,
        
        /// <summary>
        /// Both in memory and full pruning.
        /// </summary>
        Hybrid = Memory | Full
    }
    
    public static class PruningModeExtensions
    {
        public static bool IsMemory(this PruningMode mode) => (mode & PruningMode.Memory) == PruningMode.Memory;
        public static bool IsFull(this PruningMode mode) => (mode & PruningMode.Full) == PruningMode.Full;
    }
}
