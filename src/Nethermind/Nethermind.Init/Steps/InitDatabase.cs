// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Db.Rpc;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.TxPool;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(ApplyMemoryHint))]
    public class InitDatabase : IStep
    {
        private readonly INethermindApi _api;

        public InitDatabase(INethermindApi api)
        {
            _api = api;
        }

        public virtual async Task Execute(CancellationToken _)
        {
            ILogger logger = _api.LogManager.GetClassLogger();

            /* sync */
            IDbConfig dbConfig = _api.Config<IDbConfig>();
            ISyncConfig syncConfig = _api.Config<ISyncConfig>();
            IInitConfig initConfig = _api.Config<IInitConfig>();
            ITxPoolConfig txPoolConfig = _api.Config<ITxPoolConfig>();
            IReceiptConfig receiptConfig = _api.Config<IReceiptConfig>();

            foreach (PropertyInfo propertyInfo in typeof(IDbConfig).GetProperties())
            {
                if (logger.IsDebug) logger.Debug($"DB {propertyInfo.Name}: {propertyInfo.GetValue(dbConfig)}");
            }

            try
            {
                bool useReceiptsDb = receiptConfig.StoreReceipts || syncConfig.DownloadReceiptsInFastSync;
                bool useBlobsDb = txPoolConfig.BlobsSupport.IsPersistentStorage();
                InitDbApi(initConfig, dbConfig, receiptConfig.StoreReceipts || syncConfig.DownloadReceiptsInFastSync);
                _api.DisposeStack.Push(_api.DbProvider!);
                StandardDbInitializer dbInitializer = new(_api.DbProvider, _api.DbFactory, _api.FileSystem);
                await dbInitializer.InitStandardDbsAsync(useReceiptsDb, useBlobsDb);
                _api.BlobTxStorage = useBlobsDb
                    ? new BlobTxStorage(_api.DbProvider!.BlobTransactionsDb)
                    : NullBlobTxStorage.Instance;
            }
            catch (TypeInitializationException ex)
            {
                if (logger.IsError)
                    logger.Error("Failed loading RocksDB", ex);
            }
        }

        private void InitDbApi(IInitConfig initConfig, IDbConfig dbConfig, bool storeReceipts)
        {
            switch (initConfig.DiagnosticMode)
            {
                case DiagnosticMode.RpcDb:
                    _api.DbProvider = new DbProvider();
                    RocksDbFactory rocksDbFactory = new(dbConfig, _api.LogManager, Path.Combine(initConfig.BaseDbPath, "debug"));
                    RpcDbFactory rpcDbFactory = new(rocksDbFactory, _api.EthereumJsonSerializer, new BasicJsonRpcClient(new Uri(initConfig.RpcDbUrl), _api.EthereumJsonSerializer, _api.LogManager), _api.LogManager);
                    _api.DbFactory = rpcDbFactory;
                    break;
                case DiagnosticMode.ReadOnlyDb:
                    DbProvider rocksDbProvider = new();
                    _api.DbProvider = new ReadOnlyDbProvider(rocksDbProvider, storeReceipts); // ToDo storeReceipts as createInMemoryWriteStore - bug?
                    _api.DbFactory = new RocksDbFactory(dbConfig, _api.LogManager, Path.Combine(initConfig.BaseDbPath, "debug"));
                    break;
                case DiagnosticMode.MemDb:
                    _api.DbProvider = new DbProvider();
                    _api.DbFactory = new MemDbFactory();
                    break;
                default:
                    _api.DbProvider = new DbProvider();
                    _api.DbFactory = new RocksDbFactory(dbConfig, _api.LogManager, initConfig.BaseDbPath);
                    break;
            }
        }
    }
}
