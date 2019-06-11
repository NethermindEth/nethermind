/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Store;

namespace Nethermind.Db
{
    [Todo(Improve.Performance, "Remove this entirely and replace with RocksDB")]
    public class SimpleFilePublicKeyDb : IFullDb
    {
        private readonly ILogger _logger;
        private ConcurrentDictionary<byte[], byte[]> _cache;
        public const string DbName = "SimpleFileDb.db";
        private readonly string _dbPath;
        private readonly string _dbLastDirName;
        private bool _anyPendingChanges;

        public SimpleFilePublicKeyDb(string dbDirectoryPath, ILogManager logManager)
        {
            _logger = logManager.GetClassLogger() ??  throw new ArgumentNullException(nameof(logManager));
            if (dbDirectoryPath == null) throw new ArgumentNullException(nameof(dbDirectoryPath));
            
            _dbPath = Path.Combine(dbDirectoryPath, DbName);
            if (!Directory.Exists(dbDirectoryPath))
            {
                Directory.CreateDirectory(dbDirectoryPath);
            }
            
            _dbLastDirName = new DirectoryInfo(dbDirectoryPath).Name;
            
            LoadData();
        }

        public byte[] this[byte[] key]
        {
            get => _cache[key];
            set
            {
                _cache.AddOrUpdate(key, newValue => Add(value), (x, oldValue) => Update(oldValue, value));
            }
        }

        public void Remove(byte[] key)
        {
            _anyPendingChanges = true;
            _cache.TryRemove(key, out _);
        }

        public bool KeyExists(byte[] key)
        {
            return _cache.ContainsKey(key);
        }

        public byte[][] GetAll() => _cache.Values.Select(v => v).ToArray();

        public void StartBatch()
        {
        }

        public void CommitBatch()
        {
            if (!_anyPendingChanges)
            {
                if (_logger.IsTrace) _logger.Trace($"Skipping commit ({_dbLastDirName}), no changes");
                return;
            }

            var tempFilePath = CreateBackup();

            _anyPendingChanges = false;
            var snapshot = _cache.ToArray();

            using (var streamWriter = new StreamWriter(_dbPath))
            {
                foreach (var keyValuePair in snapshot)
                {
                    keyValuePair.Key.StreamHex(streamWriter);
                    streamWriter.Write(',');
                    keyValuePair.Value.StreamHex(streamWriter);
                    streamWriter.WriteLine();
                }
            }

            RemoveBackup(tempFilePath);
        }

        private void RemoveBackup(string tempFilePath)
        {
            try
            {
                if (tempFilePath != null && File.Exists(tempFilePath))
                {
                    File.Delete(tempFilePath);
                }
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error($"Error during backup removal: {_dbPath}, tempFilePath: {tempFilePath}", e);
            }
        }

        private string CreateBackup()
        {
            try
            {
                var tempFilePath = $"{_dbPath}_{Guid.NewGuid().ToString()}";

                if (File.Exists(_dbPath))
                {
                    File.Move(_dbPath, tempFilePath);
                    return tempFilePath;
                }

                return null;
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error($"Error during backup creation: {_dbPath}", e);
                return null;
            }
        }

        public string Description => _dbPath;
        public ICollection<byte[]> Keys => _cache.Keys.ToArray();
        public ICollection<byte[]> Values => _cache.Values;

        private void LoadData()
        {
            _cache = new ConcurrentDictionary<byte[], byte[]>(Bytes.EqualityComparer);

            if (!File.Exists(_dbPath))
            {
                return;
            }

            var lines = File.ReadAllLines(_dbPath);
            foreach (var line in lines)
            {
                var values = line.Split(",");
                if (values.Length != 2)
                {
                    if (_logger.IsError) _logger.Error($"Error in data file, too many items: {line}");
                    continue;
                }

                _cache[Bytes.FromHexString(values[0])] = Bytes.FromHexString(values[1]);
            }
        }

        private byte[] Update(byte[] oldValue, byte[] newValue)
        {
            if (!Bytes.AreEqual(oldValue, newValue))
            {
                _anyPendingChanges = true;
            }

            return newValue;
        }

        private byte[] Add(byte[] value)
        {
            _anyPendingChanges = true;
            return value;
        }

        public void Dispose()
        {
        }
    }
}