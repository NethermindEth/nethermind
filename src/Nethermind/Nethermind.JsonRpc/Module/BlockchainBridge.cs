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
using System.Numerics;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.TransactionPools;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Store;
using Nethermind.Wallet;

namespace Nethermind.JsonRpc.Module
{
    [DoNotUseInSecuredContext("Not reviewed, work in progress")]
    public class BlockchainBridge : IBlockchainBridge
    {
        private readonly IBlockTree _blockTree;
        private readonly ITransactionPool _transactionPool;
        private readonly IFilterManager _filterManager;
        private readonly IFilterStore _filterStore;
        private readonly IEthereumSigner _signer;
        private readonly IStateProvider _stateProvider;
        private readonly ITransactionProcessor _transactionProcessor;
        private readonly IReceiptStorage _receiptStorage;
        private readonly IWallet _wallet;

        private ReaderWriterLockSlim _readerWriterLockSlim = new ReaderWriterLockSlim();

        public BlockchainBridge(IEthereumSigner signer,
            IStateProvider stateProvider,
            IBlockTree blockTree,
            ITransactionPool transactionPool,
            IReceiptStorage receiptStorage,
            IFilterStore filterStore,
            IFilterManager filterManager,
            IWallet wallet,
            ITransactionProcessor transactionProcessor)
        {
            _signer = signer ?? throw new ArgumentNullException(nameof(signer));
            _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _transactionPool = transactionPool;
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _filterStore = filterStore ?? throw new ArgumentException(nameof(filterStore));
            _filterManager = filterManager ?? throw new ArgumentException(nameof(filterManager));
            _wallet = wallet ?? throw new ArgumentException(nameof(wallet));
            _transactionProcessor = transactionProcessor ?? throw new ArgumentException(nameof(transactionProcessor));
        }

        public IReadOnlyCollection<Address> GetWalletAccounts()
        {
            return _wallet.GetAccounts();
        }

        public Signature Sign(Address address, Keccak message)
        {
            return _wallet.Sign(address, message);
        }

        public BlockHeader Head => _blockTree.Head;
        public BlockHeader BestSuggested => _blockTree.BestSuggested;
        public UInt256 BestKnown => _blockTree.BestKnownNumber;
        public bool IsSyncing => _blockTree.CanAcceptNewBlocks;

        public Block FindBlock(Keccak blockHash, bool mainChainOnly)
        {
            return _blockTree.FindBlock(blockHash, mainChainOnly);
        }

        public Block FindBlock(UInt256 blockNumber)
        {
            return _blockTree.FindBlock(blockNumber);
        }

        public Block RetrieveHeadBlock()
        {
            return _blockTree.FindBlock(_blockTree.Head.Hash, false);
        }

        public Block RetrieveGenesisBlock()
        {
            return _blockTree.FindBlock(_blockTree.Genesis.Hash, true);
        }

        public (TransactionReceipt Receipt, Transaction Transaction) GetTransaction(Keccak transactionHash)
        {
            TransactionReceipt receipt = _receiptStorage.Get(transactionHash);
            if (receipt.BlockHash == null) return (null, null);

            Block block = _blockTree.FindBlock(receipt.BlockHash, true);
            return (receipt, block.Transactions[receipt.Index]);
        }

        public Keccak GetBlockHash(Keccak transactionHash)
        {
            return _receiptStorage.Get(transactionHash).BlockHash;
        }

        public Keccak SendTransaction(Transaction transaction)
        {
            try
            {
                _readerWriterLockSlim.EnterWriteLock();
                _stateProvider.StateRoot = _blockTree.Head.StateRoot;

                if (transaction.SenderAddress == null) transaction.SenderAddress = _wallet.GetAccounts()[0];

                transaction.Nonce = _stateProvider.GetNonce(transaction.SenderAddress);
                _wallet.Sign(transaction, _blockTree.ChainId);
                transaction.Hash = Transaction.CalculateHash(transaction);

                if (_stateProvider.GetNonce(transaction.SenderAddress) != transaction.Nonce) throw new InvalidOperationException("Invalid nonce");

                _transactionPool.AddTransaction(transaction, _blockTree.Head.Number);

                _stateProvider.Reset();
                return transaction.Hash;
            }
            finally
            {
                _readerWriterLockSlim.ExitWriteLock();
            }
        }

        public TransactionReceipt GetTransactionReceipt(Keccak txHash)
        {
            return _receiptStorage.Get(txHash);
        }

