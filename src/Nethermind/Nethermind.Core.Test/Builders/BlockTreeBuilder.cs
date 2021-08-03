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
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Specs;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;
using Nethermind.State.Repositories;
using Nethermind.Db.Blooms;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Core.Test.Builders
{
    public class BlockTreeBuilder : BuilderBase<BlockTree>
    {
        private readonly Block _genesisBlock;
        private IReceiptStorage _receiptStorage;

        private bool _onlyHeaders;

        public BlockTreeBuilder()
            : this(Build.A.Block.Genesis.TestObject)
        {
        }

        public BlockTreeBuilder(Block genesisBlock)
        {
            BlocksDb = new MemDb();
            HeadersDb = new MemDb();
            BlockInfoDb = new MemDb();
            
            // so we automatically include in all tests my questionable decision of storing Head block header at 00...
            BlocksDb.Set(Keccak.Zero, Rlp.Encode(Build.A.BlockHeader.TestObject).Bytes);
            _genesisBlock = genesisBlock;
            ChainLevelInfoRepository = new ChainLevelInfoRepository(BlockInfoDb);
            TestObjectInternal = new BlockTree(BlocksDb, HeadersDb, BlockInfoDb, ChainLevelInfoRepository, RopstenSpecProvider.Instance, Substitute.For<IBloomStorage>(), LimboLogs.Instance);
        }

        public MemDb BlocksDb { get; set; }

        public MemDb HeadersDb { get; set; }

        public MemDb BlockInfoDb { get; set; }

        public ChainLevelInfoRepository ChainLevelInfoRepository { get; private set; }

        public BlockTreeBuilder OfHeadersOnly
        {
            get
            {
                _onlyHeaders = true;
                return this;
            }
        }

        public BlockTreeBuilder OfChainLength(int chainLength, int splitVariant = 0, int splitFrom = 0, params Address[] blockBeneficiaries)
        {
            OfChainLength(out _, chainLength, splitVariant, splitFrom, blockBeneficiaries);
            return this;
        }

        private ISpecProvider _specProvider;
        private IEthereumEcdsa _ecdsa;
        private Func<Block, Transaction, IEnumerable<LogEntry>> _logCreationFunction;

        public BlockTreeBuilder OfChainLength(out Block headBlock, int chainLength, int splitVariant = 0, int splitFrom = 0, params Address[] blockBeneficiaries)
        {
            Block current = _genesisBlock;
            headBlock = _genesisBlock;

            bool skipGenesis = TestObjectInternal.Genesis != null;
            for (int i = 0; i < chainLength; i++)
            {
                Address beneficiary = blockBeneficiaries.Length == 0 ? Address.Zero : blockBeneficiaries[i % blockBeneficiaries.Length];
                headBlock = current;
                if (_onlyHeaders)
                {
                    if (!(current.IsGenesis && skipGenesis))
                    {
                        TestObjectInternal.SuggestHeader(current.Header);
                    }

                    Block parent = current;
                    current = CreateBlock(splitVariant, splitFrom, i, parent, beneficiary);
                }
                else
                {
                    if (!(current.IsGenesis && skipGenesis))
                    {
                        AddBlockResult result = TestObjectInternal.SuggestBlock(current);
                        Assert.AreEqual(AddBlockResult.Added, result, $"Adding {current.ToString(Block.Format.Short)} at split variant {splitVariant}");

                        TestObjectInternal.UpdateMainChain(current);
                    }

                    Block parent = current;

                    current = CreateBlock(splitVariant, splitFrom, i, parent, beneficiary);
                }
            }

            return this;
        }

        private Block CreateBlock(int splitVariant, int splitFrom, int blockIndex, Block parent, Address beneficiary)
        {
            Block currentBlock;
            if (_receiptStorage != null && blockIndex % 3 == 0)
            {
                Transaction[] transactions = new[]
                {
                    Build.A.Transaction.WithValue(1).WithData(Rlp.Encode(blockIndex).Bytes).Signed(_ecdsa, TestItem.PrivateKeyA, _specProvider.GetSpec(blockIndex + 1).IsEip155Enabled).TestObject,
                    Build.A.Transaction.WithValue(2).WithData(Rlp.Encode(blockIndex + 1).Bytes).Signed(_ecdsa, TestItem.PrivateKeyA, _specProvider.GetSpec(blockIndex + 1).IsEip155Enabled).TestObject
                };

                currentBlock = Build.A.Block
                    .WithNumber(blockIndex + 1)
                    .WithParent(parent)
                    .WithDifficulty(BlockHeaderBuilder.DefaultDifficulty - (splitFrom > parent.Number ? 0 : (ulong) splitVariant))
                    .WithTransactions(transactions)
                    .WithBloom(new Bloom())
                    .WithBeneficiary(beneficiary)
                    .TestObject;

                List<TxReceipt> receipts = new List<TxReceipt>();
                foreach (var transaction in currentBlock.Transactions)
                {
                    var logEntries = _logCreationFunction?.Invoke(currentBlock, transaction)?.ToArray() ?? Array.Empty<LogEntry>();
                    TxReceipt receipt = new TxReceipt
                    {
                        Logs = logEntries,
                        TxHash = transaction.Hash,
                        Bloom = new Bloom(logEntries),
                        BlockNumber = currentBlock.Number,
                        BlockHash = currentBlock.Hash
                    };

                    receipts.Add(receipt);
                    currentBlock.Bloom.Add(receipt.Logs);
                }

                currentBlock.Header.TxRoot = new TxTrie(currentBlock.Transactions).RootHash;
                var txReceipts = receipts.ToArray();
                currentBlock.Header.ReceiptsRoot = new ReceiptTrie(_specProvider.GetSpec(currentBlock.Number), txReceipts).RootHash;
                currentBlock.Header.Hash = currentBlock.CalculateHash();
                foreach (var txReceipt in txReceipts)
                {
                    txReceipt.BlockHash = currentBlock.Hash;
                }

                _receiptStorage.Insert(currentBlock, txReceipts);
            }
            else
            {
                currentBlock = Build.A.Block.WithNumber(blockIndex + 1)
                    .WithParent(parent)
                    .WithDifficulty(BlockHeaderBuilder.DefaultDifficulty - (splitFrom > parent.Number ? 0 : (ulong) splitVariant))
                    .WithBeneficiary(beneficiary)
                    .TestObject;
            }

            currentBlock.Header.AuRaStep = blockIndex;

            return currentBlock;
        }

        public BlockTreeBuilder WithOnlySomeBlocksProcessed(int chainLength, int processedChainLength)
        {
            Block current = _genesisBlock;
            for (int i = 0; i < chainLength; i++)
            {
                TestObjectInternal.SuggestBlock(current);
                if (current.Number < processedChainLength)
                {
                    TestObjectInternal.UpdateMainChain(current);
                }

                current = Build.A.Block.WithNumber(i + 1).WithParent(current).WithDifficulty(BlockHeaderBuilder.DefaultDifficulty).TestObject;
            }

            return this;
        }

        public static void AddBlock(IBlockTree blockTree, Block block)
        {
            blockTree.SuggestBlock(block);
            blockTree.UpdateMainChain(new[] {block}, true);
        }

        public BlockTreeBuilder WithBlocks(params Block[] blocks)
        {
            int counter = 0;
            if (blocks.Length == 0)
            {
                return this;
            }

            if (blocks[0].Number != 0)
            {
                throw new ArgumentException("First block does not have block number 0.");
            }

            foreach (Block block in blocks)
            {
                if (block.Number != counter++)
                {
                    throw new ArgumentException("Block numbers are not consecutively increasing.");
                }

                TestObjectInternal.SuggestBlock(block);
                TestObjectInternal.UpdateMainChain(new[] {block}, true);
            }

            return this;
        }

        public static void ExtendTree(IBlockTree blockTree, long newChainLength)
        {
            Block previous = blockTree.RetrieveHeadBlock();
            long initialLength = previous.Number + 1;
            for (long i = initialLength; i < newChainLength; i++)
            {
                previous = Build.A.Block.WithNumber(i).WithParent(previous).TestObject;
                blockTree.SuggestBlock(previous);
                blockTree.UpdateMainChain(new[] {previous}, true);
            }
        }

        public BlockTreeBuilder WithTransactions(IReceiptStorage receiptStorage, ISpecProvider specProvider, Func<Block, Transaction, IEnumerable<LogEntry>> logsForBlockBuilder = null)
        {
            _specProvider = specProvider;
            _ecdsa = new EthereumEcdsa(specProvider.ChainId, LimboLogs.Instance);
            _receiptStorage = receiptStorage;
            _logCreationFunction = logsForBlockBuilder;
            return this;
        }
    }
}
