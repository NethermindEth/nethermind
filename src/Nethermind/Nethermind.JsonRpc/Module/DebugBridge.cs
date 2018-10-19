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
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Store;

namespace Nethermind.JsonRpc.Module
{
    public class DebugBridge : IDebugBridge
    {
        private readonly IBlockchainProcessor _blockchainProcessor;
        private readonly ITxTracer _txTracer;
        private Dictionary<string, IDb> _dbMappings;

        public DebugBridge(IReadOnlyDbProvider dbProvider, ITxTracer txTracer, IBlockchainProcessor blockchainProcessor)
        {
            _blockchainProcessor = blockchainProcessor ?? throw new ArgumentNullException(nameof(blockchainProcessor));
            _txTracer = txTracer ?? throw new ArgumentNullException(nameof(txTracer));
            dbProvider = dbProvider ?? throw new ArgumentNullException(nameof(dbProvider));
            IDb blockInfosDb = dbProvider.BlockInfosDb ?? throw new ArgumentNullException(nameof(dbProvider.BlockInfosDb));
            IDb blocksDb = dbProvider.BlocksDb ?? throw new ArgumentNullException(nameof(dbProvider.BlocksDb));
            IDb receiptsDb = dbProvider.ReceiptsDb ?? throw new ArgumentNullException(nameof(dbProvider.ReceiptsDb));
            IDb codeDb = dbProvider.CodeDb ?? throw new ArgumentNullException(nameof(dbProvider.CodeDb));
            
            _dbMappings = new Dictionary<string, IDb>(StringComparer.InvariantCultureIgnoreCase)
            {
                {DbNames.State, dbProvider.StateDb},
                {DbNames.Storage, dbProvider.StateDb},
                {DbNames.BlockInfos, blockInfosDb},
                {DbNames.Blocks, blocksDb},
                {DbNames.Code, codeDb},
                {DbNames.Receipts, receiptsDb}
            };    
        }
        
        public byte[] GetDbValue(string dbName, byte[] key)
        {
            return _dbMappings[dbName][key];
        }
        
        public TransactionTrace GetTransactionTrace(Keccak transactionHash)
        {
            return _txTracer.Trace(transactionHash);
        }

        public TransactionTrace GetTransactionTrace(UInt256 blockNumber, int index)
        {
            return _txTracer.Trace(blockNumber, index);
        }

        public TransactionTrace GetTransactionTrace(Keccak blockHash, int index)
        {
            return _txTracer.Trace(blockHash, index);
        }

        public BlockTrace GetBlockTrace(Keccak blockHash)
        {
            return _txTracer.TraceBlock(blockHash);
        }

        public BlockTrace GetBlockTrace(UInt256 blockNumber)
        {
            return _txTracer.TraceBlock(blockNumber);
        }
        
        public void AddTxData(Keccak blockHash)
        {
            _blockchainProcessor.AddTxData(blockHash);
        }
    }
}