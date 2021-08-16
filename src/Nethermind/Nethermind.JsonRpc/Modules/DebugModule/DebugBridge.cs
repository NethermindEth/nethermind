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

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;

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
        private readonly Dictionary<string, IDb> _dbMappings;

        public DebugBridge(
            IConfigProvider configProvider,
            IReadOnlyDbProvider dbProvider,
            IGethStyleTracer tracer,
            IBlockTree blockTree,
            IReceiptStorage receiptStorage,
            IReceiptsMigration receiptsMigration,
            ISpecProvider specProvider)
        {
            _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
            _tracer = tracer ?? throw new ArgumentNullException(nameof(tracer));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _receiptsMigration = receiptsMigration ?? throw new ArgumentNullException(nameof(receiptsMigration));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            dbProvider = dbProvider ?? throw new ArgumentNullException(nameof(dbProvider));
            IDb blockInfosDb = dbProvider.BlockInfosDb ?? throw new ArgumentNullException(nameof(dbProvider.BlockInfosDb));
            IDb blocksDb = dbProvider.BlocksDb ?? throw new ArgumentNullException(nameof(dbProvider.BlocksDb));
            IDb headersDb = dbProvider.HeadersDb ?? throw new ArgumentNullException(nameof(dbProvider.HeadersDb));
            IDb receiptsDb = dbProvider.ReceiptsDb ?? throw new ArgumentNullException(nameof(dbProvider.ReceiptsDb));
            IDb codeDb = dbProvider.CodeDb ?? throw new ArgumentNullException(nameof(dbProvider.CodeDb));

            _dbMappings = new Dictionary<string, IDb>(StringComparer.InvariantCultureIgnoreCase)
            {
                {DbNames.State, dbProvider.StateDb},
                {DbNames.Storage, dbProvider.StateDb},
                {DbNames.BlockInfos, blockInfosDb},
                {DbNames.Blocks, blocksDb},
                {DbNames.Headers, headersDb},
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
            ReceiptTrie receiptTrie = new(_specProvider.GetSpec(block.Number), txReceipts);
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

        public GethLikeTxTrace GetTransactionTrace(long blockNumber, int index, CancellationToken cancellationToken,GethTraceOptions gethTraceOptions = null)
        {
            return _tracer.Trace(blockNumber, index, gethTraceOptions ?? GethTraceOptions.Default, cancellationToken);
        }

        public GethLikeTxTrace GetTransactionTrace(Keccak blockHash, int index, CancellationToken cancellationToken, GethTraceOptions gethTraceOptions = null)
        {
            return _tracer.Trace(blockHash, index, gethTraceOptions ?? GethTraceOptions.Default, cancellationToken);
        }

        public GethLikeTxTrace GetTransactionTrace(Rlp blockRlp, Keccak transactionHash,CancellationToken cancellationToken, GethTraceOptions gethTraceOptions = null)
        {
            return _tracer.Trace(blockRlp, transactionHash, gethTraceOptions ?? GethTraceOptions.Default, cancellationToken);
        }

        public GethLikeTxTrace[] GetBlockTrace(Keccak blockHash,CancellationToken cancellationToken, GethTraceOptions gethTraceOptions = null)
        {
            return _tracer.TraceBlock(blockHash, gethTraceOptions ?? GethTraceOptions.Default, cancellationToken); 
        }

        public GethLikeTxTrace[] GetBlockTrace(long blockNumber, CancellationToken cancellationToken, GethTraceOptions gethTraceOptions = null)
        {
            return _tracer.TraceBlock(blockNumber, gethTraceOptions ?? GethTraceOptions.Default, cancellationToken); 
        }

        public GethLikeTxTrace[] GetBlockTrace(Rlp blockRlp, CancellationToken cancellationToken,GethTraceOptions gethTraceOptions = null)
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
            return hash == null ? null : _dbMappings[DbNames.Blocks].Get(hash);
        }

        public object GetConfigValue(string category, string name)
        {
            return _configProvider.GetRawValue(category, name);
        }
    }
}
