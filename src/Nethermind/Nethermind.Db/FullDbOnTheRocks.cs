using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using Nethermind.Store;
using RocksDbSharp;

namespace Nethermind.Db
{
    public class FullDbOnTheRocks : IFullDb
    {
        public const string DiscoveryNodesDbPath = "discoveryNodes";
        public const string PeersDbPath = "peers";

        private static readonly ConcurrentDictionary<string, RocksDb> DbsByPath = new ConcurrentDictionary<string, RocksDb>();

        protected readonly RocksDb _db;
        private readonly DbInstance _dbInstance;

        private WriteBatchWithIndex _currentBatch;

        public FullDbOnTheRocks(string dbPath) // TODO: check column families
        {
            if (!Directory.Exists(dbPath))
            {
                Directory.CreateDirectory(dbPath);
            }

            // options are based mainly from EtheruemJ at the moment

            BlockBasedTableOptions tableOptions = new BlockBasedTableOptions();
            //tableOptions.SetPinL0FilterAndIndexBlocksInCache(true);
            tableOptions.SetBlockSize(16 * 1024);
            //tableOptions.SetCacheIndexAndFilterBlocks(true);
            tableOptions.SetFilterPolicy(BloomFilterPolicy.Create(10, false));
            tableOptions.SetFormatVersion(2);

            DbOptions options = new DbOptions();
            options.SetCreateIfMissing(true);
            options.OptimizeForPointLookup(32);
            //options.SetCompression(CompressionTypeEnum.rocksdb_snappy_compression);
            //options.SetLevelCompactionDynamicLevelBytes(true);
            //options.SetMaxBackgroundCompactions(4);
            //options.SetMaxBackgroundFlushes(2);
            //options.SetMaxOpenFiles(32);
            //options.SetDbWriteBufferSize(1024 * 1024 * 16);
            options.SetWriteBufferSize(1024 * 1024 * 16);
            options.SetMaxWriteBufferNumber(6);
            options.SetMinWriteBufferNumberToMerge(2);
            options.SetBlockBasedTableFactory(tableOptions);

            //SliceTransform transform = SliceTransform.CreateFixedPrefix(16);
            //options.SetPrefixExtractor(transform);

            _db = DbsByPath.GetOrAdd(dbPath, path => RocksDb.Open(options, Path.Combine("db", path)));

            if (dbPath.EndsWith(DiscoveryNodesDbPath))
            {
                _dbInstance = DbInstance.DiscoveryNodes;
            }
            else if (dbPath.EndsWith(PeersDbPath))
            {
                _dbInstance = DbInstance.Peers;
            }            
        }

        public ICollection<byte[]> Keys
        {
            get { return GetKeysOrValues(x => x.Key()); }
        }

        public ICollection<byte[]> Values
        {
            get { return GetKeysOrValues(x => x.Value()); }
        }

        public byte[] this[byte[] key]
        {
            get
            {
                switch (_dbInstance)
                {
                    case DbInstance.DiscoveryNodes:
                        Metrics.DiscoveryNodesDbReads++;
                        break;
                    case DbInstance.Peers:
                        Metrics.PeersDbReads++;
                        break;
                }

                if (_currentBatch != null)
                {
                    return _currentBatch.Get(key);
                }

                return _db.Get(key);
            }
            set
            {
                switch (_dbInstance)
                {
                    case DbInstance.DiscoveryNodes:
                        Metrics.DiscoveryNodesDbWrites++;
                        break;
                    case DbInstance.Peers:
                        Metrics.PeersDbWrites++;
                        break;
                }

                if (_currentBatch != null)
                {
                    if (value == null)
                    {
                        _currentBatch.Delete(key);
                    }
                    else
                    {
                        _currentBatch.Put(key, value);
                    }
                }
                else
                {
                    if (value == null)
                    {
                        _db.Remove(key);
                    }
                    else
                    {
                        _db.Put(key, value);
                    }
                }
            }
        }

        public void Remove(byte[] key)
        {
            _db.Remove(key);
        }

        public void StartBatch()
        {
            _currentBatch = new WriteBatchWithIndex();
        }

        public void CommitBatch()
        {
            _db.Write(_currentBatch);
            _currentBatch.Dispose();
            _currentBatch = null;
        }

        private ICollection<byte[]> GetKeysOrValues(Func<Iterator, byte[]> selector)
        {
            ReadOptions readOptions = new ReadOptions();
            List<byte[]> items = new List<byte[]>();
            using (Iterator iter = _db.NewIterator(readOptions: readOptions))
            {
                iter.SeekToFirst();
                while (iter.Valid())
                {
                    byte[] item = selector.Invoke(iter);
                    items.Add(item);
                    iter.Next();
                }
            }

            return items;
        }

        private enum DbInstance
        {
            DiscoveryNodes,
            Peers
        }
    }
}