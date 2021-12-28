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

namespace Nethermind.Db.FullPruning
{
    /// <summary>
    /// Database wrapper for full pruning.
    /// </summary>
    public interface IFullPruningDb
    {
        /// <summary>
        /// Are we able to start full pruning.
        /// </summary>
        bool CanStartPruning { get; }
        
        /// <summary>
        /// Try starting full pruning.
        /// </summary>
        /// <param name="context">Out, context of pruning.</param>
        /// <returns>true if pruning was started, false otherwise.</returns>
        bool TryStartPruning(out IPruningContext context);
        
        /// <summary>
        /// Gets the path to current DB using base path.
        /// </summary>
        /// <param name="basePath"></param>
        /// <returns></returns>
        string GetPath(string basePath);
        
        /// <summary>
        /// Gets the name of inner DB.
        /// </summary>
        string InnerDbName { get; }
    }
}
