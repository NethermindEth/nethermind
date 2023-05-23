// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

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
        IColumnsDb<T> CreateColumnsDb<T>(RocksDbSettings rocksDbSettings) where T : struct, Enum;

        /// <summary>
        /// Gets the file system path for the DB.
        /// </summary>
        /// <param name="rocksDbSettings">Setting to use for DB creation.</param>
        /// <returns>File system path for the DB.</returns>
        public string GetFullDbPath(RocksDbSettings rocksDbSettings) => rocksDbSettings.DbPath;
    }
}
