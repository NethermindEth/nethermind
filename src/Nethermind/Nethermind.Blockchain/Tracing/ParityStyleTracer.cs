//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Linq;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Blockchain.Tracing
{
    public class ParityStyleTracer : IParityStyleTracer
    {
        private readonly IBlockTree _blockTree;
        private readonly IBlockchainProcessor _processor;
        private readonly IReceiptStorage _receiptStorage;

        public ParityStyleTracer(IBlockchainProcessor processor, IReceiptStorage receiptStorage, IBlockTree blockTree)
        {
            _processor = processor ?? throw new ArgumentNullException(nameof(processor));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        }

        public ParityLikeTxTrace ParityTrace(Keccak txHash, ParityTraceTypes traceTypes)
        {
            TxReceipt txReceipt = _receiptStorage.Find(txHash);
            Block block = _blockTree.FindBlock(txReceipt.BlockNumber, BlockTreeLookupOptions.RequireCanonical);
            if (block == null) throw new InvalidOperationException("Only historical blocks");

            return ParityTrace(block, txHash, traceTypes);
        }

        public ParityLikeTxTrace[] ParityTraceBlock(long blockNumber, ParityTraceTypes traceTypes)
        {
            Block block = _blockTree.FindBlock(blockNumber, BlockTreeLookupOptions.RequireCanonical);
            return ParityTraceBlock(block, traceTypes);
        }

        private TransactionDecoder _transactionDecoder = new TransactionDecoder();

        public ParityLikeTxTrace ParityTraceRawTransaction(byte[] txRlp, ParityTraceTypes traceTypes)
        {
            BlockHeader headBlockHeader = _blockTree.Head;
            BlockHeader traceHeader = new BlockHeader(
                headBlockHeader.Hash,
                Keccak.OfAnEmptySequenceRlp,
                Address.Zero,
                headBlockHeader.Difficulty,
                headBlockHeader.Number + 1,
                headBlockHeader.GasLimit,
                headBlockHeader.Timestamp + 1,
                headBlockHeader.ExtraData);

            Transaction tx = _transactionDecoder.Decode(new RlpStream(txRlp));
            Block block = new Block(traceHeader, new[] {tx}, Enumerable.Empty<BlockHeader>());
            traceHeader.Author = Address.Zero;
            traceHeader.TotalDifficulty = headBlockHeader.TotalDifficulty + traceHeader.Difficulty;

            return ParityTraceBlock(block, traceTypes)[0];
        }

        public ParityLikeTxTrace[] ParityTraceBlock(Keccak blockHash, ParityTraceTypes traceTypes)
        {
            Block block = _blockTree.FindBlock(blockHash, BlockTreeLookupOptions.None);
            return ParityTraceBlock(block, traceTypes);
        }

        private ParityLikeTxTrace ParityTrace(Block block, Keccak txHash, ParityTraceTypes traceTypes)
        {
            ParityLikeBlockTracer listener = new ParityLikeBlockTracer(txHash, traceTypes);
            _processor.Process(block, ProcessingOptions.ForceProcessing | ProcessingOptions.ReadOnlyChain, listener);
            return listener.BuildResult().SingleOrDefault();
        }

        private ParityLikeTxTrace[] ParityTraceBlock(Block block, ParityTraceTypes traceTypes)
        {
            if (block == null) throw new InvalidOperationException("Only canonical, historical blocks supported");

            if (block.Number != 0)
            {
                BlockHeader parent = _blockTree.FindParentHeader(block.Header, BlockTreeLookupOptions.None);
                if (!_blockTree.IsMainChain(parent.Hash)) throw new InvalidOperationException("Cannot trace orphaned blocks");
            }

            ParityLikeBlockTracer listener = new ParityLikeBlockTracer(traceTypes);
            _processor.Process(block, ProcessingOptions.ForceProcessing | ProcessingOptions.ReadOnlyChain | ProcessingOptions.NoValidation, listener);
            return listener.BuildResult().ToArray();
        }
    }
}