using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Logging;
using Nethermind.Store;

namespace Nethermind.Db
{
    public class SimpleFilePublicKeyDb : IFullDb
    {
        private readonly ILogger _logger;
        private readonly IPerfService _perfService;
        private ConcurrentDictionary<PublicKey, byte[]> _cache;
        private const string DbName = "SimpleFileDb.db";
        private readonly string _dbPath;
        private readonly string _dbLastDirName;
        private bool _anyPendingChanges;

        public SimpleFilePublicKeyDb(string dbDirectoryPath, ILogManager logManager, IPerfService perfService)
        {
            _perfService = perfService;
            _dbPath = Path.Combine(dbDirectoryPath, DbName);
            _logger = logManager.GetClassLogger();
            if (!Directory.Exists(dbDirectoryPath))
            {
                Directory.CreateDirectory(dbDirectoryPath);
            }
            _dbLastDirName = new DirectoryInfo(dbDirectoryPath).Name;
            LoadData();
        }

        public byte[] this[byte[] key]
        {
            get => _cache[new PublicKey(key)];
            set
            {
                _cache.AddOrUpdate(new PublicKey(key), newValue => Add(value), (x, oldValue) => Update(oldValue, value));
            }
        }

        public void Remove(byte[] key)
        {
            _anyPendingChanges = true;
            _cache.TryRemove(new PublicKey(key), out _);
        }

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

            var key = _perfService.StartPerfCalc();

            var tempFilePath = CreateBackup();

            _anyPendingChanges = false;
            var snapshot = _cache.ToArray();

            using (var streamWriter = new StreamWriter(_dbPath))
            {
                foreach (var keyValuePair in snapshot)
                {
                    streamWriter.WriteLine($"{keyValuePair.Key},{keyValuePair.Value.ToHexString()}");
                }
            }

            RemoveBackup(tempFilePath);

            _perfService.EndPerfCalc(key, $"Db commit ({_dbLastDirName}), items count: {snapshot.Length}");
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

        public ICollection<byte[]> Keys => _cache.Keys.Select(x => x.Bytes).ToArray();
        public ICollection<byte[]> Values => _cache.Values;

        private void LoadData()
        {
            var key = _perfService.StartPerfCalc();

            _cache = new ConcurrentDictionary<PublicKey, byte[]>();

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

                _cache[new PublicKey(values[0])] = Bytes.FromHexString(values[1]);
            }

            _perfService.EndPerfCalc(key, $"Db load ({_dbLastDirName}), items count: {lines.Length}");
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