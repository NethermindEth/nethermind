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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Comparers;
using Nethermind.Blockchain.Validators;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool.Filters;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.TxPool.Test
{
    [TestFixture]
    public class TxPoolTests
    {
        protected ILogManager _logManager;
        protected IEthereumEcdsa _ethereumEcdsa;
        protected ISpecProvider _specProvider;
        protected TxPool _txPool;
        protected IStateProvider _stateProvider;
        protected IBlockTree _blockTree;
        
        protected int _txGasLimit = 1_000_000;

        [SetUp]
        public void Setup()
        {
            _logManager = LimboLogs.Instance;
            _specProvider = RopstenSpecProvider.Instance;
            _ethereumEcdsa = new EthereumEcdsa(_specProvider.ChainId, _logManager);
            var trieStore = new TrieStore(new MemDb(), _logManager);
            var codeDb = new MemDb();
            _stateProvider = new StateProvider(trieStore, codeDb, _logManager);
            _blockTree = Substitute.For<IBlockTree>();
            Block block =  Build.A.Block.WithNumber(0).TestObject;
            _blockTree.Head.Returns(block);
            _blockTree.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(10000000).TestObject);
        }

        [Test]
        public void should_add_peers()
        {
            _txPool = CreatePool();
            var peers = GetPeers();

            foreach ((ITxPoolPeer peer, _) in peers)
            {
                _txPool.AddPeer(peer);
            }
        }

        [Test]
        public void should_delete_peers()
        {
            _txPool = CreatePool();
            var peers = GetPeers();

            foreach ((ITxPoolPeer peer, _) in peers)
            {
                _txPool.AddPeer(peer);
            }

            foreach ((ITxPoolPeer peer, _) in peers)
            {
                _txPool.RemovePeer(peer.Id);
            }
        }

        [Test]
        public void should_ignore_transactions_with_different_chain_id()
        {
            _txPool = CreatePool();
            EthereumEcdsa ecdsa = new(ChainId.Mainnet, _logManager);
            Transaction tx = Build.A.Transaction.SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            _txPool.GetPendingTransactions().Length.Should().Be(0);
            result.Should().Be(AcceptTxResult.Invalid);
        }
        
        [Test]
        public void should_ignore_transactions_with_insufficient_intrinsic_gas()
        {
            _txPool = CreatePool();
            Transaction tx = Build.A.Transaction
                .WithData(new byte[] 
                {
                    127, 243, 106, 181, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 145, 162, 136, 9, 81, 126, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                    0, 128, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 188, 120, 128, 96, 158, 141, 79, 126, 233, 131, 209, 47, 215, 166, 85, 190, 220, 187, 180, 115, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                    0, 0, 0, 96, 44, 207, 221, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 233, 29, 21, 62, 11, 65, 81, 138, 44, 232, 221, 61, 121,
                    68, 250, 134, 52, 99, 169, 125, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 183, 211, 17, 226, 235, 85, 242, 246, 138, 148, 64, 218, 56, 231, 152, 146, 16, 185, 160, 94, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 22, 226, 139,
                    67, 163, 88, 22, 43, 150, 247, 11, 77, 225, 76, 152, 164, 70, 95, 37
                })
                .SignedAndResolved()
                .TestObject;

            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            _txPool.GetPendingTransactions().Length.Should().Be(0);
            result.Should().Be(AcceptTxResult.Invalid);;
        }

        [Test]
        public void should_not_ignore_old_scheme_signatures()
        {
            _txPool = CreatePool();
            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, false).TestObject;
            EnsureSenderBalance(tx);
            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            _txPool.GetPendingTransactions().Length.Should().Be(1);
            result.Should().Be(AcceptTxResult.Accepted);
        }

        [Test]
        public void should_ignore_already_known()
        {
            _txPool = CreatePool();
            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx);
            AcceptTxResult result1 = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            AcceptTxResult result2 = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            _txPool.GetPendingTransactions().Length.Should().Be(1);
            result1.Should().Be(AcceptTxResult.Accepted);
            result2.Should().Be(AcceptTxResult.AlreadyKnown);
        }

        [Test]
        public void should_add_valid_transactions_recovering_its_address()
        {
            _txPool = CreatePool();
            Transaction tx = Build.A.Transaction
                .WithGasLimit(_txGasLimit)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx);
            tx.SenderAddress = null;
            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            _txPool.GetPendingTransactions().Length.Should().Be(1);
            result.Should().Be(AcceptTxResult.Accepted);
        }
        
        [Test]
        public void should_reject_transactions_from_contract_address()
        {
            _txPool = CreatePool();
            Transaction tx = Build.A.Transaction
                .WithGasLimit(_txGasLimit)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx);
            _stateProvider.UpdateCodeHash(TestItem.AddressA, TestItem.KeccakA, _specProvider.GetSpec(1));
            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            result.Should().Be(AcceptTxResult.SenderIsContract);
        }
        
        
        [Test]
        public void should_accept_1559_transactions_only_when_eip1559_enabled([Values(false, true)] bool eip1559Enabled)
        {
            ISpecProvider specProvider = null;
            if (eip1559Enabled)
            {
                specProvider = Substitute.For<ISpecProvider>();
                specProvider.GetSpec(Arg.Any<long>()).Returns(London.Instance);
            }
            var txPool = CreatePool(null, specProvider);
            Transaction tx = Build.A.Transaction
                .WithType(TxType.EIP1559)
                .WithChainId(ChainId.Mainnet)
                .WithMaxFeePerGas(10.GWei())
                .WithMaxPriorityFeePerGas(5.GWei())
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx);
            _blockTree.BlockAddedToMain += Raise.EventWith(_blockTree, new BlockReplacementEventArgs(Build.A.Block.WithGasLimit(10000000).TestObject));
            AcceptTxResult result = txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            txPool.GetPendingTransactions().Length.Should().Be(eip1559Enabled ? 1 : 0);
            result.Should().Be(eip1559Enabled ? AcceptTxResult.Accepted : AcceptTxResult.Invalid);
        }

        [Test]
        public void should_ignore_insufficient_funds_for_eip1559_transactions()
        {
            ISpecProvider specProvider = GetLondonSpecProvider();
            var txPool = CreatePool(null, specProvider);
            Transaction tx = Build.A.Transaction
                .WithType(TxType.EIP1559).WithMaxFeePerGas(20)
                .WithChainId(ChainId.Mainnet)
                .WithValue(5).SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx.SenderAddress, tx.Value - 1); // we should have InsufficientFunds only when balance < tx.Value
            AcceptTxResult result = txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            txPool.GetPendingTransactions().Length.Should().Be(0);
            result.Should().Be(AcceptTxResult.InsufficientFunds);
            EnsureSenderBalance(tx.SenderAddress, tx.Value);
            _blockTree.BlockAddedToMain += Raise.EventWith(_blockTree, new BlockReplacementEventArgs(Build.A.Block.WithGasLimit(10000000).TestObject));
            result = txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            result.Should().Be(AcceptTxResult.Accepted);
            txPool.GetPendingTransactions().Length.Should().Be(1);
        }

        private static ISpecProvider GetLondonSpecProvider()
        {
            var specProvider = Substitute.For<ISpecProvider>();
            specProvider.GetSpec(Arg.Any<long>()).Returns(London.Instance);
            return specProvider;
        }

        [TestCase(false, false, ExpectedResult = nameof(AcceptTxResult.Accepted))]
        [TestCase(false, true, ExpectedResult = nameof(AcceptTxResult.Accepted))]
        [TestCase(true, false, ExpectedResult = nameof(AcceptTxResult.Accepted))]
        [TestCase(true, true, ExpectedResult = nameof(AcceptTxResult.SenderIsContract))]
        public string should_reject_transactions_with_deployed_code_when_eip3607_enabled(bool eip3607Enabled, bool hasCode)
        {
            ISpecProvider specProvider = new OverridableSpecProvider(new TestSpecProvider(London.Instance), r => new OverridableReleaseSpec(r) {IsEip3607Enabled = eip3607Enabled});
            TxPool txPool = CreatePool(null, specProvider);

            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx);
            _stateProvider.UpdateCodeHash(TestItem.AddressA, hasCode ? TestItem.KeccakH : Keccak.OfAnEmptyString, London.Instance);
            
            return txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast).ToString();
        }

        [Test]
        public void should_ignore_insufficient_funds_transactions()
        {
            _txPool = CreatePool();
            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            _txPool.GetPendingTransactions().Length.Should().Be(0);
            result.Should().Be(AcceptTxResult.InsufficientFunds);
        }
        
        [Test]
        public void should_ignore_old_nonce_transactions()
        {
            _txPool = CreatePool();
            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx);
            _stateProvider.IncrementNonce(tx.SenderAddress);
            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            _txPool.GetPendingTransactions().Length.Should().Be(0);
            result.Should().Be(AcceptTxResult.OldNonce);
        }

        [Test]
        public void should_ignore_overflow_transactions()
        {
            _txPool = CreatePool();
            Transaction tx = Build.A.Transaction.WithGasPrice(UInt256.MaxValue / Transaction.BaseTxGasCost)
                .WithGasLimit(Transaction.BaseTxGasCost)
                .WithValue(Transaction.BaseTxGasCost)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx);
            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            _txPool.GetPendingTransactions().Length.Should().Be(0);
            result.Should().Be(AcceptTxResult.Int256Overflow);
        }
        
        [Test]
        public void should_ignore_overflow_transactions_gas_premium_and_fee_cap()
        {
            ISpecProvider specProvider = GetLondonSpecProvider();
            var txPool = CreatePool(null, specProvider);
            Transaction tx = Build.A.Transaction.WithGasPrice(UInt256.MaxValue / Transaction.BaseTxGasCost)
                .WithGasLimit(Transaction.BaseTxGasCost)
                .WithValue(Transaction.BaseTxGasCost)
                .WithMaxFeePerGas(UInt256.MaxValue - 10)
                .WithMaxPriorityFeePerGas((UInt256)15)
                .WithType(TxType.EIP1559)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx.SenderAddress, UInt256.MaxValue);
            AcceptTxResult result = txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            txPool.GetPendingTransactions().Length.Should().Be(0);
            result.Should().Be(AcceptTxResult.Int256Overflow);
        }
        
        [Test]
        public void should_ignore_block_gas_limit_exceeded()
        {
            _txPool = CreatePool();
            Transaction tx = Build.A.Transaction
                .WithGasLimit(Transaction.BaseTxGasCost * 5)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx);
            _headInfo.BlockGasLimit = Transaction.BaseTxGasCost * 4;
            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            _txPool.GetPendingTransactions().Length.Should().Be(0);
            result.Should().Be(AcceptTxResult.GasLimitExceeded);
        }
        
        [Test]
        public void should_accept_tx_when_base_fee_is_high()
        {
            ISpecProvider specProvider = new OverridableSpecProvider(new TestSpecProvider(London.Instance), r => new OverridableReleaseSpec(r) { Eip1559TransitionBlock = 1 });
            IIncomingTxFilter incomingTxFilter = new TxFilterAdapter(_blockTree, new MinGasPriceTxFilter(1.GWei(), specProvider), LimboLogs.Instance);
            _txPool = CreatePool(specProvider: specProvider, incomingTxFilter: incomingTxFilter);
            Transaction tx = Build.A.Transaction
                .WithGasLimit(Transaction.BaseTxGasCost)
                .WithGasPrice(2.GWei())
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx);
            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            result.Should().Be(AcceptTxResult.Accepted);
            _txPool.GetPendingTransactions().Length.Should().Be(1);
        }
        
        [Test]
        public void should_ignore_tx_gas_limit_exceeded()
        {
            _txPool = CreatePool();
            Transaction tx = Build.A.Transaction
                .WithGasLimit(_txGasLimit + 1)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx);
            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            _txPool.GetPendingTransactions().Length.Should().Be(0);
            result.Should().Be(AcceptTxResult.GasLimitExceeded);
        }

        [TestCase(10,0, nameof(AcceptTxResult.FeeTooLow))]
        [TestCase(10,5, nameof(AcceptTxResult.FeeTooLow))]
        [TestCase(10,6, nameof(AcceptTxResult.InsufficientFunds))]
        [TestCase(11,0, nameof(AcceptTxResult.Accepted))]
        [TestCase(11,4, nameof(AcceptTxResult.Accepted))]
        [TestCase(11,5, nameof(AcceptTxResult.InsufficientFunds))]
        [TestCase(15,0, nameof(AcceptTxResult.Accepted))]
        [TestCase(16,0, nameof(AcceptTxResult.InsufficientFunds))]
        public void should_handle_adding_tx_to_full_txPool_properly(int gasPrice, int value, string expected)
        {
            _txPool = CreatePool(new TxPoolConfig() {Size = 30});
            Transaction[] transactions = GetTransactions(GetPeers(3), true, false);
            
            foreach (Address address in transactions.Select(t => t.SenderAddress).Distinct())
            {
                EnsureSenderBalance(address, UInt256.MaxValue);
            }
            
            foreach (Transaction transaction in transactions)
            {
                transaction.GasPrice = 10;
                _txPool.SubmitTx(transaction, TxHandlingOptions.PersistentBroadcast);
            }
            
            Transaction tx = Build.A.Transaction
                .WithGasPrice((UInt256)gasPrice)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            tx.Value = (UInt256)(value * tx.GasLimit);
            EnsureSenderBalance(tx.SenderAddress, (UInt256)(15 * tx.GasLimit));
            _txPool.GetPendingTransactions().Length.Should().Be(30);
            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.None);
            result.ToString().Should().Contain(expected);
        }
        
        [TestCase(10,0, nameof(AcceptTxResult.FeeTooLow))]
        [TestCase(11,0, nameof(AcceptTxResult.Accepted))]
        [TestCase(11,4, nameof(AcceptTxResult.Accepted))]
        [TestCase(11,5, nameof(AcceptTxResult.FeeTooLow))]
        [TestCase(11,15, nameof(AcceptTxResult.FeeTooLow))]
        [TestCase(11,16, nameof(AcceptTxResult.InsufficientFunds))]
        [TestCase(50,0, nameof(AcceptTxResult.Invalid))]
        [TestCase(50,15, nameof(AcceptTxResult.Invalid))]
        [TestCase(50,16, nameof(AcceptTxResult.Invalid))]
        public void should_handle_adding_1559_tx_to_full_txPool_properly(int gasPremium, int value, string expected)
        {
            ISpecProvider specProvider = GetLondonSpecProvider();
            _txPool = CreatePool(new TxPoolConfig() {Size = 30}, specProvider);
            Transaction[] transactions = GetTransactions(GetPeers(3), true, false);

            foreach (Address address in transactions.Select(t => t.SenderAddress).Distinct())
            {
                EnsureSenderBalance(address, UInt256.MaxValue);
            }
            
            foreach (Transaction transaction in transactions)
            {
                transaction.GasPrice = 10;
                _txPool.SubmitTx(transaction, TxHandlingOptions.PersistentBroadcast);
            }
            
            Transaction tx = Build.A.Transaction
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas(20)
                .WithMaxPriorityFeePerGas((UInt256)gasPremium)
                .WithChainId(ChainId.Mainnet)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            tx.Value = (UInt256)(value * tx.GasLimit);
            EnsureSenderBalance(tx.SenderAddress, (UInt256)(15 * tx.GasLimit));
            _txPool.GetPendingTransactions().Length.Should().Be(30);
            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.None);
            _txPool.GetPendingTransactions().Length.Should().Be(30);
            result.ToString().Should().Contain(expected);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void should_add_underpaid_txs_to_full_TxPool_only_if_local(bool isLocal)
        {
            TxHandlingOptions txHandlingOptions = isLocal ? TxHandlingOptions.PersistentBroadcast : TxHandlingOptions.None;
            
            _txPool = CreatePool(new TxPoolConfig() {Size = 30});
            Transaction[] transactions = GetTransactions(GetPeers(3), true, false);
            
            foreach (Address address in transactions.Select(t => t.SenderAddress).Distinct())
            {
                EnsureSenderBalance(address, UInt256.MaxValue);
            }
            
            foreach (Transaction transaction in transactions)
            {
                transaction.GasPrice = 10;
                _txPool.SubmitTx(transaction, TxHandlingOptions.None);
            }
            
            Transaction tx = Build.A.Transaction
                .WithGasPrice(UInt256.Zero)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
                .TestObject;
            EnsureSenderBalance(tx.SenderAddress, UInt256.MaxValue);
            _txPool.GetPendingTransactions().Length.Should().Be(30);
            _txPool.GetOwnPendingTransactions().Length.Should().Be(0);
            AcceptTxResult result = _txPool.SubmitTx(tx, txHandlingOptions);
            _txPool.GetPendingTransactions().Length.Should().Be(30);
            _txPool.GetOwnPendingTransactions().Length.Should().Be(isLocal ? 1 : 0);
            result.ToString().Should().Contain(isLocal ? nameof(AcceptTxResult.Accepted) : nameof(AcceptTxResult.FeeTooLow));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(10)]
        public void should_not_add_tx_if_already_pending_lower_nonces_are_exhausting_balance(int numberOfTxsPossibleToExecuteBeforeGasExhaustion)
        {
            const int gasPrice = 10;
            const int value = 1;
            int oneTxPrice = _txGasLimit * gasPrice + value;
            _txPool = CreatePool();
            Transaction[] transactions = new Transaction[10];

            EnsureSenderBalance(TestItem.AddressA, (UInt256)(oneTxPrice * numberOfTxsPossibleToExecuteBeforeGasExhaustion));
            
            for (int i = 0; i < 10; i++)
            {
                transactions[i] = Build.A.Transaction
                    .WithSenderAddress(TestItem.AddressA)
                    .WithNonce((UInt256)i)
                    .WithGasPrice((UInt256)gasPrice)
                    .WithGasLimit(_txGasLimit)
                    .WithValue(value)
                    .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
                _txPool.SubmitTx(transactions[i], TxHandlingOptions.PersistentBroadcast);
            }

            _txPool.GetPendingTransactionsCount().Should().Be(numberOfTxsPossibleToExecuteBeforeGasExhaustion);
        }
        
        [TestCase(1, 0)]
        [TestCase(2, 1)]
        [TestCase(5, 5)]
        [TestCase(10, 3)]
        public void should_not_count_txs_with_stale_nonces_when_calculating_cumulative_cost(int numberOfTxsPossibleToExecuteBeforeGasExhaustion, int numberOfStaleTxsInBucket)
        {
            const int gasPrice = 10;
            const int value = 1;
            int oneTxPrice = _txGasLimit * gasPrice + value;
            _txPool = CreatePool();

            EnsureSenderBalance(TestItem.AddressA, (UInt256)(oneTxPrice * numberOfTxsPossibleToExecuteBeforeGasExhaustion));

            for (int i = 0; i < numberOfTxsPossibleToExecuteBeforeGasExhaustion * 2; i++)
            {
                Transaction tx = Build.A.Transaction
                    .WithSenderAddress(TestItem.AddressA)
                    .WithNonce((UInt256)i)
                    .WithGasPrice((UInt256)gasPrice)
                    .WithGasLimit(_txGasLimit)
                    .WithValue(value)
                    .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
                _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);

                if (i < numberOfStaleTxsInBucket)
                {
                    _stateProvider.IncrementNonce(TestItem.AddressA);
                }
            }

            int numberOfTxsInTxPool = _txPool.GetPendingTransactionsCount();
            numberOfTxsInTxPool.Should().Be(numberOfTxsPossibleToExecuteBeforeGasExhaustion);
            _txPool.GetPendingTransactions()[numberOfTxsInTxPool - 1].Nonce.Should().Be((UInt256)(numberOfTxsInTxPool - 1 + numberOfStaleTxsInBucket));
        }

        [Test]
        public void
            should_add_tx_if_cost_of_executing_all_txs_in_bucket_exceeds_balance_but_these_with_lower_nonces_doesnt()
        {
            const int gasPrice = 10;
            const int value = 1;
            int oneTxPrice = _txGasLimit * gasPrice + value;
            _txPool = CreatePool();
            Transaction[] transactions = new Transaction[10];

            EnsureSenderBalance(TestItem.AddressA, (UInt256)(oneTxPrice * 8));
            
            for (int i = 0; i < 10; i++)
            {
                transactions[i] = Build.A.Transaction
                    .WithSenderAddress(TestItem.AddressA)
                    .WithNonce((UInt256)i)
                    .WithGasPrice((UInt256)gasPrice)
                    .WithGasLimit(_txGasLimit)
                    .WithValue(value)
                    .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
                if (i != 7)
                {
                    _txPool.SubmitTx(transactions[i], TxHandlingOptions.PersistentBroadcast);
                }
            }
            
            _txPool.GetPendingTransactions().Length.Should().Be(8);
            _txPool.SubmitTx(transactions[7], TxHandlingOptions.PersistentBroadcast);
            _txPool.GetPendingTransactions().Length.Should().Be(9);
        }

        [Test]
        public void should_discard_tx_because_of_overflow_of_cumulative_cost_of_this_tx_and_all_txs_with_lower_nonces()
        {
            _txPool = CreatePool();

            Transaction[] transactions = new Transaction[3];

            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            UInt256.MaxValue.Divide(GasCostOf.Transaction * 2, out UInt256 halfOfMaxGasPriceWithoutOverflow);
            
            for (int i = 0; i < 3; i++)
            {
                transactions[i] = Build.A.Transaction
                    .WithSenderAddress(TestItem.AddressA)
                    .WithNonce((UInt256)i)
                    .WithGasPrice(halfOfMaxGasPriceWithoutOverflow)
                    .WithGasLimit(GasCostOf.Transaction)
                    .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
                if (i != 2)
                {
                    _txPool.SubmitTx(transactions[i], TxHandlingOptions.PersistentBroadcast);
                }
            }

            transactions[2].GasPrice = 5;
            _txPool.GetPendingTransactions().Length.Should().Be(2);
            _txPool.SubmitTx(transactions[2], TxHandlingOptions.PersistentBroadcast).Should().Be(AcceptTxResult.Int256Overflow);
        }

        [Test]
        public async Task should_not_dump_GasBottleneck_of_all_txs_in_bucket_if_first_tx_in_bucket_has_insufficient_balance_but_has_old_nonce()
        {
            _txPool = CreatePool();
            Transaction[] transactions = new Transaction[5];
            
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            for (int i = 0; i < 5; i++)
            {
                transactions[i] = Build.A.Transaction
                    .WithSenderAddress(TestItem.AddressA)
                    .WithNonce((UInt256)i)
                    .WithGasPrice((UInt256)(i + 2))
                    .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
                _txPool.SubmitTx(transactions[i], TxHandlingOptions.PersistentBroadcast);
            }

            for (int i = 0; i < 3; i++)
            {
                _stateProvider.IncrementNonce(TestItem.AddressA);
            }

            transactions[0].Value = 100000;

            await RaiseBlockAddedToMainAndWaitForTransactions(5);
            
            _txPool.GetPendingTransactions().Count(t => t.GasBottleneck == 0).Should().Be(0);
            _txPool.GetPendingTransactions().Max(t => t.GasBottleneck).Should().Be((UInt256)5);
        }

        [Test]
        public async Task should_not_fail_if_there_is_no_current_nonce_in_bucket()
        {
            _txPool = CreatePool();
            Transaction[] transactions = new Transaction[5];
            
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            for (int i = 0; i < 3; i++)
            {
                transactions[i] = Build.A.Transaction
                    .WithSenderAddress(TestItem.AddressA)
                    .WithNonce((UInt256)i + 4)
                    .WithGasPrice((UInt256)(i + 2))
                    .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
                _txPool.SubmitTx(transactions[i], TxHandlingOptions.PersistentBroadcast);
            }

            for (int i = 0; i < 3; i++)
            {
                _stateProvider.IncrementNonce(TestItem.AddressA);
            }
            
            await RaiseBlockAddedToMainAndWaitForTransactions(3);
            _txPool.GetPendingTransactions().Count(t => t.GasBottleneck == 0).Should().Be(0);
        }

        [Test]
        public void should_remove_txHash_from_hashCache_when_tx_removed_because_of_txPool_size_exceeded()
        {
            _txPool = CreatePool(new TxPoolConfig(){Size = 1});
            Transaction transaction = Build.A.Transaction
                .WithSenderAddress(TestItem.AddressA)
                .WithGasPrice(2)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(transaction);
            _txPool.SubmitTx(transaction, TxHandlingOptions.PersistentBroadcast);
            
            _txPool.IsKnown(transaction.Hash).Should().BeTrue();

            Transaction higherPriorityTx = Build.A.Transaction
                .WithSenderAddress(TestItem.AddressB)
                .WithGasPrice(100)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyB).TestObject;
            EnsureSenderBalance(higherPriorityTx);
            _txPool.SubmitTx(higherPriorityTx, TxHandlingOptions.PersistentBroadcast);

            _blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(Build.A.Block.TestObject));
            _txPool.IsKnown(higherPriorityTx.Hash).Should().BeTrue();
            _txPool.IsKnown(transaction.Hash).Should().BeFalse();
        }

        [Test]
        public void should_calculate_gasBottleneck_properly()
        {
            _txPool = CreatePool();
            Transaction[] transactions = new Transaction[5];
            
            for (int i = 0; i < 5; i++)
            {
                transactions[i] = Build.A.Transaction
                    .WithSenderAddress(TestItem.AddressA)
                    .WithNonce((UInt256)i)
                    .WithGasPrice((UInt256)(i + 2))
                    .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
                EnsureSenderBalance(transactions[i]);
                _txPool.SubmitTx(transactions[i], TxHandlingOptions.PersistentBroadcast);
            }

            _txPool.GetPendingTransactions().Min(t => t.GasBottleneck).Should().Be((UInt256)2);
            _txPool.GetPendingTransactions().Max(t => t.GasBottleneck).Should().Be((UInt256)2);
        }
        
        [Test]
        public async Task should_remove_GasBottleneck_of_old_nonces()
        {
            _txPool = CreatePool();
            Transaction[] transactions = new Transaction[5];
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            for (int i = 0; i < 5; i++)
            {
                transactions[i] = Build.A.Transaction
                    .WithSenderAddress(TestItem.AddressA)
                    .WithNonce((UInt256)i)
                    .WithGasPrice((UInt256)(i + 2))
                    .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
                _txPool.SubmitTx(transactions[i], TxHandlingOptions.PersistentBroadcast);
            }
            _txPool.GetPendingTransactions().Length.Should().Be(5);

            for (int i = 0; i < 3; i++)
            {
               _stateProvider.IncrementNonce(TestItem.AddressA);
            }

            await RaiseBlockAddedToMainAndWaitForTransactions(5);
            _txPool.GetPendingTransactions().Count(t => t.GasBottleneck == 0).Should().Be(0);
            _txPool.GetPendingTransactions().Max(t => t.GasBottleneck).Should().Be((UInt256)5);
        }

        [Test]
        public void should_broadcast_own_transactions()
        {
            _txPool = CreatePool();
            AddTransactionToPool();
            Assert.AreEqual(1, _txPool.GetOwnPendingTransactions().Length);
        }
        
        [Test]
        public void should_not_broadcast_own_transactions_that_faded_out_and_came_back()
        {
            _txPool = CreatePool();
            var transaction = AddTransactionToPool();
            _txPool.RemoveTransaction(transaction.Hash);
            _txPool.RemoveTransaction(TestItem.KeccakA);
            _txPool.SubmitTx(transaction, TxHandlingOptions.None);
            Assert.AreEqual(0, _txPool.GetOwnPendingTransactions().Length);
        }

        [TestCase(1, 0)]
        [TestCase(2, 0)]
        [TestCase(2, 1)]
        [TestCase(10, 0)]
        [TestCase(10, 1)]
        [TestCase(10, 5)]
        [TestCase(10, 8)]
        [TestCase(10, 9)]
        public void should_remove_stale_txs_from_persistent_transactions(int numberOfTxs, int nonceIncludedInBlock)
        {
            _txPool = CreatePool();

            Transaction[] transactions = new Transaction[numberOfTxs];
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            for (int i = 0; i < numberOfTxs; i++)
            {
                transactions[i] = Build.A.Transaction
                    .WithNonce((UInt256)i)
                    .WithGasLimit(GasCostOf.Transaction)
                    .WithGasPrice(10.GWei())
                    .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
                    .TestObject;
                _txPool.SubmitTx(transactions[i], TxHandlingOptions.PersistentBroadcast);
            }
            _txPool.GetOwnPendingTransactions().Length.Should().Be(numberOfTxs);

            Block block = Build.A.Block.WithTransactions(transactions[nonceIncludedInBlock]).TestObject;
            BlockReplacementEventArgs blockReplacementEventArgs = new(block, null);
            
            ManualResetEvent manualResetEvent = new(false);
            _txPool.RemoveTransaction(Arg.Do<Keccak>(t => manualResetEvent.Set()));
            _blockTree.BlockAddedToMain += Raise.EventWith(new object(), blockReplacementEventArgs);
            manualResetEvent.WaitOne(TimeSpan.FromMilliseconds(200));
            
            // transactions[nonceIncludedInBlock] was included in the block and should be removed, as well as all lower nonces.
            _txPool.GetOwnPendingTransactions().Length.Should().Be(numberOfTxs - nonceIncludedInBlock - 1);
        }

        [Test]
        public void broadcaster_should_work_well_when_there_are_no_txs_in_persistent_txs_from_sender_of_tx_included_in_block()
        {
            _txPool = CreatePool();
            
            Transaction transactionA = Build.A.Transaction
                .WithNonce(0)
                .WithGasLimit(GasCostOf.Transaction)
                .WithGasPrice(10.GWei())
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
                .TestObject;
            EnsureSenderBalance(transactionA);
            _txPool.SubmitTx(transactionA, TxHandlingOptions.None);

            Transaction transactionB = Build.A.Transaction
                .WithNonce(0)
                .WithGasLimit(GasCostOf.Transaction)
                .WithGasPrice(10.GWei())
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyB)
                .TestObject;
            EnsureSenderBalance(transactionB);
            _txPool.SubmitTx(transactionB, TxHandlingOptions.PersistentBroadcast);
            
            _txPool.GetPendingTransactions().Length.Should().Be(2);
            _txPool.GetOwnPendingTransactions().Length.Should().Be(1);
            
            Block block = Build.A.Block.WithTransactions(transactionA).TestObject;
            BlockReplacementEventArgs blockReplacementEventArgs = new(block, null);
            
            ManualResetEvent manualResetEvent = new(false);
            _txPool.RemoveTransaction(Arg.Do<Keccak>(t => manualResetEvent.Set()));
            _blockTree.BlockAddedToMain += Raise.EventWith(new object(), blockReplacementEventArgs);
            manualResetEvent.WaitOne(TimeSpan.FromMilliseconds(200));
            
            _txPool.GetPendingTransactions().Length.Should().Be(1);
            _txPool.GetOwnPendingTransactions().Length.Should().Be(1);
        }

        [Test]
        public async Task should_remove_transactions_concurrently()
        {
            var maxTryCount = 5;
            for (int i = 0; i < maxTryCount; ++i)
            {
                _txPool = CreatePool();
                int transactionsPerPeer = 5;
                var transactions = AddTransactionsToPool(true, false, transactionsPerPeer);
                Transaction[] transactionsForFirstTask = transactions.Where(t => t.Nonce == 8).ToArray();
                Transaction[] transactionsForSecondTask = transactions.Where(t => t.Nonce == 6).ToArray();
                Transaction[] transactionsForThirdTask = transactions.Where(t => t.Nonce == 7).ToArray();
                transactions.Should().HaveCount(transactionsPerPeer * 10);
                transactionsForFirstTask.Should().HaveCount(transactionsPerPeer);
                var firstTask = Task.Run(() => DeleteTransactionsFromPool(transactionsForFirstTask));
                var secondTask = Task.Run(() => DeleteTransactionsFromPool(transactionsForSecondTask));
                var thirdTask = Task.Run(() => DeleteTransactionsFromPool(transactionsForThirdTask));
                await Task.WhenAll(firstTask, secondTask, thirdTask);
                _txPool.GetPendingTransactions().Should().HaveCount(transactionsPerPeer * 7);
            }
        }

        [Test]
        public void should_add_transactions_concurrently()
        {
            int size = 3;
            TxPoolConfig config = new() {GasLimit = _txGasLimit, Size = size};
            _txPool = CreatePool(config);

            foreach (PrivateKey privateKey in TestItem.PrivateKeys)
            {
                EnsureSenderBalance(privateKey.Address, 10.Ether());
            }
            
            Parallel.ForEach(TestItem.PrivateKeys, k =>
            {
                for (uint i = 0; i < 100; i++)
                {
                    Transaction tx = GetTransaction(i, GasCostOf.Transaction, 10.GWei(), TestItem.AddressA, Array.Empty<byte>(), k);
                    _txPool.SubmitTx(tx, TxHandlingOptions.None);
                }
            });

            _txPool.GetPendingTransactionsCount().Should().Be(size);
        }

        [TestCase(true, true,10)]
        [TestCase(false, true,100)]
        [TestCase(true, false,100)]
        [TestCase(false, false,100)]
        public void should_add_pending_transactions(bool sameTransactionSenderPerPeer, bool sameNoncePerPeer, int expectedTransactions)
        {
            _txPool = CreatePool();
            AddTransactionsToPool(sameTransactionSenderPerPeer, sameNoncePerPeer);
            _txPool.GetPendingTransactions().Length.Should().Be(expectedTransactions);
        }

        [TestCase(true, true,10)]
        [TestCase(false, true,100)]
        [TestCase(true, false,100)]
        [TestCase(false, false,100)]
        public void should_remove_tx_from_txPool_when_included_in_block(bool sameTransactionSenderPerPeer, bool sameNoncePerPeer, int expectedTransactions)
        {
            _txPool = CreatePool();
            
            AddTransactionsToPool(sameTransactionSenderPerPeer, sameNoncePerPeer);
            _txPool.GetPendingTransactions().Length.Should().Be(expectedTransactions);

            Transaction[] transactions = _txPool.GetPendingTransactions();
            Block block = Build.A.Block.WithTransactions(transactions).TestObject;
            BlockReplacementEventArgs blockReplacementEventArgs = new(block, null);

            ManualResetEvent manualResetEvent = new(false);
            _txPool.RemoveTransaction(Arg.Do<Keccak>(t => manualResetEvent.Set()));
            _blockTree.BlockAddedToMain += Raise.EventWith(new object(), blockReplacementEventArgs);
            manualResetEvent.WaitOne(TimeSpan.FromMilliseconds(200));

            _txPool.GetPendingTransactions().Length.Should().Be(0);
        }
        
        [TestCase(true, true,10)]
        [TestCase(false, true,100)]
        [TestCase(true, false,100)]
        [TestCase(false, false,100)]
        public void should_not_remove_txHash_from_hashCache_when_tx_removed_because_of_including_in_block(bool sameTransactionSenderPerPeer, bool sameNoncePerPeer, int expectedTransactions)
        {
            _txPool = CreatePool();
            
            AddTransactionsToPool(sameTransactionSenderPerPeer, sameNoncePerPeer);
            _txPool.GetPendingTransactions().Length.Should().Be(expectedTransactions);

            Transaction[] transactions = _txPool.GetPendingTransactions();
            Block block = Build.A.Block.WithTransactions(transactions).TestObject;
            BlockReplacementEventArgs blockReplacementEventArgs = new(block, null);

            ManualResetEvent manualResetEvent = new(false);
            _txPool.RemoveTransaction(Arg.Do<Keccak>(t => manualResetEvent.Set()));
            _blockTree.BlockAddedToMain += Raise.EventWith(new object(), blockReplacementEventArgs);
            manualResetEvent.WaitOne(TimeSpan.FromMilliseconds(200));

            foreach (Transaction transaction in transactions)
            {
                _txPool.IsKnown(transaction.Hash).Should().BeTrue();
            }
        }

        [Test]
        public void should_delete_pending_transactions()
        {
            _txPool = CreatePool();
            var transactions = AddTransactionsToPool();
            DeleteTransactionsFromPool(transactions);
            _txPool.GetPendingTransactions().Should().BeEmpty();
            _txPool.GetOwnPendingTransactions().Should().BeEmpty();
        }

        [Test]
        public void should_increment_own_transaction_nonces_locally_when_requesting_reservations()
        {
            _txPool = CreatePool();
            var nonceA1 = _txPool.ReserveOwnTransactionNonce(TestItem.AddressA);
            var nonceA2 = _txPool.ReserveOwnTransactionNonce(TestItem.AddressA);
            var nonceA3 = _txPool.ReserveOwnTransactionNonce(TestItem.AddressA);
            var nonceB1 = _txPool.ReserveOwnTransactionNonce(TestItem.AddressB);
            var nonceB2 = _txPool.ReserveOwnTransactionNonce(TestItem.AddressB);
            var nonceB3 = _txPool.ReserveOwnTransactionNonce(TestItem.AddressB);

            nonceA1.Should().Be(0);
            nonceA2.Should().Be(1);
            nonceA3.Should().Be(2);
            nonceB1.Should().Be(0);
            nonceB2.Should().Be(1);
            nonceB3.Should().Be(2);
        }
        
        [Test]
        public void should_increment_own_transaction_nonces_locally_when_requesting_reservations_in_parallel()
        {
            var address = TestItem.AddressA;
            const int reservationsCount = 1000;
            _txPool = CreatePool();
            ConcurrentQueue<UInt256> nonces = new();
            
            var result = Parallel.For(0, reservationsCount, i =>
            {
                nonces.Enqueue(_txPool.ReserveOwnTransactionNonce(address));
            });

            result.IsCompleted.Should().BeTrue();
            UInt256 nonce = _txPool.ReserveOwnTransactionNonce(address);
            nonces.Enqueue(nonce);
            nonce.Should().Be(new UInt256(reservationsCount));
            nonces.OrderBy(n => n).Should().BeEquivalentTo(Enumerable.Range(0, reservationsCount + 1).Select(i => new UInt256((uint)i)));
        }

        [Test]
        public void should_return_own_nonce_already_used_result_when_trying_to_send_transaction_with_same_nonce_for_same_address()
        {
            _txPool = CreatePool();
            var result1 = _txPool.SubmitTx(GetTransaction(TestItem.PrivateKeyA, TestItem.AddressA), TxHandlingOptions.PersistentBroadcast | TxHandlingOptions.ManagedNonce);
            result1.Should().Be(AcceptTxResult.Accepted);
            _txPool.GetOwnPendingTransactions().Length.Should().Be(1);
            _txPool.GetPendingTransactions().Length.Should().Be(1);
            var result2 = _txPool.SubmitTx(GetTransaction(TestItem.PrivateKeyA, TestItem.AddressB), TxHandlingOptions.PersistentBroadcast | TxHandlingOptions.ManagedNonce);
            result2.Should().Be(AcceptTxResult.OwnNonceAlreadyUsed);
            _txPool.GetOwnPendingTransactions().Length.Should().Be(1);
            _txPool.GetPendingTransactions().Length.Should().Be(1);
        }

        [Test]
        public void Should_not_try_to_load_transactions_from_storage()
        {
            var transaction = Build.A.Transaction.SignedAndResolved().TestObject;
            _txPool = CreatePool();
            _txPool.TryGetPendingTransaction(transaction.Hash, out var retrievedTransaction).Should().BeFalse();
        }
        
        [Test]
        public void should_retrieve_added_transaction_correctly()
        {
            var transaction = Build.A.Transaction.SignedAndResolved().TestObject;
            EnsureSenderBalance(transaction);
            _specProvider = Substitute.For<ISpecProvider>();
            _specProvider.ChainId.Returns(transaction.Signature.ChainId.Value);
            _txPool = CreatePool();
            _txPool.SubmitTx(transaction, TxHandlingOptions.PersistentBroadcast).Should().Be(AcceptTxResult.Accepted);
            _txPool.TryGetPendingTransaction(transaction.Hash, out var retrievedTransaction).Should().BeTrue();
            retrievedTransaction.Should().BeEquivalentTo(transaction);
        }
        
        [Test]
        public void should_not_retrieve_not_added_transaction()
        {
            var transaction = Build.A.Transaction.SignedAndResolved().TestObject;
            _txPool = CreatePool();
            _txPool.TryGetPendingTransaction(transaction.Hash, out var retrievedTransaction).Should().BeFalse();
            retrievedTransaction.Should().BeNull();
        }
        
        [Test]
        public void should_notify_added_peer_of_own_tx()
        {
            _txPool = CreatePool();
            Transaction tx = AddTransactionToPool();
            ITxPoolPeer txPoolPeer = Substitute.For<ITxPoolPeer>();
            txPoolPeer.Id.Returns(TestItem.PublicKeyA);
            _txPool.AddPeer(txPoolPeer);
            txPoolPeer.Received().SendNewTransactions(Arg.Any<IEnumerable<Transaction>>(), false);
        }
        
        [Test]
        public async Task should_notify_peer_only_once()
        {
            _txPool = CreatePool();
            ITxPoolPeer txPoolPeer = Substitute.For<ITxPoolPeer>();
            txPoolPeer.Id.Returns(TestItem.PublicKeyA);
            _txPool.AddPeer(txPoolPeer);
            Transaction tx = AddTransactionToPool();
            await Task.Delay(500);
            txPoolPeer.Received(1).SendNewTransactions(Arg.Any<IEnumerable<Transaction>>(), false);
        }

        [Test]
        public void should_send_to_peers_full_newly_added_local_tx()
        {
            _txPool = CreatePool();
            ITxPoolPeer txPoolPeer = Substitute.For<ITxPoolPeer>();
            txPoolPeer.Id.Returns(TestItem.PublicKeyA);
            _txPool.AddPeer(txPoolPeer);
            Transaction tx = AddTransactionToPool();
            txPoolPeer.Received().SendNewTransaction(tx);
        }
        
        [Test]
        public void should_not_send_to_peers_full_newly_added_external_tx()
        {
            _txPool = CreatePool();
            ITxPoolPeer txPoolPeer = Substitute.For<ITxPoolPeer>();
            txPoolPeer.Id.Returns(TestItem.PublicKeyA);
            _txPool.AddPeer(txPoolPeer);
            Transaction tx = AddTransactionToPool(false);
            txPoolPeer.DidNotReceive().SendNewTransaction(tx);
        }

        [Test]
        public void should_accept_access_list_transactions_only_when_eip2930_enabled([Values(false, true)] bool eip2930Enabled)
        {
            if (!eip2930Enabled)
            {
                _blockTree.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(RopstenSpecProvider.BerlinBlockNumber - 1).TestObject);
            }
            
            _txPool = CreatePool();
            Transaction tx = Build.A.Transaction
                .WithType(TxType.AccessList)
                .WithChainId(ChainId.Mainnet)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx);
            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            _txPool.GetPendingTransactions().Length.Should().Be(eip2930Enabled ? 1 : 0);
            result.Should().Be(eip2930Enabled ? AcceptTxResult.Accepted : AcceptTxResult.Invalid);
        }

        [Test]
        public void When_MaxFeePerGas_is_lower_than_MaxPriorityFeePerGas_tx_is_invalid()
        {
            _txPool = CreatePool();
            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
                .WithMaxPriorityFeePerGas(10.GWei())
                .WithMaxFeePerGas(5.GWei())
                .WithType(TxType.EIP1559)
                .TestObject;
            EnsureSenderBalance(tx);
            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            _txPool.GetPendingTransactions().Length.Should().Be(0);
            result.Should().Be(AcceptTxResult.Invalid);
        }
        
        [Test]
        public void should_return_true_when_asking_for_txHash_existing_in_pool()
        {
            _txPool = CreatePool();
            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx);
            _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            _txPool.IsKnown(tx.Hash).Should().Be(true);
            _txPool.RemoveTransaction(tx.Hash).Should().Be(true);
        }
        
        [Test]
        public void should_return_false_when_asking_for_not_known_txHash()
        {
            _txPool = CreatePool();
            _txPool.IsKnown(TestItem.KeccakA).Should().Be(false);
            Transaction tx = Build.A.Transaction.TestObject;
            tx.Hash = TestItem.KeccakA;
            _txPool.RemoveTransaction(tx.Hash).Should().Be(false);
        }

        [Test]
        public void should_return_false_when_trying_to_remove_tx_with_null_txHash()
        {
            _txPool = CreatePool();
            _txPool.RemoveTransaction(null).Should().Be(false);
        }

        [TestCase(0,0,false)]
        [TestCase(0,1,true)]
        [TestCase(1,2,true)]
        [TestCase(10,11,true)]
        [TestCase(100,0,false)]
        [TestCase(100,80,false)]
        [TestCase(100,109,false)]
        [TestCase(100,110,true)]
        [TestCase(1_000_000_000,1_099_999_999,false)]
        [TestCase(1_000_000_000,1_100_000_000,true)]
        public void should_replace_tx_with_same_sender_and_nonce_only_if_new_fee_is_at_least_10_percent_higher_than_old(int oldGasPrice, int newGasPrice, bool replaced)
        {
            ISpecProvider specProvider = GetLondonSpecProvider();
            _txPool = CreatePool(null, specProvider);
            Transaction oldTx = Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithNonce(0).WithGasPrice((UInt256)oldGasPrice).SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            Transaction newTx = Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithNonce(0).WithGasPrice((UInt256)newGasPrice).SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            _txPool.SubmitTx(oldTx, TxHandlingOptions.PersistentBroadcast);
            _txPool.SubmitTx(newTx, TxHandlingOptions.PersistentBroadcast);
            
            _txPool.GetPendingTransactions().Length.Should().Be(1);
            _txPool.GetPendingTransactions().First().Should().BeEquivalentTo(replaced ? newTx : oldTx);
        }
        
        [TestCase(0, 0, 0, 0, false)]
        [TestCase(0, 1, 0, 1, true)]
        [TestCase(1, 2, 1, 1, false)]
        [TestCase(1, 1, 1, 2, false)]
        [TestCase(1, 2, 1, 2, true)]
        [TestCase(10, 11, 10, 11, true)]
        [TestCase(100, 0, 100, 100, false)]
        [TestCase(100, 80, 100, 80, false)]
        [TestCase(100, 109, 100, 120, false)]
        [TestCase(100, 120, 100, 109, false)]
        [TestCase(100, 110, 100, 110, true)]
        [TestCase(1_000_000_000, 1_099_999_999, 1_000_000_000, 1_099_999_999, false)]
        [TestCase(1_000_000_000, 1_100_000_000, 1_000_000_000, 1_100_000_000, true)]
        public void should_replace_1559tx_with_same_sender_and_nonce_only_if_both_new_maxPriorityFeePerGas_and_new_maxFeePerGas_are_at_least_10_percent_higher_than_old(int oldMaxFeePerGas, int newMaxFeePerGas, int oldMaxPriorityFeePerGas, int newMaxPriorityFeePerGas, bool replaced)
        {
            ISpecProvider specProvider = GetLondonSpecProvider();
            _txPool = CreatePool(null, specProvider);
            Transaction oldTx = Build.A.Transaction
                .WithSenderAddress(TestItem.AddressA)
                .WithNonce(0)
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas((UInt256)oldMaxFeePerGas)
                .WithMaxPriorityFeePerGas((UInt256)oldMaxPriorityFeePerGas)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            Transaction newTx = Build.A.Transaction
                .WithSenderAddress(TestItem.AddressA)
                .WithNonce(0)
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas((UInt256)newMaxFeePerGas)
                .WithMaxPriorityFeePerGas((UInt256)newMaxPriorityFeePerGas)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            _txPool.SubmitTx(oldTx, TxHandlingOptions.PersistentBroadcast);
            _txPool.SubmitTx(newTx, TxHandlingOptions.PersistentBroadcast);
            
            _txPool.GetPendingTransactions().Length.Should().Be(1);
            _txPool.GetPendingTransactions().First().Should().BeEquivalentTo(replaced ? newTx : oldTx);
        }

        private IDictionary<ITxPoolPeer, PrivateKey> GetPeers(int limit = 100)
        {
            var peers = new Dictionary<ITxPoolPeer, PrivateKey>();
            for (var i = 0; i < limit; i++)
            {
                var privateKey = Build.A.PrivateKey.TestObject;
                peers.Add(GetPeer(privateKey.PublicKey), privateKey);
            }

            return peers;
        }

        private ChainHeadInfoProvider _headInfo;
        
        private TxPool CreatePool(
            ITxPoolConfig config = null, 
            ISpecProvider specProvider = null, 
            ChainHeadInfoProvider chainHeadInfoProvider = null, 
            IIncomingTxFilter incomingTxFilter = null)
        {
            specProvider ??= RopstenSpecProvider.Instance;
            ITransactionComparerProvider transactionComparerProvider =
                new TransactionComparerProvider(specProvider, _blockTree);
            
            _headInfo = chainHeadInfoProvider;
            if (_headInfo is null)
            {
                _headInfo = new ChainHeadInfoProvider(specProvider, _blockTree, _stateProvider);
            }
            
            return new TxPool(
                _ethereumEcdsa,
                _headInfo,
                config ?? new TxPoolConfig() { GasLimit = _txGasLimit },
                new TxValidator(_specProvider.ChainId), 
                _logManager, 
                transactionComparerProvider.GetDefaultComparer(),
                incomingTxFilter);
        }

        private ITxPoolPeer GetPeer(PublicKey publicKey)
        {
            ITxPoolPeer peer = Substitute.For<ITxPoolPeer>();
            peer.Id.Returns(publicKey);
            
            return peer;
        }

        private Transaction[] AddTransactionsToPool(bool sameTransactionSenderPerPeer = true, bool sameNoncePerPeer= false, int transactionsPerPeer = 10)
        {
            var transactions = GetTransactions(GetPeers(transactionsPerPeer), sameTransactionSenderPerPeer, sameNoncePerPeer);
            
            foreach (Address address in transactions.Select(t => t.SenderAddress).Distinct())
            {
                EnsureSenderBalance(address, UInt256.MaxValue);
            }
            
            foreach (var transaction in transactions)
            {
                _txPool.SubmitTx(transaction, TxHandlingOptions.PersistentBroadcast);
            }

            return transactions;
        }

        private Transaction AddTransactionToPool(bool isOwn = true)
        {
            var transaction = GetTransaction(TestItem.PrivateKeyA, Address.Zero);
            _txPool.SubmitTx(transaction, isOwn ? TxHandlingOptions.PersistentBroadcast : TxHandlingOptions.None);
            return transaction;
        }

        private void DeleteTransactionsFromPool(params Transaction[] transactions)
        {
            foreach (var transaction in transactions)
            {
                _txPool.RemoveTransaction(transaction.Hash);
            }
        }

        private Transaction[] GetTransactions(IDictionary<ITxPoolPeer, PrivateKey> peers, bool sameTransactionSenderPerPeer = true, bool sameNoncePerPeer = true, int transactionsPerPeer = 10)
        {
            var transactions = new List<Transaction>();
            foreach ((_, PrivateKey privateKey) in peers)
            {
                for (var i = 0; i < transactionsPerPeer; i++)
                {
                    transactions.Add(GetTransaction(sameTransactionSenderPerPeer ? privateKey : Build.A.PrivateKey.TestObject, Address.FromNumber((UInt256)i), sameNoncePerPeer ? UInt256.Zero : (UInt256?) i));
                }
            }

            return transactions.ToArray();
        }

        private Transaction GetTransaction(PrivateKey privateKey, Address to = null, UInt256? nonce = null)
        {
            Transaction transaction = GetTransaction(nonce ?? UInt256.Zero, GasCostOf.Transaction, nonce ?? 1000, to, Array.Empty<byte>(), privateKey);
            EnsureSenderBalance(transaction);
            return transaction;
        }

        private void EnsureSenderBalance(Transaction transaction)
        {
            if (transaction.IsEip1559)
                EnsureSenderBalance(transaction.SenderAddress, transaction.MaxFeePerGas * (UInt256)transaction.GasLimit + transaction.Value);
            else
                EnsureSenderBalance(transaction.SenderAddress, transaction.GasPrice * (UInt256)transaction.GasLimit + transaction.Value);
        }
        
        private void EnsureSenderBalance(Address address, UInt256 balance)
        {
            _stateProvider.CreateAccount(address, balance);
        }

        private Transaction GetTransaction(UInt256 nonce, long gasLimit, UInt256 gasPrice, Address to, byte[] data,
            PrivateKey privateKey)
            => Build.A.Transaction
                .WithNonce(nonce)
                .WithGasLimit(gasLimit)
                .WithGasPrice(gasPrice)
                .WithData(data)
                .To(to)
                .DeliveredBy(privateKey.PublicKey)
                .SignedAndResolved(_ethereumEcdsa, privateKey)
                .TestObject;
        
        private async Task RaiseBlockAddedToMainAndWaitForTransactions(int txCount)
        {
            SemaphoreSlim semaphoreSlim = new(0, txCount);
            _txPool.NewPending += (o, e) => semaphoreSlim.Release();
            _blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(Build.A.Block.TestObject));
            for (int i = 0; i < txCount; i++)
            {
                await semaphoreSlim.WaitAsync(10);
            }
        }
    }
}