        public byte[] Call(Block block, Transaction transaction)
        {
            try
            {
                _readerWriterLockSlim.EnterWriteLock();
                _stateProvider.StateRoot = _blockTree.Head.StateRoot;
                BlockHeader header = new BlockHeader(block.Hash, Keccak.OfAnEmptySequenceRlp, block.Beneficiary, block.Difficulty, block.Number + 1, (long) transaction.GasLimit, block.Timestamp + 1, Bytes.Empty);
                transaction.Nonce = _stateProvider.GetNonce(transaction.SenderAddress);
                transaction.Hash = Transaction.CalculateHash(transaction);
                (TransactionReceipt receipt, TransactionTrace trace) = _transactionProcessor.CallAndRestore(0, transaction, header, true);

                _stateProvider.Reset();
                return Bytes.FromHexString(trace.ReturnValue);
            }
            finally
            {
                _readerWriterLockSlim.ExitWriteLock();
            }
        }

        public byte[] GetCode(Address address)
        {
            try
            {
                _readerWriterLockSlim.EnterReadLock();
                _stateProvider.StateRoot = _blockTree.Head.StateRoot;
                return _stateProvider.GetCode(address);
            }
            finally
            {
                _readerWriterLockSlim.ExitReadLock();
            }
        }

        public byte[] GetCode(Keccak codeHash)
        {
            try
            {
                _readerWriterLockSlim.EnterReadLock();
                _stateProvider.StateRoot = _blockTree.Head.StateRoot;
                return _stateProvider.GetCode(codeHash);
            }
            finally
            {
                _readerWriterLockSlim.ExitReadLock();
            }
        }

        public BigInteger GetNonce(Address address)
        {
            try
            {
                _readerWriterLockSlim.EnterReadLock();
                _stateProvider.StateRoot = _blockTree.Head.StateRoot;
                return _stateProvider.GetNonce(address);
            }
            finally
            {
                _readerWriterLockSlim.ExitReadLock();
            }
        }

        public BigInteger GetBalance(Address address)
        {
            try
            {
                _readerWriterLockSlim.EnterReadLock();
                _stateProvider.StateRoot = _blockTree.Head.StateRoot;
                return _stateProvider.GetBalance(address);
            }
            finally
            {
                _readerWriterLockSlim.ExitReadLock();
            }
        }

        public Account GetAccount(Address address, Keccak stateRoot)
        {
            try
            {
                _readerWriterLockSlim.EnterReadLock();
                _stateProvider.StateRoot = stateRoot;
                return _stateProvider.GetAccount(address);
            }
            finally
            {
                _readerWriterLockSlim.ExitReadLock();
            }
        }

        public int GetNetworkId()
        {
            return _blockTree.ChainId;
        }

        public bool FilterExists(int filterId)
        {
            return _filterStore.FilterExists(filterId);
        }

        public FilterType GetFilterType(int filterId)
        {
            return _filterStore.GetFilterType(filterId);
        }

        public FilterLog[] GetFilterLogs(int filterId)
        {
            return _filterManager.GetLogs(filterId);
        }

        public FilterLog[] GetLogs(FilterBlock fromBlock, FilterBlock toBlock, object address = null,
            IEnumerable<object> topics = null)
        {
            LogFilter filter = _filterStore.CreateLogFilter(fromBlock, toBlock, address, topics, false);
            return new FilterLog[0];
        }

        public int NewFilter(FilterBlock fromBlock, FilterBlock toBlock,
            object address = null, IEnumerable<object> topics = null)
        {
            LogFilter filter = _filterStore.CreateLogFilter(fromBlock, toBlock, address, topics);
            _filterStore.SaveFilter(filter);
            return filter.Id;
        }

        public int NewBlockFilter()
        {
            BlockFilter filter = _filterStore.CreateBlockFilter(_blockTree.Head.Number);
            _filterStore.SaveFilter(filter);
            return filter.Id;
        }

        public void UninstallFilter(int filterId)
        {
            _filterStore.RemoveFilter(filterId);
        }

        public FilterLog[] GetLogFilterChanges(int filterId)
        {
            return _filterManager.PollLogs(filterId);
        }

        public Keccak[] GetBlockFilterChanges(int filterId)
        {
            return _filterManager.PollBlockHashes(filterId);
        }

        public Signature Sign(PrivateKey privateKey, Keccak message)
        {
            return _signer.Sign(privateKey, message);
        }
    }
}