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
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.TxPools;
using Nethermind.Blockchain.Validators;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Store;
using Nethermind.Store.Repositories;
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
            MemDb blocksDb = new MemDb();
            MemDb headersDb = new MemDb();
            // so we automatically include in all tests my questionable decision of storing Head block header at 00...
            blocksDb.Set(Keccak.Zero, Rlp.Encode(Build.A.BlockHeader.TestObject).Bytes);

            _genesisBlock = genesisBlock;
            var blockInfoDb = new MemDb();
            ChainLevelInfoRepository = new ChainLevelInfoRepository(blockInfoDb);
            TestObjectInternal = new BlockTree(blocksDb, headersDb, blockInfoDb, ChainLevelInfoRepository,  RopstenSpecProvider.Instance, Substitute.For<ITxPool>(), NullLogManager.Instance);
        }

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
                Address beneficiary = blockBeneficiaries.Length == 0 ? Address.Zero : blockBeneficiaries[i%blockBeneficiaries.Length];
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
            Block current;
            if (_receiptStorage != null && blockIndex % 3 == 0)
            {
                Transaction[] transactions = new[]
                {
                    Build.A.Transaction.WithValue(1).WithData(Rlp.Encode(blockIndex).Bytes).Signed(_ecdsa, TestItem.PrivateKeyA, blockIndex + 1).TestObject,
                    Build.A.Transaction.WithValue(2).WithData(Rlp.Encode(blockIndex + 1).Bytes).Signed(_ecdsa, TestItem.PrivateKeyA, blockIndex + 1).TestObject
                };

                current = Build.A.Block
                    .WithNumber(blockIndex + 1)
                    .WithParent(parent)
                    .WithDifficulty(BlockHeaderBuilder.DefaultDifficulty - (splitFrom > parent.Number ? 0 : (ulong) splitVariant))
                    .WithTransactions(transactions)
                    .WithBloom(new Bloom())
                    .WithBeneficiary(beneficiary)
                    .TestObject;

                List<TxReceipt> receipts = new List<TxReceipt>();
                foreach (var transaction in current.Transactions)
                {
                    var logEntries = _logCreationFunction?.Invoke(current, transaction)?.ToArray() ?? Array.Empty<LogEntry>();
                    TxReceipt receipt = new TxReceipt
                    {
                        Logs = logEntries,
                        TxHash = transaction.Hash,
                        Bloom = new Bloom(logEntries)
                    };
                    _receiptStorage.Add(receipt, false);
                    receipts.Add(receipt);
                    current.Bloom.Add(receipt.Logs);
                }

                current.Header.TxRoot = current.CalculateTxRoot();
                current.Header.ReceiptsRoot = current.CalculateReceiptRoot(_specProvider, receipts.ToArray());
                current.Hash = BlockHeader.CalculateHash(current);
            }
            else
            {
                current = Build.A.Block.WithNumber(blockIndex + 1)
                    .WithParent(parent)
                    .WithDifficulty(BlockHeaderBuilder.DefaultDifficulty - (splitFrom > parent.Number ? 0 : (ulong) splitVariant))
                    .WithBeneficiary(beneficiary)
                    .TestObject;
            }

            return current;
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

        public static void ExtendTree(IBlockTree blockTree, int newChainLength)
        {
            Block previous = blockTree.RetrieveHeadBlock();
            int initialLength = (int) previous.Number + 1;
            for (int i = initialLength; i < newChainLength; i++)
            {
                previous = Build.A.Block.WithNumber(i).WithParent(previous).TestObject;
                blockTree.SuggestBlock(previous);
                blockTree.UpdateMainChain(new[] {previous});
            }
        }

        public BlockTreeBuilder WithTransactions(IReceiptStorage receiptStorage, ISpecProvider specProvider, Func<Block, Transaction, IEnumerable<LogEntry>> logsForBlockBuilder = null)
        {
            _specProvider = specProvider;
            _ecdsa = new EthereumEcdsa(specProvider, LimboLogs.Instance);
            _receiptStorage = receiptStorage;
            _logCreationFunction = logsForBlockBuilder;
            return this;
        }
    }
}