// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Db
{
    /// <summary>
    /// Allows to create a new Db <see cref="IDb"/> instance.
    /// </summary>
    public interface IDbFactory
    {
        /// <summary>
        /// Creates a standard Db.
        /// </summary>
        /// <param name="dbSettingstings">Setting to use for DB creation.</param>
        /// <returns>Standard DB.</returns>
        IDb CreateDb(DbSettings dbSettings);

        /// <summary>
        /// Creates a column Db.
        /// </summary>
        /// <param name="dbSettingstings">Setting to use for DB creation.</param>
        /// <returns>Column DB.</returns>
        IColumnsDb<T> CreateColumnsDb<T>(DbSettings dbSettings) where T : struct, Enum;

        /// <summary>
        /// Gets the file system path for the DB.
        /// </summary>
        /// <param name="dbSettingstings">Setting to use for DB creation.</param>
        /// <returns>File system path for the DB.</returns>
        public string GetFullDbPath(DbSettings dbSettings) => dbSettings.DbPath;
    }
}
