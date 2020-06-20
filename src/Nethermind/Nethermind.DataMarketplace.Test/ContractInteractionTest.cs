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
using System.Collections.Generic;
using System.Linq;
using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Core.Services.Models;
using Nethermind.Db;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Facade;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.TxPool;
using Nethermind.TxPool.Storages;
using Nethermind.Wallet;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Test
{
    public abstract class ContractInteractionTest
    {
        public class TestLogger : ILogger
        {
            public void Info(string text)
            {
                TestContext.WriteLine(text);
            }

            public void Warn(string text)
            {
                TestContext.WriteLine(text);
            }

            public void Debug(string text)
            {
                TestContext.WriteLine(text);
            }

            public void Trace(string text)
            {
                TestContext.WriteLine(text);
            }

            public void Error(string text, Exception ex = null)
            {
                TestContext.WriteLine(text);
            }

            public bool IsInfo { get; } = true;
            public bool IsWarn { get; } = true;
            public bool IsDebug { get; } = true;
            public bool IsTrace { get; } = true;
            public bool IsError { get; } = true;
        }

        protected IReleaseSpec _releaseSpec = Constantinople.Instance;
        protected Address _feeAccount;
        protected Address _consumerAccount;
        protected Address _providerAccount;
        protected DevWallet _wallet;
        protected BlockchainBridge _bridge;
        protected INdmBlockchainBridge _ndmBridge;
        protected IStateProvider _state;
        protected INdmConfig _ndmConfig;
        protected AbiEncoder _abiEncoder = new AbiEncoder();
        protected ILogManager _logManager = new OneLoggerLogManager(new TestLogger());
        protected Address _contractAddress;
        protected ITxPool _txPool;

        protected void Prepare()
        {
            _wallet = new DevWallet(new WalletConfig(), _logManager);
            _feeAccount = _wallet.GetAccounts()[0];
            _consumerAccount = _wallet.GetAccounts()[1];
            _providerAccount = _wallet.GetAccounts()[2];
            _ndmConfig = new NdmConfig();

            IReleaseSpec spec = _releaseSpec;
            ISpecProvider specProvider = new SingleReleaseSpecProvider(spec, 99);
            StateDb stateDb = new StateDb();
            _state = new StateProvider(stateDb, new StateDb(), _logManager);
            StorageProvider storageProvider = new StorageProvider(stateDb, _state, _logManager);
            _state.CreateAccount(_consumerAccount, 1000.Ether());
            _state.CreateAccount(_providerAccount, 1.Ether());
            _state.Commit(spec);
            _state.CommitTree();

            VirtualMachine machine = new VirtualMachine(_state, storageProvider, Substitute.For<IBlockhashProvider>(),
                specProvider, _logManager);
            TransactionProcessor processor = new TransactionProcessor(specProvider, _state, storageProvider, machine, _logManager);
            _bridge = new BlockchainBridge(processor);

            TxReceipt receipt = DeployContract(Bytes.FromHexString(ContractData.GetInitCode(_feeAccount)));
            ((NdmConfig) _ndmConfig).ContractAddress = receipt.ContractAddress.ToString();
            _contractAddress = receipt.ContractAddress;
            _txPool = new TxPool.TxPool(new InMemoryTxStorage(), Timestamper.Default,
                new EthereumEcdsa(specProvider.ChainId, _logManager), specProvider, new TxPoolConfig(), _state, _logManager);

            _ndmBridge = new NdmBlockchainBridge(_bridge, _bridge, _txPool);
        }

        protected TxReceipt DeployContract(byte[] initCode)
        {
            Transaction deployContract = new Transaction();
            deployContract.SenderAddress = _providerAccount;
            deployContract.GasLimit = 4000000;
            deployContract.Init = initCode;
            deployContract.Nonce = _bridge.GetNonce(_providerAccount);
            Keccak txHash = _bridge.SendTransaction(deployContract, TxHandlingOptions.None);
            TxReceipt receipt = _bridge.GetReceipt(txHash);
            Assert.AreEqual(StatusCode.Success, receipt.StatusCode, $"contract deployed {receipt.Error}");
            return receipt;
        }

        public class BlockchainBridge : IBlockchainBridge, ITxPoolBridge
        {
            private readonly TransactionProcessor _processor;

            public void NextBlockPlease(UInt256 timestamp)
            {
                _txIndex = 0;
                _headBlock = Build.A.Block.WithParent(Head).WithTimestamp(timestamp).TestObject;
                _headBlock.Body = _headBlock.Body.WithChangedTransactions(new Transaction[100]);
                _receiptsTracer.StartNewBlockTrace(_headBlock);
            }

            public IReadOnlyCollection<Address> GetWalletAccounts()
            {
                throw new NotImplementedException();
            }

            public Signature Sign(Address address, Keccak message)
            {
                throw new NotImplementedException();
            }
            
            public void Sign(Transaction transaction)
            {
                throw new NotImplementedException();
            }

            public int GetNetworkId()
            {
                return 99;
            }

            public GethLikeBlockTracer GethTracer { get; set; } = new GethLikeBlockTracer(GethTraceOptions.Default);

            public BlockchainBridge(TransactionProcessor processor)
            {
                _receiptsTracer = new BlockReceiptsTracer();
                _processor = processor;
                _tx = Build.A.Transaction.SignedAndResolved(new EthereumEcdsa(ChainId.Mainnet, LimboLogs.Instance), TestItem.PrivateKeyA).TestObject;
                _headBlock = Build.A.Block.WithNumber(1).WithTransactions(Enumerable.Repeat(_tx, 100).ToArray()).TestObject;

                _receiptsTracer.SetOtherTracer(GethTracer);
                _receiptsTracer.StartNewBlockTrace(_headBlock);
            }

            private Transaction _tx;
            private Block _headBlock;

            public Block Head => _headBlock;
            public long BestKnown { get; }
            public bool IsSyncing { get; }
            public bool IsMining { get; }

            public void RecoverTxSenders(Block block)
            {
                throw new NotImplementedException();
            }

            public void RecoverTxSender(Transaction tx)
            {
                throw new NotImplementedException();
            }

            public Keccak HeadHash => _headBlock.Hash;
            public Keccak GenesisHash => null;
            public Keccak PendingHash => null;
            public Block FindBlock(Keccak blockHash, BlockTreeLookupOptions options) => _headBlock.Hash == blockHash ? _headBlock : null;

            public Block FindBlock(long blockNumber, BlockTreeLookupOptions options) => _headBlock.Number == blockNumber ? _headBlock : null;

            public BlockHeader FindHeader(Keccak blockHash, BlockTreeLookupOptions options) => _headBlock.Hash == blockHash ? _headBlock.Header : null;

            public BlockHeader FindHeader(long blockNumber, BlockTreeLookupOptions options) => _headBlock.Number == blockNumber ? _headBlock.Header : null;
            public Keccak FindBlockHash(long blockNumber) => _headBlock.Number == blockNumber ? _headBlock.Hash : null;

            public bool IsMainChain(BlockHeader blockHeader) => blockHeader.Number == _headBlock.Number;

            public bool IsMainChain(Keccak blockHash) => _headBlock.Hash == blockHash;

            public (TxReceipt Receipt, Transaction Transaction) GetTransaction(Keccak txHash)
            {
                return (new TxReceipt(), new Transaction
                {
                    Hash = txHash
                });
            }

            public Transaction GetPendingTransaction(Keccak txHash)
            {
                throw new NotImplementedException();
            }

            public Transaction[] GetPendingTransactions()
            {
                throw new NotImplementedException();
            }

            private BlockReceiptsTracer _receiptsTracer;

            private int _txIndex = 0;

            public Keccak SendTransaction(Transaction tx, TxHandlingOptions txHandlingOptions)
            {
                tx.Hash = tx.CalculateHash();
                _headBlock.Transactions[_txIndex++] = tx;
                _receiptsTracer.StartNewTxTrace(tx.Hash);
                _processor.Execute(tx, Head?.Header, _receiptsTracer);
                _receiptsTracer.EndTxTrace();
                return tx.CalculateHash();
            }

            public TxReceipt GetReceipt(Keccak txHash) => _receiptsTracer.TxReceipts.Single(r => r?.TxHash == txHash);
            
            public Facade.BlockchainBridge.CallOutput Call(BlockHeader blockHeader, Transaction transaction)
            {
                CallOutputTracer tracer = new CallOutputTracer();
                _processor.Execute(transaction, Head?.Header, tracer);
                return new Facade.BlockchainBridge.CallOutput(tracer.ReturnValue, tracer.GasSpent, tracer.Error);
            }

            public Facade.BlockchainBridge.CallOutput EstimateGas(BlockHeader header, Transaction tx)
            {
                throw new NotImplementedException();
            }

            public long GetChainId()
            {
                throw new NotImplementedException();
            }

            public byte[] GetCode(Address address)
            {
                throw new NotImplementedException();
            }

            public byte[] GetCode(Keccak codeHash)
            {
                throw new NotImplementedException();
            }

            Dictionary<Address, UInt256> _nonces = new Dictionary<Address, UInt256>();

            public UInt256 GetNonce(Address address)
            {
                if (!_nonces.ContainsKey(address))
                {
                    _nonces[address] = 0;
                }

                return _nonces[address];
            }

            public void IncrementNonce(Address address)
            {
                var nonce = GetNonce(address);
                _nonces[address] = nonce + 1;
            }

            public byte[] GetStorage(Address address, UInt256 index, Keccak storageRoot)
            {
                throw new NotImplementedException();
            }
            
            public Account GetAccount(Address address, Keccak stateRoot)
            {
                throw new NotImplementedException();
            }

            public int NewBlockFilter()
            {
                throw new NotImplementedException();
            }

            public int NewPendingTransactionFilter()
            {
                throw new NotImplementedException();
            }

            public int NewFilter(BlockParameter fromBlock, BlockParameter toBlock, object address = null, IEnumerable<object> topics = null)
            {
                throw new NotImplementedException();
            }

            public void UninstallFilter(int filterId)
            {
                throw new NotImplementedException();
            }

            public bool FilterExists(int filterId)
            {
                throw new NotImplementedException();
            }

            public FilterLog[] GetLogFilterChanges(int filterId)
            {
                throw new NotImplementedException();
            }

            public Keccak[] GetBlockFilterChanges(int filterId)
            {
                throw new NotImplementedException();
            }

            public Keccak[] GetPendingTransactionFilterChanges(int filterId)
            {
                throw new NotImplementedException();
            }

            public FilterType GetFilterType(int filterId)
            {
                throw new NotImplementedException();
            }

            public FilterLog[] GetFilterLogs(int filterId)
            {
                throw new NotImplementedException();
            }

            public IEnumerable<FilterLog> GetLogs(BlockParameter fromBlock, BlockParameter toBlock, object address, IEnumerable<object> topics)
            {
                throw new NotImplementedException();
            }

            public void RunTreeVisitor(ITreeVisitor treeVisitor, Keccak stateRoot)
            {
                throw new NotImplementedException();
            }
        }
    }
}