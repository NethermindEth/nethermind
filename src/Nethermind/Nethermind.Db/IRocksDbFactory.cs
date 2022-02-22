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

namespace Nethermind.Db
{
    /// <summary>
    /// Allows to create a new Rocks <see cref="IDb"/> instance.
    /// </summary>
    public interface IRocksDbFactory
    {
        /// <summary>
        /// Creates a standard RocksDB.
        /// </summary>
        /// <param name="rocksDbSettings">Setting to use for DB creation.</param>
        /// <returns>Standard DB.</returns>
        IDb CreateDb(RocksDbSettings rocksDbSettings);
        
        /// <summary>
        /// Creates a column RocksDB.
        /// </summary>
        /// <param name="rocksDbSettings">Setting to use for DB creation.</param>
        /// <returns>Column DB.</returns>
        IColumnsDb<T> CreateColumnsDb<T>(RocksDbSettings rocksDbSettings) where T : notnull;

        /// <summary>
        /// Gets the file system path for the DB.
        /// </summary>
        /// <param name="rocksDbSettings">Setting to use for DB creation.</param>
        /// <returns>File system path for the DB.</returns>
        public string GetFullDbPath(RocksDbSettings rocksDbSettings) => rocksDbSettings.DbPath;
    }
}
