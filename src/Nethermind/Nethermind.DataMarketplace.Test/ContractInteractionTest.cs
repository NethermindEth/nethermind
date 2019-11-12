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
using Nethermind.Abi;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.TxPools;
using Nethermind.Blockchain.TxPools.Storages;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Core.Specs;
using Nethermind.Core.Specs.Forks;
using Nethermind.Core.Test.Builders;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Core.Services.Models;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Facade;
using Nethermind.Store;
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
            _bridge = new BlockchainBridge(processor, _releaseSpec);
            
            TxReceipt receipt = DeployContract(Bytes.FromHexString(ContractData.GetInitCode(_feeAccount)));
            ((NdmConfig) _ndmConfig).ContractAddress = receipt.ContractAddress.ToString();
            _contractAddress = receipt.ContractAddress;
            _txPool = new TxPool(new InMemoryTxStorage(), new Timestamper(),
                new EthereumEcdsa(specProvider, _logManager), specProvider, new TxPoolConfig(), _state, _logManager);
            
            _ndmBridge = new NdmBlockchainBridge(_bridge, _txPool);
        }

        protected TxReceipt DeployContract(byte[] initCode)
        {
            Transaction deployContract = new Transaction();
            deployContract.SenderAddress = _providerAccount;
            deployContract.GasLimit = 4000000;
            deployContract.Init = initCode;
            deployContract.Nonce = _bridge.GetNonce(_providerAccount);
            Keccak txHash = _bridge.SendTransaction(deployContract);
            TxReceipt receipt = _bridge.GetReceipt(txHash);
            Assert.AreEqual(StatusCode.Success, receipt.StatusCode, $"contract deployed {receipt.Error}");
            return receipt;
        }

        public class BlockchainBridge : IBlockchainBridge
        {
            private readonly TransactionProcessor _processor;
            private readonly IReleaseSpec _spec;

            public void NextBlockPlease(UInt256 timestamp)
            {
                _txIndex = 0;
                _headBlock = Build.A.Block.WithParent(Head).WithTimestamp(timestamp).TestObject;
                _headBlock.Body.Transactions = new Transaction[100];
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

            public Signature Sign(Address address, byte[] message)
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

            public BlockchainBridge(TransactionProcessor processor, IReleaseSpec spec)
            {
                _spec = spec;
                _receiptsTracer = new BlockReceiptsTracer(new SingleReleaseSpecProvider(_spec, 99), Substitute.For<IStateProvider>());
                _processor = processor;
                _receiptsTracer.SetOtherTracer(GethTracer);
                _receiptsTracer.StartNewBlockTrace(_headBlock);
            }

            private Block _headBlock = Build.A.Block.WithNumber(1).WithTransactions(new Transaction[100]).TestObject;

            public BlockHeader Head => _headBlock.Header;
            public BlockHeader BestSuggested { get; }
            public long BestKnown { get; }
            public bool IsSyncing { get; }
            public void RecoverTxSenders(Block block)
            {
                throw new NotImplementedException();
            }

            public void RecoverTxSender(Transaction tx, long blockNumber)
            {
                throw new NotImplementedException();
            }

            public Block FindBlock(Keccak blockHash) => _headBlock.Hash == blockHash ? _headBlock : null;

            public Block FindBlock(long blockNumber) => _headBlock.Number == blockNumber ? _headBlock : null;

            public Block FindLatestBlock() => _headBlock;

            public Block FindPendingBlock()
            {
                throw new NotImplementedException();
            }

            public BlockHeader FindHeader(Keccak blockHash)
            {
                throw new NotImplementedException();
            }

            public BlockHeader FindHeader(long blockNumber)
            {
                throw new NotImplementedException();
            }

            public BlockHeader FindGenesisHeader()
            {
                throw new NotImplementedException();
            }

            public BlockHeader FindHeadHeader()
            {
                throw new NotImplementedException();
            }

            public BlockHeader FindEarliestHeader()
            {
                throw new NotImplementedException();
            }

            public BlockHeader FindLatestHeader()
            {
                throw new NotImplementedException();
            }

            public BlockHeader FindPendingHeader()
            {
                throw new NotImplementedException();
            }

            public Block FindEarliestBlock()
            {
                throw new NotImplementedException();
            }

            public Block FindHeadBlock()
            {
                throw new NotImplementedException();
            }

            public Block FindGenesisBlock()
            {
                throw new NotImplementedException();
            }

            public (TxReceipt Receipt, Transaction Transaction) GetTransaction(Keccak transactionHash)
            {
                return (new TxReceipt(), new Transaction
                {
                    Hash = transactionHash
                });
            }

            public Keccak GetBlockHash(Keccak transactionHash)
            {
                throw new NotImplementedException();
            }

            private BlockReceiptsTracer _receiptsTracer;

            private int _txIndex = 0;

            public Keccak SendTransaction(Transaction transaction, bool isOwn = false)
            {
                transaction.Hash = Transaction.CalculateHash(transaction);
                _headBlock.Transactions[_txIndex++] = transaction;
                _receiptsTracer.StartNewTxTrace(transaction.Hash);
                _processor.Execute(transaction, Head, _receiptsTracer);
                _receiptsTracer.EndTxTrace();
                return Transaction.CalculateHash(transaction);
            }

            public TxReceipt GetReceipt(Keccak txHash) => _receiptsTracer.TxReceipts.Single(r => r?.TxHash == txHash);

            public TxReceipt[] GetReceipts(Block block) => block.Transactions.Select(t => GetReceipt(t.Hash)).ToArray();

            public Facade.BlockchainBridge.CallOutput Call(BlockHeader blockHeader, Transaction transaction)
            {
                CallOutputTracer tracer = new CallOutputTracer();
                _processor.Execute(transaction, Head, tracer);
                return new Facade.BlockchainBridge.CallOutput(tracer.ReturnValue, tracer.GasSpent, tracer.Error);
            }

            public long EstimateGas(Block block, Transaction transaction)
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

            public UInt256 GetBalance(Address address)
            {
                throw new NotImplementedException();
            }

            public byte[] GetStorage(Address address, UInt256 index)
            {
                throw new NotImplementedException();
            }

            public byte[] GetStorage(Address address, UInt256 index, Keccak storageRoot)
            {
                throw new NotImplementedException();
            }

            public Account GetAccount(Address address)
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

            public int NewFilter(FilterBlock fromBlock, FilterBlock toBlock, object address = null, IEnumerable<object> topics = null)
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

            public FilterLog[] GetLogs(FilterBlock fromBlock, FilterBlock toBlock, object address = null, IEnumerable<object> topics = null)
            {
                throw new NotImplementedException();
            }

            public void RunTreeVisitor(ITreeVisitor treeVisitor, Keccak stateRoot)
            {
                throw new NotImplementedException();
            }

            public TxPoolInfo GetTxPoolInfo()
            {
                throw new NotImplementedException();
            }
        }
    }
}