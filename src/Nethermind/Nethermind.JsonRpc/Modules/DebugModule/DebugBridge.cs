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
using System.Collections.Generic;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm.Tracing;
using Nethermind.Network;
using Nethermind.Store;

namespace Nethermind.JsonRpc.Modules.DebugModule
{
    public class DebugBridge : IDebugBridge
    {
        private readonly IConfigProvider _configProvider;
        private readonly ITracer _tracer;
        private readonly IBlockTree _blockTree;
        private Dictionary<string, IDb> _dbMappings;

        public DebugBridge(IConfigProvider configProvider, IReadOnlyDbProvider dbProvider, ITracer tracer, IBlockchainProcessor receiptsProcessor, IBlockTree blockTree)
        {
            receiptsProcessor.ProcessingQueueEmpty += (sender, args) => _receiptProcessedEvent.Set();
            _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
            _tracer = tracer ?? throw new ArgumentNullException(nameof(tracer));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            dbProvider = dbProvider ?? throw new ArgumentNullException(nameof(dbProvider));
            IDb blockInfosDb = dbProvider.BlockInfosDb ?? throw new ArgumentNullException(nameof(dbProvider.BlockInfosDb));
            IDb blocksDb = dbProvider.BlocksDb ?? throw new ArgumentNullException(nameof(dbProvider.BlocksDb));
            IDb headersDb = dbProvider.HeadersDb ?? throw new ArgumentNullException(nameof(dbProvider.HeadersDb));
            IDb receiptsDb = dbProvider.ReceiptsDb ?? throw new ArgumentNullException(nameof(dbProvider.ReceiptsDb));
            IDb codeDb = dbProvider.CodeDb ?? throw new ArgumentNullException(nameof(dbProvider.CodeDb));
            IDb pendingTxsDb = dbProvider.PendingTxsDb ?? throw new ArgumentNullException(nameof(dbProvider.PendingTxsDb));
            IDb traceDb = dbProvider.TraceDb ?? throw new ArgumentNullException(nameof(dbProvider.TraceDb));

            _dbMappings = new Dictionary<string, IDb>(StringComparer.InvariantCultureIgnoreCase)
            {
                {DbNames.State, dbProvider.StateDb},
                {DbNames.Storage, dbProvider.StateDb},
                {DbNames.BlockInfos, blockInfosDb},
                {DbNames.Blocks, blocksDb},
                {DbNames.Headers, headersDb},
                {DbNames.Code, codeDb},
                {DbNames.Receipts, receiptsDb},
                {DbNames.PendingTxs, pendingTxsDb},
                {DbNames.Trace, traceDb}
            };
        }

        public byte[] GetDbValue(string dbName, byte[] key)
        {
            return _dbMappings[dbName][key];
        }

        public GethLikeTxTrace GetTransactionTrace(Keccak transactionHash, GethTraceOptions gethTraceOptions = null)
        {
            return _tracer.Trace(transactionHash, gethTraceOptions ?? GethTraceOptions.Default);
        }

        public GethLikeTxTrace GetTransactionTrace(long blockNumber, int index, GethTraceOptions gethTraceOptions = null)
        {
            return _tracer.Trace(blockNumber, index, gethTraceOptions ?? GethTraceOptions.Default);
        }

        public GethLikeTxTrace GetTransactionTrace(Keccak blockHash, int index, GethTraceOptions gethTraceOptions = null)
        {
            return _tracer.Trace(blockHash, index, gethTraceOptions ?? GethTraceOptions.Default);
        }

        public GethLikeTxTrace GetTransactionTrace(Rlp blockRlp, Keccak transactionHash, GethTraceOptions gethTraceOptions = null)
        {
            return _tracer.Trace(blockRlp, transactionHash, gethTraceOptions ?? GethTraceOptions.Default);
        }

        public GethLikeTxTrace[] GetBlockTrace(Keccak blockHash, GethTraceOptions gethTraceOptions = null)
        {
            return _tracer.TraceBlock(blockHash, gethTraceOptions ?? GethTraceOptions.Default);
        }

        public GethLikeTxTrace[] GetBlockTrace(long blockNumber, GethTraceOptions gethTraceOptions = null)
        {
            return _tracer.TraceBlock(blockNumber, gethTraceOptions ?? GethTraceOptions.Default);
        }

        public GethLikeTxTrace[] GetBlockTrace(Rlp blockRlp, GethTraceOptions gethTraceOptions = null)
        {
            return _tracer.TraceBlock(blockRlp, gethTraceOptions ?? GethTraceOptions.Default);
        }

        public byte[] GetBlockRlp(Keccak blockHash)
        {
            return _dbMappings[DbNames.Blocks].Get(blockHash);
        }

        public byte[] GetBlockRlp(long number)
        {
            Keccak hash = _blockTree.FindHash(number);
            if (hash == null)
            {
                return null;
            }

            return _dbMappings[DbNames.Blocks].Get(hash);
        }

        public string GetConfigValue(string category, string name)
        {
            return _configProvider.GetRawValue(category, name);
        }

        private AutoResetEvent _receiptProcessedEvent = new AutoResetEvent(false);
    }
}