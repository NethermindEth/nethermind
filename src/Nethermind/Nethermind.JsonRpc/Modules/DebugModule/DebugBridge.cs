// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Reporting;

namespace Nethermind.JsonRpc.Modules.DebugModule
{
    public class DebugBridge : IDebugBridge
    {
        private readonly IConfigProvider _configProvider;
        private readonly IGethStyleTracer _tracer;
        private readonly IBlockTree _blockTree;
        private readonly IReceiptStorage _receiptStorage;
        private readonly IReceiptsMigration _receiptsMigration;
        private readonly ISpecProvider _specProvider;
        private readonly ISyncModeSelector _syncModeSelector;
        private readonly Dictionary<string, IDb> _dbMappings;

        public DebugBridge(
            IConfigProvider configProvider,
            IReadOnlyDbProvider dbProvider,
            IGethStyleTracer tracer,
            IBlockTree blockTree,
            IReceiptStorage receiptStorage,
            IReceiptsMigration receiptsMigration,
            ISpecProvider specProvider,
            ISyncModeSelector syncModeSelector)
        {
            _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
            _tracer = tracer ?? throw new ArgumentNullException(nameof(tracer));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _receiptsMigration = receiptsMigration ?? throw new ArgumentNullException(nameof(receiptsMigration));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _syncModeSelector = syncModeSelector ?? throw new ArgumentNullException(nameof(syncModeSelector));
            dbProvider = dbProvider ?? throw new ArgumentNullException(nameof(dbProvider));
            IDb blockInfosDb = dbProvider.BlockInfosDb ?? throw new ArgumentNullException(nameof(dbProvider.BlockInfosDb));
            IDb blocksDb = dbProvider.BlocksDb ?? throw new ArgumentNullException(nameof(dbProvider.BlocksDb));
            IDb headersDb = dbProvider.HeadersDb ?? throw new ArgumentNullException(nameof(dbProvider.HeadersDb));
            IDb receiptsDb = dbProvider.ReceiptsDb ?? throw new ArgumentNullException(nameof(dbProvider.ReceiptsDb));
            IDb codeDb = dbProvider.CodeDb ?? throw new ArgumentNullException(nameof(dbProvider.CodeDb));
            IDb metadataDb = dbProvider.MetadataDb ?? throw new ArgumentNullException(nameof(dbProvider.MetadataDb));

            _dbMappings = new Dictionary<string, IDb>(StringComparer.InvariantCultureIgnoreCase)
            {
                {DbNames.State, dbProvider.StateDb},
                {DbNames.Storage, dbProvider.StateDb},
                {DbNames.BlockInfos, blockInfosDb},
                {DbNames.Blocks, blocksDb},
                {DbNames.Headers, headersDb},
                {DbNames.Metadata, metadataDb},
                {DbNames.Code, codeDb},
                {DbNames.Receipts, receiptsDb},
            };
        }

        public byte[] GetDbValue(string dbName, byte[] key)
        {
            return _dbMappings[dbName][key];
        }

        public ChainLevelInfo GetLevelInfo(long number)
        {
            return _blockTree.FindLevel(number);
        }

        public int DeleteChainSlice(long startNumber)
        {
            return _blockTree.DeleteChainSlice(startNumber);
        }

        public void UpdateHeadBlock(Keccak blockHash)
        {
            _blockTree.UpdateHeadBlock(blockHash);
        }

        public Task<bool> MigrateReceipts(long blockNumber)
            => _receiptsMigration.Run(blockNumber + 1); // add 1 to make go from inclusive (better for API) to exclusive (better for internal)

        public void InsertReceipts(BlockParameter blockParameter, TxReceipt[] txReceipts)
        {
            SearchResult<Block> searchResult = _blockTree.SearchForBlock(blockParameter);
            if (searchResult.IsError)
            {
                throw new InvalidDataException(searchResult.Error);
            }

            Block block = searchResult.Object;
            ReceiptTrie receiptTrie = new(_specProvider.GetSpec(block.Header), txReceipts);
            receiptTrie.UpdateRootHash();
            if (block.ReceiptsRoot != receiptTrie.RootHash)
            {
                throw new InvalidDataException("Receipts root mismatch");
            }

            _receiptStorage.Insert(block, txReceipts);
        }

        public GethLikeTxTrace GetTransactionTrace(Keccak transactionHash, CancellationToken cancellationToken, GethTraceOptions gethTraceOptions = null)
        {
            return _tracer.Trace(transactionHash, gethTraceOptions ?? GethTraceOptions.Default, cancellationToken);
        }

        public GethLikeTxTrace GetTransactionTrace(long blockNumber, int index, CancellationToken cancellationToken, GethTraceOptions gethTraceOptions = null)
        {
            return _tracer.Trace(blockNumber, index, gethTraceOptions ?? GethTraceOptions.Default, cancellationToken);
        }

        public GethLikeTxTrace GetTransactionTrace(Keccak blockHash, int index, CancellationToken cancellationToken, GethTraceOptions gethTraceOptions = null)
        {
            return _tracer.Trace(blockHash, index, gethTraceOptions ?? GethTraceOptions.Default, cancellationToken);
        }

        public GethLikeTxTrace GetTransactionTrace(Rlp blockRlp, Keccak transactionHash, CancellationToken cancellationToken, GethTraceOptions gethTraceOptions = null)
        {
            return _tracer.Trace(blockRlp, transactionHash, gethTraceOptions ?? GethTraceOptions.Default, cancellationToken);
        }

        public GethLikeTxTrace? GetTransactionTrace(Transaction transaction, BlockParameter blockParameter, CancellationToken cancellationToken, GethTraceOptions? gethTraceOptions = null)
        {
            return _tracer.Trace(blockParameter, transaction, gethTraceOptions ?? GethTraceOptions.Default, cancellationToken);
        }

        public GethLikeTxTrace[] GetBlockTrace(BlockParameter blockParameter, CancellationToken cancellationToken, GethTraceOptions gethTraceOptions = null)
        {
            return _tracer.TraceBlock(blockParameter, gethTraceOptions ?? GethTraceOptions.Default, cancellationToken);
        }

        public GethLikeTxTrace[] GetBlockTrace(Rlp blockRlp, CancellationToken cancellationToken, GethTraceOptions gethTraceOptions = null)
        {
            return _tracer.TraceBlock(blockRlp, gethTraceOptions ?? GethTraceOptions.Default, cancellationToken);
        }

        public byte[] GetBlockRlp(Keccak blockHash)
        {
            return _dbMappings[DbNames.Blocks].Get(blockHash);
        }

        public byte[] GetBlockRlp(long number)
        {
            Keccak hash = _blockTree.FindHash(number);
            return hash is null ? null : _dbMappings[DbNames.Blocks].Get(hash);
        }

        public object GetConfigValue(string category, string name)
        {
            return _configProvider.GetRawValue(category, name);
        }

        public SyncReportSymmary GetCurrentSyncStage()
        {
            return new SyncReportSymmary
            {
                CurrentStage = _syncModeSelector.Current.ToString()
            };
        }
    }
}
