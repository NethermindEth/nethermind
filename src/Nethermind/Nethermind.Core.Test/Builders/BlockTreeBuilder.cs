// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
using NSubstitute;
using NUnit.Framework;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Test.Builders
{
    public class BlockTreeBuilder : BuilderBase<BlockTree>
    {
        private readonly Block _genesisBlock;
        private readonly ISpecProvider _specProvider;
        private IReceiptStorage? _receiptStorage;
        private IEthereumEcdsa? _ecdsa;
        private Func<Block, Transaction, IEnumerable<LogEntry>>? _logCreationFunction;

        private bool _onlyHeaders;

        public BlockTreeBuilder(ISpecProvider specProvider)
            : this(Build.A.Block.Genesis.TestObject, specProvider)
        {
        }

        public BlockTreeBuilder(Block genesisBlock, ISpecProvider specProvider)
        {
            BlocksDb = new MemDb();
            HeadersDb = new MemDb();
            BlockInfoDb = new MemDb();
            MetadataDb = new MemDb();

            // so we automatically include in all tests my questionable decision of storing Head block header at 00...
            BlocksDb.Set(Keccak.Zero, Rlp.Encode(Build.A.BlockHeader.TestObject).Bytes);
            _genesisBlock = genesisBlock;
            _specProvider = specProvider;
            ChainLevelInfoRepository = new ChainLevelInfoRepository(BlockInfoDb);
            TestObjectInternal = new BlockTree(BlocksDb, HeadersDb, BlockInfoDb, ChainLevelInfoRepository, specProvider, Substitute.For<IBloomStorage>(), LimboLogs.Instance);
        }

        public MemDb BlocksDb { get; set; }

        public MemDb HeadersDb { get; set; }

        public MemDb BlockInfoDb { get; set; }

        public MemDb MetadataDb { get; set; }

        public ChainLevelInfoRepository ChainLevelInfoRepository { get; private set; }

        public BlockTreeBuilder OfHeadersOnly
        {
            get
            {
                _onlyHeaders = true;
                return this;
            }
        }

        public BlockTreeBuilder WithPostMergeRules()
        {
            PostMergeBlockTree = true;
            return this;
        }

        public bool PostMergeBlockTree { get; set; }


        public BlockTreeBuilder OfChainLength(int chainLength, int splitVariant = 0, int splitFrom = 0, bool withWithdrawals = false, params Address[] blockBeneficiaries)
        {
            OfChainLength(out _, chainLength, splitVariant, splitFrom, withWithdrawals, blockBeneficiaries);
            return this;
        }

        public BlockTreeBuilder OfChainLength(out Block headBlock, int chainLength, int splitVariant = 0, int splitFrom = 0, bool withWithdrawals = false, params Address[] blockBeneficiaries)
        {
            Block current = _genesisBlock;
            headBlock = _genesisBlock;

            bool skipGenesis = TestObjectInternal.Genesis is not null;
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
                    current = CreateBlock(splitVariant, splitFrom, i, parent, withWithdrawals, beneficiary);
                }
                else
                {
                    if (!(current.IsGenesis && skipGenesis))
                    {
                        AddBlockResult result = TestObjectInternal.SuggestBlock(current);
                        Assert.That(result, Is.EqualTo(AddBlockResult.Added), $"Adding {current.ToString(Block.Format.Short)} at split variant {splitVariant}");

                        TestObjectInternal.UpdateMainChain(current);
                    }

                    Block parent = current;

                    current = CreateBlock(splitVariant, splitFrom, i, parent, withWithdrawals, beneficiary);
                }
            }

            return this;
        }

        private Block CreateBlock(int splitVariant, int splitFrom, int blockIndex, Block parent, bool withWithdrawals, Address beneficiary)
        {
            Block currentBlock;
            BlockBuilder currentBlockBuilder = Build.A.Block
                .WithNumber(blockIndex + 1)
                .WithParent(parent)
                .WithWithdrawals(withWithdrawals ? new[] { TestItem.WithdrawalA_1Eth } : null)
                .WithBeneficiary(beneficiary);

            if (PostMergeBlockTree)
                currentBlockBuilder.WithPostMergeRules();
            else
                currentBlockBuilder.WithDifficulty(BlockHeaderBuilder.DefaultDifficulty -
                                                                      (splitFrom > parent.Number ? 0 : (ulong)splitVariant));

            if (_receiptStorage is not null && blockIndex % 3 == 0)
            {
                Transaction[] transactions = new[]
                {
                    Build.A.Transaction.WithValue(1).WithData(Rlp.Encode(blockIndex).Bytes).Signed(_ecdsa!, TestItem.PrivateKeyA, _specProvider!.GetSpec(blockIndex + 1, null).IsEip155Enabled).TestObject,
                    Build.A.Transaction.WithValue(2).WithData(Rlp.Encode(blockIndex + 1).Bytes).Signed(_ecdsa!, TestItem.PrivateKeyA, _specProvider!.GetSpec(blockIndex + 1, null).IsEip155Enabled).TestObject
                };

                currentBlock = currentBlockBuilder
                    .WithTransactions(transactions)
                    .WithBloom(new Bloom())
                    .TestObject;

                List<TxReceipt> receipts = new();
                foreach (Transaction transaction in currentBlock.Transactions)
                {
                    LogEntry[] logEntries = _logCreationFunction?.Invoke(currentBlock, transaction).ToArray() ?? Array.Empty<LogEntry>();
                    TxReceipt receipt = new()
                    {
                        Logs = logEntries,
                        TxHash = transaction.Hash,
                        Bloom = new Bloom(logEntries),
                        BlockNumber = currentBlock.Number,
                        BlockHash = currentBlock.Hash
                    };

                    receipts.Add(receipt);
                    currentBlock.Bloom!.Add(receipt.Logs);
                }

                currentBlock.Header.TxRoot = new TxTrie(currentBlock.Transactions).RootHash;
                TxReceipt[] txReceipts = receipts.ToArray();
                currentBlock.Header.ReceiptsRoot = new ReceiptTrie(_specProvider.GetSpec(currentBlock.Header), txReceipts).RootHash;
                currentBlock.Header.Hash = currentBlock.CalculateHash();
                foreach (TxReceipt txReceipt in txReceipts)
                {
                    txReceipt.BlockHash = currentBlock.Hash;
                }

                _receiptStorage.Insert(currentBlock, txReceipts);
            }
            else
            {
                currentBlock = currentBlockBuilder
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
            blockTree.UpdateMainChain(new[] { block }, true);
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
                TestObjectInternal.UpdateMainChain(new[] { block }, true);
            }

            return this;
        }

        public static void ExtendTree(IBlockTree blockTree, long newChainLength)
        {
            Block previous = blockTree.RetrieveHeadBlock()!;
            long initialLength = previous.Number + 1;
            for (long i = initialLength; i < newChainLength; i++)
            {
                previous = Build.A.Block.WithNumber(i).WithParent(previous).TestObject;
                blockTree.SuggestBlock(previous);
                blockTree.UpdateMainChain(new[] { previous }, true);
            }
        }

        public BlockTreeBuilder WithTransactions(IReceiptStorage receiptStorage, Func<Block, Transaction, IEnumerable<LogEntry>>? logsForBlockBuilder = null)
        {
            _ecdsa = new EthereumEcdsa(TestObjectInternal.ChainId, LimboLogs.Instance);
            _receiptStorage = receiptStorage;
            _logCreationFunction = logsForBlockBuilder;
            return this;
        }
    }
}
