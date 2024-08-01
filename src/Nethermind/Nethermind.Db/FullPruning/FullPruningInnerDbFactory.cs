// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Abstractions;
using System.Linq;

namespace Nethermind.Db.FullPruning
{
    /// <summary>
    /// Factory
    /// </summary>
    public class FullPruningInnerDbFactory : IDbFactory
    {
        private readonly IDbFactory _dbFactory;
        private readonly IFileSystem _fileSystem;
        private int _index; // current index of the inner db

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="dbFactory">Inner real db factory.</param>
        /// <param name="fileSystem">File system.</param>
        /// <param name="path">Main DB path.</param>
        public FullPruningInnerDbFactory(IDbFactory dbFactory, IFileSystem fileSystem, string path)
        {
            _dbFactory = dbFactory;
            _fileSystem = fileSystem;
            _index = GetStartingIndex(path); // we need to read the current state of inner DB's
        }

        /// <inheritdoc />
        public IDb CreateDb(DbSettings dbSettings)
        {
            DbSettings settings = GetRocksDbSettings(dbSettings);
            return _dbFactory.CreateDb(settings);
        }

        /// <inheritdoc />
        public IColumnsDb<T> CreateColumnsDb<T>(DbSettings dbSettings) where T : struct, Enum
        {
            DbSettings settings = GetRocksDbSettings(dbSettings);
            return _dbFactory.CreateColumnsDb<T>(settings);
        }

        /// <inheritdoc />
        public string GetFullDbPath(DbSettings dbSettings)
        {
            DbSettings settings = GetRocksDbSettings(dbSettings);
            return _dbFactory.GetFullDbPath(settings);
        }

        // When creating a new DB, we need to change its inner settings
        private DbSettings GetRocksDbSettings(DbSettings originalSetting)
        {
            _index++;

            // if its -1 then this is first db.
            bool firstDb = _index == -1;

            // if first DB, then we will put it into main directory and not use indexed subdirectory
            string dbName = firstDb ? originalSetting.DbName : originalSetting.DbName + _index;
            string dbPath = firstDb ? originalSetting.DbPath : _fileSystem.Path.Combine(originalSetting.DbPath, _index.ToString());
            DbSettings dbSettings = originalSetting.Clone(dbName, dbPath);
            dbSettings.CanDeleteFolder = !firstDb; // we cannot delete main db folder, only indexed subfolders
            return dbSettings;
        }

        /// <summary>
        /// Gets the current start index for indexed DB's
        /// </summary>
        /// <param name="path">Main path to DB directory.</param>
        /// <returns>Current - starting index of DB.</returns>
        private int GetStartingIndex(string path)
        {
            // gets path to non-index DB.
            string fullPath = _dbFactory.GetFullDbPath(new DbSettings(string.Empty, path));
            IDirectoryInfo directory = _fileSystem.DirectoryInfo.New(fullPath);
            if (directory.Exists)
            {
                if (directory.EnumerateFiles().Any())
                {
                    return -2; // if there are files in the directory, then we have a main DB, marked -2.
                }

                // else we have sub-directories, which should be index based
                // we want to find lowest positive index and return it - 1.
                int minIndex = directory.EnumerateDirectories()
                    .Select(d => int.TryParse(d.Name, out int index) ? index : -1)
                    .Where(i => i >= 0)
                    .OrderBy(i => i)
                    .FirstOrDefault();

                return minIndex - 1;
            }

            return -1; // if directory doesn't exist current index is -1.
        }
    }
}
