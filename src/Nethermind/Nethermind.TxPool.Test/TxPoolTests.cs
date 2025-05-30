// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Events;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
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
    public partial class TxPoolTests
    {
        private ILogManager _logManager;
        private IEthereumEcdsa _ethereumEcdsa;
        private ISpecProvider _specProvider;
        private TxPool _txPool;
        private IWorldState _stateProvider;
        private IBlockTree _blockTree;

        private readonly int _txGasLimit = 1_000_000;

        [SetUp]
        public void Setup()
        {
            _logManager = LimboLogs.Instance;
            _specProvider = MainnetSpecProvider.Instance;
            _ethereumEcdsa = new EthereumEcdsa(_specProvider.ChainId);
            var trieStore = TestTrieStoreFactory.Build(new MemDb(), _logManager);
            var codeDb = new MemDb();
            _stateProvider = new WorldState(trieStore, codeDb, _logManager);
            _blockTree = Substitute.For<IBlockTree>();
            Block block = Build.A.Block.WithNumber(10000000 - 1).TestObject;
            _blockTree.Head.Returns(block);
            _blockTree.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(10000000).TestObject);

            KzgPolynomialCommitments.InitializeAsync().Wait();
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
            _txPool = CreatePool(null, new TestSpecProvider(Shanghai.Instance));
            EthereumEcdsa ecdsa = new(BlockchainIds.Sepolia); // default is mainnet, we're passing sepolia
            Transaction tx = Build.A.Transaction.SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            _txPool.GetPendingTransactionsCount().Should().Be(0);
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
            _txPool.GetPendingTransactionsCount().Should().Be(0);
            result.Should().Be(AcceptTxResult.Invalid); ;
        }

        [Test]
        public void should_not_ignore_old_scheme_signatures()
        {
            _txPool = CreatePool();
            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, false).TestObject;
            EnsureSenderBalance(tx);
            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            _txPool.GetPendingTransactionsCount().Should().Be(1);
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
            _txPool.GetPendingTransactionsCount().Should().Be(1);
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
            _txPool.GetPendingTransactionsCount().Should().Be(1);
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
            _stateProvider.InsertCode(TestItem.AddressA, "A"u8.ToArray(), _specProvider.GetSpec((ForkActivation)1));
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
                specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(London.Instance);
            }
            var txPool = CreatePool(null, specProvider);
            Transaction tx = Build.A.Transaction
                .WithType(TxType.EIP1559)
                .WithChainId(TestBlockchainIds.ChainId)
                .WithMaxFeePerGas(10.GWei())
                .WithMaxPriorityFeePerGas(5.GWei())
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx);
            _blockTree.BlockAddedToMain += Raise.EventWith(_blockTree, new BlockReplacementEventArgs(Build.A.Block.WithGasLimit(10000000).TestObject));
            AcceptTxResult result = txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            txPool.GetPendingTransactionsCount().Should().Be(eip1559Enabled ? 1 : 0);
            result.Should().Be(eip1559Enabled ? AcceptTxResult.Accepted : AcceptTxResult.Invalid);
        }

        [Test]
        public void should_not_ignore_insufficient_funds_for_eip1559_transactions()
        {
            ISpecProvider specProvider = GetLondonSpecProvider();
            var txPool = CreatePool(null, specProvider);
            Transaction tx = Build.A.Transaction
                .WithType(TxType.EIP1559).WithMaxFeePerGas(20)
                .WithChainId(TestBlockchainIds.ChainId)
                .WithValue(5).SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx.SenderAddress, tx.Value - 1); // we should have InsufficientFunds if balance < tx.Value + fee
            AcceptTxResult result = txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            txPool.GetPendingTransactionsCount().Should().Be(0);
            result.Should().Be(AcceptTxResult.InsufficientFunds);
            EnsureSenderBalance(tx.SenderAddress, tx.Value);

            var headProcessed = new ManualResetEventSlim(false);
            txPool.TxPoolHeadChanged += (s, a) => headProcessed.Set();
            _blockTree.BlockAddedToMain += Raise.EventWith(_blockTree, new BlockReplacementEventArgs(Build.A.Block.WithGasLimit(10000000).TestObject));

            headProcessed.Wait();
            result = txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            result.Should().Be(AcceptTxResult.InsufficientFunds);
            txPool.GetPendingTransactionsCount().Should().Be(0);
        }

        [TestCaseSource(nameof(Eip3607RejectionsTestCases))]
        public AcceptTxResult should_reject_transactions_with_deployed_code_when_eip3607_enabled(bool eip3607Enabled, bool hasCode)
        {
            ISpecProvider specProvider = new OverridableSpecProvider(new TestSpecProvider(London.Instance), r => new OverridableReleaseSpec(r) { IsEip3607Enabled = eip3607Enabled });
            TxPool txPool = CreatePool(null, specProvider);

            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx);
            _stateProvider.InsertCode(TestItem.AddressA, hasCode ? "H"u8.ToArray() : System.Text.Encoding.UTF8.GetBytes(""), London.Instance);

            return txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
        }

        public static IEnumerable<TestCaseData> Eip3607RejectionsTestCases()
        {
            yield return new TestCaseData(false, false).Returns(AcceptTxResult.Accepted);
            yield return new TestCaseData(false, true).Returns(AcceptTxResult.Accepted);
            yield return new TestCaseData(true, false).Returns(AcceptTxResult.Accepted);
            yield return new TestCaseData(true, true).Returns(AcceptTxResult.SenderIsContract);
        }

        [Test]
        public void should_ignore_insufficient_funds_transactions()
        {
            _txPool = CreatePool();
            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            _txPool.GetPendingTransactionsCount().Should().Be(0);
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
            _txPool.GetPendingTransactionsCount().Should().Be(0);
            result.Should().Be(AcceptTxResult.OldNonce);
        }

        [Test]
        public void get_next_pending_nonce()
        {
            _txPool = CreatePool();

            // LatestPendingNonce=0, when account does not exist
            _ = _txPool.GetLatestPendingNonce(TestItem.AddressA);

            _stateProvider.CreateAccount(TestItem.AddressA, 10.Ether());

            // LatestPendingNonce=0, for a new account
            UInt256 latestNonce = _txPool.GetLatestPendingNonce(TestItem.AddressA);
            Assert.That((UInt256)0, Is.EqualTo(latestNonce));

            // LatestPendingNonce=1, when the current nonce of the account=1 and no pending transactions
            _stateProvider.IncrementNonce(TestItem.AddressA);
            _txPool.ResetAddress(TestItem.AddressA);
            latestNonce = _txPool.GetLatestPendingNonce(TestItem.AddressA);
            Assert.That((UInt256)1, Is.EqualTo(latestNonce));

            // LatestPendingNonce=1, when a pending transaction added to the pool with a gap in nonce (skipping nonce=1)
            Transaction tx = Build.A.Transaction.WithNonce(2).SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            result.Should().Be(AcceptTxResult.Accepted);
            latestNonce = _txPool.GetLatestPendingNonce(TestItem.AddressA);
            Assert.That((UInt256)1, Is.EqualTo(latestNonce));

            // LatestPendingNonce=5, when added pending transactions upto nonce=4
            tx = Build.A.Transaction.WithNonce(1).SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            result.Should().Be(AcceptTxResult.Accepted);
            tx = Build.A.Transaction.WithNonce(3).SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            result.Should().Be(AcceptTxResult.Accepted);
            tx = Build.A.Transaction.WithNonce(4).SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            result.Should().Be(AcceptTxResult.Accepted);
            latestNonce = _txPool.GetLatestPendingNonce(TestItem.AddressA);
            Assert.That((UInt256)5, Is.EqualTo(latestNonce));

            //LatestPendingNonce=5, when added a new pending transaction with a gap in nonce (skipped nonce=5)
            tx = Build.A.Transaction.WithNonce(6).SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            result.Should().Be(AcceptTxResult.Accepted);
            latestNonce = _txPool.GetLatestPendingNonce(TestItem.AddressA);
            Assert.That((UInt256)5, Is.EqualTo(latestNonce));
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
            _txPool.GetPendingTransactionsCount().Should().Be(0);
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
            txPool.GetPendingTransactionsCount().Should().Be(0);
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
            _txPool.GetPendingTransactionsCount().Should().Be(0);
            result.Should().Be(AcceptTxResult.GasLimitExceeded);
        }

        [Test]
        public void should_reject_tx_if_max_size_is_exceeded([Values(true, false)] bool sizeExceeded)
        {
            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx);

            var txPoolConfig = new TxPoolConfig() { MaxTxSize = tx.GetLength() - (sizeExceeded ? 1 : 0) };
            _txPool = CreatePool(txPoolConfig);

            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            result.Should().Be(sizeExceeded ? AcceptTxResult.MaxTxSizeExceeded : AcceptTxResult.Accepted);
            _txPool.GetPendingTransactionsCount().Should().Be(sizeExceeded ? 0 : 1);
        }

        [Test]
        public void should_accept_tx_when_base_fee_is_high()
        {
            ISpecProvider specProvider = new OverridableSpecProvider(new TestSpecProvider(London.Instance), static r => new OverridableReleaseSpec(r) { Eip1559TransitionBlock = 1 });
            BlocksConfig blocksConfig = new()
            {
                MinGasPrice = 1.GWei()
            };
            IIncomingTxFilter incomingTxFilter = new TxFilterAdapter(_blockTree, new MinGasPriceTxFilter(blocksConfig, specProvider), LimboLogs.Instance);
            _txPool = CreatePool(specProvider: specProvider, incomingTxFilter: incomingTxFilter);
            Transaction tx = Build.A.Transaction
                .WithGasLimit(Transaction.BaseTxGasCost)
                .WithGasPrice(2.GWei())
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx);
            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            result.Should().Be(AcceptTxResult.Accepted);
            _txPool.GetPendingTransactionsCount().Should().Be(1);
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
            _txPool.GetPendingTransactionsCount().Should().Be(0);
            result.Should().Be(AcceptTxResult.GasLimitExceeded);
        }

        [Test]
        public void should_ignore_tx_gas_limit_exceeded_for_eip7825()
        {
            ISpecProvider specProvider = new OverridableSpecProvider(
                new TestSpecProvider(London.Instance),
                static r => new OverridableReleaseSpec(r) { IsEip7825Enabled = true });

            var config = new TxPoolConfig { GasLimit = long.MaxValue };
            _txPool = CreatePool(config, specProvider);
            Transaction tx = Build.A.Transaction
                .WithGasLimit(Eip7825Constants.DefaultTxGasLimitCap + 1)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx);
            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            _txPool.GetPendingTransactionsCount().Should().Be(0);
            result.Should().Be(AcceptTxResult.Invalid);
        }

        [TestCase(4, 0, nameof(AcceptTxResult.FeeTooLow))]
        [TestCase(4, 11, nameof(AcceptTxResult.FeeTooLow))]
        [TestCase(4, 12, nameof(AcceptTxResult.FeeTooLow))]
        [TestCase(5, 0, nameof(AcceptTxResult.FeeTooLow))]
        [TestCase(5, 10, nameof(AcceptTxResult.FeeTooLow))]
        [TestCase(5, 11, nameof(AcceptTxResult.FeeTooLow))]
        [TestCase(9, 0, nameof(AcceptTxResult.Accepted))]
        [TestCase(9, 6, nameof(AcceptTxResult.Accepted))]
        [TestCase(9, 7, nameof(AcceptTxResult.InsufficientFunds))]
        [TestCase(9, 45, nameof(AcceptTxResult.InsufficientFunds))]
        [TestCase(11, 0, nameof(AcceptTxResult.Accepted))]
        [TestCase(11, 4, nameof(AcceptTxResult.Accepted))]
        [TestCase(11, 5, nameof(AcceptTxResult.InsufficientFunds))]
        [TestCase(15, 0, nameof(AcceptTxResult.Accepted))]
        [TestCase(16, 0, nameof(AcceptTxResult.InsufficientFunds))]
        [TestCase(16, 90, nameof(AcceptTxResult.InsufficientFunds))]
        public void should_handle_adding_tx_to_full_txPool_properly(int gasPrice, int value, string expected)
        {
            _txPool = CreatePool(new TxPoolConfig() { Size = 30 });
            Transaction[] transactions = GetTransactions(GetPeers(3), true, false);

            foreach (Address address in transactions.Select(static t => t.SenderAddress).Distinct())
            {
                EnsureSenderBalance(address, UInt256.MaxValue);
            }

            UInt256 txGasPrice = 10;
            UInt256 minGasPrice = 5;
            foreach (Transaction transaction in transactions)
            {
                transaction.GasPrice = txGasPrice;
                if (txGasPrice > minGasPrice)
                {
                    txGasPrice -= 1;
                }
                _txPool.SubmitTx(transaction, TxHandlingOptions.PersistentBroadcast);
            }

            Transaction tx = Build.A.Transaction
                .WithGasPrice((UInt256)gasPrice)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            tx.Value = (UInt256)(value * tx.GasLimit);
            EnsureSenderBalance(tx.SenderAddress, (UInt256)(15 * tx.GasLimit));
            _txPool.GetPendingTransactionsCount().Should().Be(30);
            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.None);
            result.ToString().Should().Contain(expected);
        }

        [TestCase(5, 10, nameof(AcceptTxResult.FeeTooLow))]
        [TestCase(5, 11, nameof(AcceptTxResult.FeeTooLow))]
        [TestCase(10, 0, nameof(AcceptTxResult.FeeTooLow))]
        [TestCase(10, 5, nameof(AcceptTxResult.FeeTooLow))]
        [TestCase(10, 6, nameof(AcceptTxResult.FeeTooLow))]
        [TestCase(11, 0, nameof(AcceptTxResult.Accepted))]
        [TestCase(11, 4, nameof(AcceptTxResult.Accepted))]
        [TestCase(11, 5, nameof(AcceptTxResult.InsufficientFunds))]
        [TestCase(15, 0, nameof(AcceptTxResult.Accepted))]
        [TestCase(15, 1, nameof(AcceptTxResult.InsufficientFunds))]
        [TestCase(16, 0, nameof(AcceptTxResult.Invalid))]
        [TestCase(16, 15, nameof(AcceptTxResult.Invalid))]
        [TestCase(50, 16, nameof(AcceptTxResult.Invalid))]
        public void should_handle_adding_1559_tx_to_full_txPool_properly(int gasPremium, int value, string expected)
        {
            ISpecProvider specProvider = GetLondonSpecProvider();
            _txPool = CreatePool(new TxPoolConfig() { Size = 30 }, specProvider);
            Transaction[] transactions = GetTransactions(GetPeers(3), true, false);

            foreach (Address address in transactions.Select(static t => t.SenderAddress).Distinct())
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
                .WithMaxFeePerGas((UInt256)gasPremium < 15 ? (UInt256)gasPremium : 15)
                .WithMaxPriorityFeePerGas((UInt256)gasPremium)
                .WithChainId(TestBlockchainIds.ChainId)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            tx.Value = (UInt256)(value * tx.GasLimit);
            EnsureSenderBalance(tx.SenderAddress, (UInt256)(15 * tx.GasLimit));
            _txPool.GetPendingTransactionsCount().Should().Be(30);
            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.None);
            _txPool.GetPendingTransactionsCount().Should().Be(30);
            result.ToString().Should().Contain(expected);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void should_add_underpaid_txs_to_full_TxPool_only_if_local(bool isLocal)
        {
            TxHandlingOptions txHandlingOptions = isLocal ? TxHandlingOptions.PersistentBroadcast : TxHandlingOptions.None;

            _txPool = CreatePool(new TxPoolConfig() { Size = 30 });
            Transaction[] transactions = GetTransactions(GetPeers(3), true, false);

            foreach (Address address in transactions.Select(static t => t.SenderAddress).Distinct())
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
            _txPool.GetPendingTransactionsCount().Should().Be(30);
            _txPool.GetOwnPendingTransactions().Length.Should().Be(0);
            AcceptTxResult result = _txPool.SubmitTx(tx, txHandlingOptions);
            _txPool.GetPendingTransactionsCount().Should().Be(30);
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
                    _txPool.ResetAddress(TestItem.AddressA);
                }
            }

            int numberOfTxsInTxPool = _txPool.GetPendingTransactionsCount();
            numberOfTxsInTxPool.Should().Be(numberOfTxsPossibleToExecuteBeforeGasExhaustion);
            _txPool.GetPendingTransactions()[numberOfTxsInTxPool - 1].Nonce.Should().Be((UInt256)(numberOfTxsInTxPool - 1 + numberOfStaleTxsInBucket));
        }

        [Test]
        public void should_add_tx_if_cost_of_executing_all_txs_in_bucket_exceeds_balance_but_these_with_lower_nonces_doesnt()
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

            _txPool.GetPendingTransactionsCount().Should().Be(8); // nonces 0-6 and 8
            _txPool.GetPendingTransactions().Last().Nonce.Should().Be(8);

            _txPool.SubmitTx(transactions[8], TxHandlingOptions.PersistentBroadcast).Should().Be(AcceptTxResult.AlreadyKnown);
            _txPool.SubmitTx(transactions[7], TxHandlingOptions.PersistentBroadcast).Should().Be(AcceptTxResult.Accepted);

            _txPool.GetPendingTransactionsCount().Should().Be(8); // nonces 0-7 - 8 was removed because of not enough balance
            _txPool.GetPendingTransactions().Last().Nonce.Should().Be(7);
            _txPool.GetPendingTransactions().Should().BeEquivalentTo(transactions.SkipLast(2));
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
            _txPool.GetPendingTransactionsCount().Should().Be(2);
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

            _txPool.GetPendingTransactions().Count(static t => t.GasBottleneck == 0).Should().Be(0);
            _txPool.GetPendingTransactions().Max(static t => t.GasBottleneck).Should().Be((UInt256)5);
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
            _txPool.GetPendingTransactions().Count(static t => t.GasBottleneck == 0).Should().Be(0);
        }

        [Test]
        public void should_remove_txHash_from_hashCache_when_tx_removed_because_of_txPool_size_exceeded()
        {
            _txPool = CreatePool(new TxPoolConfig() { Size = 1 });
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

            var headProcessed = new ManualResetEventSlim(false);
            _txPool.TxPoolHeadChanged += (s, a) => headProcessed.Set();

            _blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(Build.A.Block.TestObject));

            headProcessed.Wait();
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

            _txPool.GetPendingTransactions().Min(static t => t.GasBottleneck).Should().Be((UInt256)2);
            _txPool.GetPendingTransactions().Max(static t => t.GasBottleneck).Should().Be((UInt256)2);
        }

        [Test]
        public async Task should_remove_txs_with_old_nonces_when_updating_GasBottleneck()
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
            _txPool.GetPendingTransactionsCount().Should().Be(5);

            for (int i = 0; i < 3; i++)
            {
                _stateProvider.IncrementNonce(TestItem.AddressA);
            }

            await RaiseBlockAddedToMainAndWaitForTransactions(5);
            _txPool.GetPendingTransactionsCount().Should().Be(2);
            _txPool.GetPendingTransactions().Count(static t => t.GasBottleneck == 0).Should().Be(0);
            _txPool.GetPendingTransactions().Max(static t => t.GasBottleneck).Should().Be((UInt256)5);
        }

        [Test]
        public void should_broadcast_own_transactions()
        {
            _txPool = CreatePool();
            AddTransactionToPool();
            Assert.That(_txPool.GetOwnPendingTransactions().Length, Is.EqualTo(1));
        }

        [Test]
        public void should_not_broadcast_own_transactions_that_faded_out_and_came_back()
        {
            _txPool = CreatePool();
            var transaction = AddTransactionToPool();
            _txPool.RemoveTransaction(transaction.Hash);
            _txPool.RemoveTransaction(TestItem.KeccakA);
            _txPool.SubmitTx(transaction, TxHandlingOptions.None);
            Assert.That(_txPool.GetOwnPendingTransactions().Length, Is.EqualTo(0));
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
            _txPool.RemoveTransaction(Arg.Do<Hash256>(t => manualResetEvent.Set()));
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

            _txPool.GetPendingTransactionsCount().Should().Be(2);
            _txPool.GetOwnPendingTransactions().Length.Should().Be(1);

            Block block = Build.A.Block.WithTransactions(transactionA).TestObject;
            BlockReplacementEventArgs blockReplacementEventArgs = new(block, null);

            ManualResetEvent manualResetEvent = new(false);
            _txPool.RemoveTransaction(Arg.Do<Hash256>(t => manualResetEvent.Set()));
            _blockTree.BlockAddedToMain += Raise.EventWith(new object(), blockReplacementEventArgs);
            manualResetEvent.WaitOne(TimeSpan.FromMilliseconds(200));

            _txPool.GetPendingTransactionsCount().Should().Be(1);
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
                _txPool.GetPendingTransactionsCount().Should().Be(transactionsPerPeer * 7);
            }
        }

        [Test]
        public void should_add_transactions_concurrently()
        {
            int size = 3;
            TxPoolConfig config = new() { GasLimit = _txGasLimit, Size = size };
            _txPool = CreatePool(config);

            foreach (PrivateKey privateKey in TestItem.PrivateKeys)
            {
                EnsureSenderBalance(privateKey.Address, 10.Ether());
            }

            Parallel.ForEach(TestItem.PrivateKeys, k =>
            {
                for (uint i = 0; i < 100; i++)
                {
                    Transaction tx = GetTransaction(i, GasCostOf.Transaction, 10.GWei(), TestItem.AddressA, [], k);
                    _txPool.SubmitTx(tx, TxHandlingOptions.None);
                }
            });

            _txPool.GetPendingTransactionsCount().Should().Be(size);
        }

        [TestCase(true, true, 10)]
        [TestCase(false, true, 100)]
        [TestCase(true, false, 100)]
        [TestCase(false, false, 100)]
        public void should_add_pending_transactions(bool sameTransactionSenderPerPeer, bool sameNoncePerPeer, int expectedTransactions)
        {
            _txPool = CreatePool();
            AddTransactionsToPool(sameTransactionSenderPerPeer, sameNoncePerPeer);
            _txPool.GetPendingTransactionsCount().Should().Be(expectedTransactions);
        }

        [TestCase(true, true, 10)]
        [TestCase(false, true, 100)]
        [TestCase(true, false, 100)]
        [TestCase(false, false, 100)]
        public void should_remove_tx_from_txPool_when_included_in_block(bool sameTransactionSenderPerPeer, bool sameNoncePerPeer, int expectedTransactions)
        {
            _txPool = CreatePool();

            AddTransactionsToPool(sameTransactionSenderPerPeer, sameNoncePerPeer);
            _txPool.GetPendingTransactionsCount().Should().Be(expectedTransactions);

            Transaction[] transactions = _txPool.GetPendingTransactions();
            Block block = Build.A.Block.WithTransactions(transactions).TestObject;
            BlockReplacementEventArgs blockReplacementEventArgs = new(block, null);

            ManualResetEvent manualResetEvent = new(false);
            _txPool.RemoveTransaction(Arg.Do<Hash256>(t => manualResetEvent.Set()));
            _blockTree.BlockAddedToMain += Raise.EventWith(new object(), blockReplacementEventArgs);
            manualResetEvent.WaitOne(TimeSpan.FromMilliseconds(200));

            _txPool.GetPendingTransactionsCount().Should().Be(0);
        }

        [TestCase(true, true, 10)]
        [TestCase(false, true, 100)]
        [TestCase(true, false, 100)]
        [TestCase(false, false, 100)]
        public void should_not_remove_txHash_from_hashCache_when_tx_removed_because_of_including_in_block(bool sameTransactionSenderPerPeer, bool sameNoncePerPeer, int expectedTransactions)
        {
            _txPool = CreatePool();

            AddTransactionsToPool(sameTransactionSenderPerPeer, sameNoncePerPeer);
            _txPool.GetPendingTransactionsCount().Should().Be(expectedTransactions);

            Transaction[] transactions = _txPool.GetPendingTransactions();
            Block block = Build.A.Block.WithTransactions(transactions).TestObject;
            BlockReplacementEventArgs blockReplacementEventArgs = new(block, null);

            ManualResetEvent manualResetEvent = new(false);
            _txPool.RemoveTransaction(Arg.Do<Hash256>(t => manualResetEvent.Set()));
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
        public void should_return_ReplacementNotAllowed_when_trying_to_send_transaction_with_same_nonce_and_same_fee_for_same_address()
        {
            _txPool = CreatePool();
            var result1 = _txPool.SubmitTx(GetTransaction(TestItem.PrivateKeyA, TestItem.AddressA), TxHandlingOptions.PersistentBroadcast | TxHandlingOptions.ManagedNonce);
            result1.Should().Be(AcceptTxResult.Accepted);
            _txPool.GetOwnPendingTransactions().Length.Should().Be(1);
            _txPool.GetPendingTransactionsCount().Should().Be(1);
            var result2 = _txPool.SubmitTx(GetTransaction(TestItem.PrivateKeyA, TestItem.AddressB), TxHandlingOptions.PersistentBroadcast | TxHandlingOptions.ManagedNonce);
            result2.Should().Be(AcceptTxResult.ReplacementNotAllowed);
            _txPool.GetOwnPendingTransactions().Length.Should().Be(1);
            _txPool.GetPendingTransactionsCount().Should().Be(1);
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
        public void should_retrieve_added_persistent_transaction_correctly_even_if_was_evicted()
        {
            Transaction transaction = Build.A.Transaction
                .WithGasPrice(10)
                .WithSenderAddress(TestItem.AddressA)
                .SignedAndResolved().TestObject;
            Transaction transactionWithHigherFee = Build.A.Transaction
                .WithGasPrice(11)
                .WithSenderAddress(TestItem.AddressB)
                .SignedAndResolved().TestObject;
            _specProvider = Substitute.For<ISpecProvider>();
            _specProvider.ChainId.Returns(transaction.Signature.ChainId.Value);
            _txPool = CreatePool(config: new TxPoolConfig() { Size = 1 });

            EnsureSenderBalance(transaction);
            _txPool.SubmitTx(transaction, TxHandlingOptions.PersistentBroadcast).Should().Be(AcceptTxResult.Accepted);
            _txPool.TryGetPendingTransaction(transaction.Hash, out var retrievedTransaction).Should().BeTrue();
            retrievedTransaction.Should().BeEquivalentTo(transaction);

            EnsureSenderBalance(transactionWithHigherFee);
            _txPool.ResetAddress(transactionWithHigherFee.SenderAddress);
            _txPool.SubmitTx(transactionWithHigherFee, TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);
            _txPool.TryGetPendingTransaction(transactionWithHigherFee.Hash, out var retrievedTransactionWithHigherFee).Should().BeTrue();
            retrievedTransactionWithHigherFee.Should().BeEquivalentTo(transactionWithHigherFee);

            // now transaction with lower fee should be evicted from pending txs and should still be present in persistentTxs
            _txPool.TryGetPendingTransaction(transaction.Hash, out var retrievedTransactionWithLowerFee).Should().BeTrue();
            retrievedTransactionWithLowerFee.Should().BeEquivalentTo(transaction);
        }

        [Test]
        public void should_notify_added_peer_of_own_tx_when_we_are_synced([Values(0, 1)] int headNumber)
        {
            _txPool = CreatePool();
            _ = AddTransactionToPool();
            ITxPoolPeer txPoolPeer = Substitute.For<ITxPoolPeer>();
            txPoolPeer.HeadNumber.Returns(headNumber);
            txPoolPeer.Id.Returns(TestItem.PublicKeyA);
            _txPool.AddPeer(txPoolPeer);
            txPoolPeer.Received(headNumber).SendNewTransactions(Arg.Any<IEnumerable<Transaction>>(), false);
        }

        [Test]
        public async Task should_notify_peer_only_once()
        {
            _txPool = CreatePool();
            ITxPoolPeer txPoolPeer = Substitute.For<ITxPoolPeer>();
            txPoolPeer.Id.Returns(TestItem.PublicKeyA);
            _txPool.AddPeer(txPoolPeer);
            _ = AddTransactionToPool();
            await Task.Delay(500);
            txPoolPeer.Received(1).SendNewTransaction(Arg.Any<Transaction>());
            txPoolPeer.DidNotReceive().SendNewTransactions(Arg.Any<IEnumerable<Transaction>>(), false);
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
                _blockTree.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(MainnetSpecProvider.BerlinBlockNumber - 1).TestObject);
                Block block = Build.A.Block.WithNumber(MainnetSpecProvider.BerlinBlockNumber - 2).TestObject;
                _blockTree.Head.Returns(block);
            }

            _txPool = CreatePool(null, new TestSpecProvider(eip2930Enabled ? Berlin.Instance : Istanbul.Instance));
            Transaction tx = Build.A.Transaction
                .WithType(TxType.AccessList)
                .WithChainId(TestBlockchainIds.ChainId)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx);
            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            _txPool.GetPendingTransactionsCount().Should().Be(eip2930Enabled ? 1 : 0);
            result.Should().Be(eip2930Enabled ? AcceptTxResult.Accepted : AcceptTxResult.Invalid);
        }

        [Test]
        public void should_accept_only_when_synced([Values(false, true)] bool isSynced, [Values(false, true)] bool isLocal)
        {
            if (!isSynced)
            {
                _blockTree.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(MainnetSpecProvider.BerlinBlockNumber - 1).TestObject);
                Block block = Build.A.Block.WithNumber(1).TestObject;
                _blockTree.Head.Returns(block);
            }

            _txPool = CreatePool(null, new TestSpecProvider(Berlin.Instance));
            Transaction tx = Build.A.Transaction
                .WithChainId(TestBlockchainIds.ChainId)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx);
            AcceptTxResult result = _txPool.SubmitTx(tx, isLocal ? TxHandlingOptions.PersistentBroadcast : TxHandlingOptions.None);
            _txPool.GetPendingTransactionsCount().Should().Be((isSynced || isLocal) ? 1 : 0);
            result.Should().Be((isSynced || isLocal) ? AcceptTxResult.Accepted : AcceptTxResult.Syncing);
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
            _txPool.GetPendingTransactionsCount().Should().Be(0);
            result.Should().Be(AcceptTxResult.Invalid);
        }

        [Test]
        public void should_accept_zero_MaxFeePerGas_and_zero_MaxPriorityFee_1559_tx()
        {
            ISpecProvider specProvider = GetLondonSpecProvider();
            _txPool = CreatePool(null, specProvider);
            Transaction tx = Build.A.Transaction
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas(UInt256.Zero)
                .WithMaxPriorityFeePerGas(UInt256.Zero)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);
            _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            _txPool.GetPendingTransactionsCount().Should().Be(1);
        }

        [Test]
        public void should_reject_zero_MaxFeePerGas_and_positive_MaxPriorityFee_1559_tx()
        {
            ISpecProvider specProvider = GetLondonSpecProvider();
            _txPool = CreatePool(null, specProvider);
            Transaction tx = Build.A.Transaction
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas(UInt256.Zero)
                .WithMaxPriorityFeePerGas(UInt256.One)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);
            _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            _txPool.GetPendingTransactionsCount().Should().Be(0);
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
            Transaction tx = Build.A.Transaction.WithHash(TestItem.KeccakA).TestObject;
            _txPool.RemoveTransaction(tx.Hash).Should().Be(false);
        }

        [Test]
        public void should_return_false_when_trying_to_remove_tx_with_null_txHash()
        {
            _txPool = CreatePool();
            _txPool.RemoveTransaction(null).Should().Be(false);
        }

        [TestCase(0, 0, false)]
        [TestCase(0, 1, true)]
        [TestCase(1, 2, true)]
        [TestCase(10, 11, true)]
        [TestCase(100, 0, false)]
        [TestCase(100, 80, false)]
        [TestCase(100, 109, false)]
        [TestCase(100, 110, true)]
        [TestCase(1_000_000_000, 1_099_999_999, false)]
        [TestCase(1_000_000_000, 1_100_000_000, true)]
        public void should_replace_tx_with_same_sender_and_nonce_only_if_new_fee_is_at_least_10_percent_higher_than_old(int oldGasPrice, int newGasPrice, bool replaced)
        {
            ISpecProvider specProvider = GetLondonSpecProvider();
            _txPool = CreatePool(null, specProvider);
            Transaction oldTx = Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithNonce(0).WithGasPrice((UInt256)oldGasPrice).SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            Transaction newTx = Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithNonce(0).WithGasPrice((UInt256)newGasPrice).SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            _txPool.SubmitTx(oldTx, TxHandlingOptions.PersistentBroadcast);
            _txPool.SubmitTx(newTx, TxHandlingOptions.PersistentBroadcast);

            _txPool.GetPendingTransactionsCount().Should().Be(1);
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

            _txPool.GetPendingTransactionsCount().Should().Be(1);
            _txPool.GetPendingTransactions().First().Should().BeEquivalentTo(replaced ? newTx : oldTx);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(1000000)]
        public void should_always_replace_zero_fee_tx(int newGasPrice)
        {
            ISpecProvider specProvider = GetLondonSpecProvider();
            _txPool = CreatePool(null, specProvider);
            Transaction oldTx = Build.A.Transaction
                .WithNonce(0)
                .WithType(TxType.Legacy)
                .WithGasPrice(UInt256.Zero)
                .WithTo(TestItem.AddressB)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            Transaction newTx = Build.A.Transaction
                .WithNonce(0)
                .WithType(TxType.Legacy)
                .WithGasPrice((UInt256)newGasPrice)
                .WithTo(TestItem.AddressC)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            _txPool.SubmitTx(oldTx, TxHandlingOptions.PersistentBroadcast);
            _txPool.SubmitTx(newTx, TxHandlingOptions.PersistentBroadcast);

            _txPool.GetPendingTransactionsCount().Should().Be(1);
            _txPool.GetPendingTransactions().First().Should().BeEquivalentTo(newTx);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(1000000)]
        public void should_always_replace_zero_fee_tx_1559(int newMaxFeePerGas)
        {
            ISpecProvider specProvider = GetLondonSpecProvider();
            _txPool = CreatePool(null, specProvider);
            Transaction oldTx = Build.A.Transaction
                .WithNonce(0)
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas(UInt256.Zero)
                .WithMaxPriorityFeePerGas(UInt256.Zero)
                .WithTo(TestItem.AddressB)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            Transaction newTx = Build.A.Transaction
                .WithNonce(0)
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas((UInt256)newMaxFeePerGas)
                .WithMaxPriorityFeePerGas(UInt256.Zero)
                .WithTo(TestItem.AddressC)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            _txPool.SubmitTx(oldTx, TxHandlingOptions.PersistentBroadcast);
            _txPool.SubmitTx(newTx, TxHandlingOptions.PersistentBroadcast);

            _txPool.GetPendingTransactionsCount().Should().Be(1);
            _txPool.GetPendingTransactions().First().Should().BeEquivalentTo(newTx);
        }

        [Test]
        public void TooExpensiveTxFilter_correctly_calculates_cumulative_cost()
        {
            ISpecProvider specProvider = GetLondonSpecProvider();
            _txPool = CreatePool(null, specProvider);
            EnsureSenderBalance(TestItem.AddressF, 1);

            Transaction zeroCostTx = Build.A.Transaction
                .WithNonce(0)
                .WithValue(0)
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas(0)
                .WithMaxPriorityFeePerGas(0)
                .WithTo(TestItem.AddressB)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyF).TestObject;

            _txPool.SubmitTx(zeroCostTx, TxHandlingOptions.PersistentBroadcast).Should().Be(AcceptTxResult.Accepted);

            // Cumulative cost should be 1
            Transaction expensiveTx = Build.A.Transaction
                .WithNonce(1)
                .WithValue(1)
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas(0)
                .WithMaxPriorityFeePerGas(0)
                .WithTo(TestItem.AddressB)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyF).TestObject;
            _txPool.SubmitTx(expensiveTx, TxHandlingOptions.PersistentBroadcast).Should().Be(AcceptTxResult.Accepted);
        }

        [Test]
        public void should_increase_nonce_when_transaction_not_included_in_txPool_but_broadcasted()
        {
            ISpecProvider specProvider = GetLondonSpecProvider();
            _txPool = CreatePool(new TxPoolConfig { Size = 2 }, specProvider);

            ITxPoolPeer peer = Substitute.For<ITxPoolPeer>();
            peer.Id.Returns(TestItem.PublicKeyA);

            _txPool.AddPeer(peer);

            // Add two transactions with high gas price
            Transaction firstTx = Build.A.Transaction
                .WithNonce(0)
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas(100)
                .WithMaxPriorityFeePerGas(100)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            Transaction secondTx = Build.A.Transaction
                .WithNonce(1)
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas(100)
                .WithMaxPriorityFeePerGas(100)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            _txPool.SubmitTx(firstTx, TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);
            _txPool.SubmitTx(secondTx, TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);
            _txPool.GetPendingTransactions().Should().Contain(firstTx);
            _txPool.GetPendingTransactions().Should().Contain(secondTx);
            _txPool.GetOwnPendingTransactions().Should().NotContain(firstTx);
            _txPool.GetOwnPendingTransactions().Should().NotContain(secondTx);

            // Send cheap transaction => Not included in txPool
            Transaction cheapTx = Build.A.Transaction
                .WithNonce(2)
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas(1)
                .WithMaxPriorityFeePerGas(1)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            _txPool.SubmitTx(cheapTx, TxHandlingOptions.PersistentBroadcast).Should().Be(AcceptTxResult.Accepted);
            _txPool.GetPendingTransactions().Should().NotContain(cheapTx);
            _txPool.GetOwnPendingTransactions().Should().Contain(cheapTx);
            peer.Received().SendNewTransaction(cheapTx);

            // Send transaction with increased nonce => NonceGap should not appear as previous transaction is broadcasted, should be accepted
            Transaction fourthTx = Build.A.Transaction
                .WithNonce(3)
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas(1)
                .WithMaxPriorityFeePerGas(1)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            _txPool.SubmitTx(fourthTx, TxHandlingOptions.PersistentBroadcast).Should().Be(AcceptTxResult.Accepted);
            _txPool.GetPendingTransactions().Should().NotContain(fourthTx);
            _txPool.GetOwnPendingTransactions().Should().Contain(fourthTx);
            peer.Received().SendNewTransaction(fourthTx);
        }

        [Test]
        public async Task should_include_transaction_after_removal()
        {
            ISpecProvider specProvider = GetLondonSpecProvider();
            _txPool = CreatePool(new TxPoolConfig { Size = 2 }, specProvider);

            // Send cheap transaction
            Transaction txA = Build.A.Transaction
                .WithNonce(0)
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas(1)
                .WithMaxPriorityFeePerGas(1)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyB).TestObject;
            EnsureSenderBalance(TestItem.AddressB, UInt256.MaxValue);

            _txPool.SubmitTx(txA, TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);

            Transaction expensiveTx1 = Build.A.Transaction
                .WithNonce(0)
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas(100)
                .WithMaxPriorityFeePerGas(100)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            Transaction expensiveTx2 = Build.A.Transaction
                .WithNonce(1)
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas(100)
                .WithMaxPriorityFeePerGas(100)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            // Send two transactions with high gas price => txA removed from pool
            _txPool.SubmitTx(expensiveTx1, TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);
            _txPool.SubmitTx(expensiveTx2, TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);

            // Rise new block event to cleanup cash and remove one expensive tx
            _blockTree.BlockAddedToMain +=
                Raise.Event<EventHandler<BlockReplacementEventArgs>>(this,
                    new BlockReplacementEventArgs(Build.A.Block.WithTransactions(expensiveTx1).TestObject));

            // Wait four event processing
            await Task.Delay(100);

            // Send txA again => should be Accepted
            _txPool.SubmitTx(txA, TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);
        }

        [TestCase(true, 1, 1, true)]
        [TestCase(true, 1, 0, true)]
        [TestCase(true, 0, 0, true)]
        [TestCase(false, 1, 1, true)]
        [TestCase(false, 1, 0, false)]
        [TestCase(false, 0, 0, false)]
        public void Should_filter_txs_depends_on_priority_contract(bool thereIsPriorityContract, int balance, int fee, bool shouldBeAccepted)
        {
            ISpecProvider specProvider = GetLondonSpecProvider();
            _txPool = CreatePool(specProvider: specProvider, thereIsPriorityContract: thereIsPriorityContract);
            EnsureSenderBalance(TestItem.AddressF, (UInt256)balance * GasCostOf.Transaction);

            Transaction zeroCostTx = Build.A.Transaction
                .WithNonce(0)
                .WithValue(0)
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas((UInt256)fee)
                .WithMaxPriorityFeePerGas((UInt256)fee)
                .WithTo(TestItem.AddressB)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyF).TestObject;

            AcceptTxResult result = _txPool.SubmitTx(zeroCostTx, TxHandlingOptions.None);
            if (shouldBeAccepted)
            {
                result.Should().Be(AcceptTxResult.Accepted);
            }
            else
            {
                result.Should().NotBe(AcceptTxResult.Accepted);
            }
        }

        [Test]
        public void Should_not_replace_better_txs_by_worse_ones()
        {
            TxPoolConfig txPoolConfig = new TxPoolConfig { Size = 128 };
            _txPool = CreatePool(txPoolConfig);

            // send (size - 1) standard txs from different senders
            for (int i = 0; i < txPoolConfig.Size - 1; i++)
            {
                Transaction tx = Build.A.Transaction
                    .WithNonce(0)
                    .WithValue(0)
                    .WithGasPrice(10)
                    .WithTo(TestItem.AddressB)
                    .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeys[i]).TestObject;

                EnsureSenderBalance(TestItem.PrivateKeys[i].Address, UInt256.MaxValue);
                AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);

                result.Should().Be(AcceptTxResult.Accepted);
            }

            _txPool.GetPendingTransactionsCount().Should().Be(txPoolConfig.Size - 1);
            _txPool.GetPendingTransactionsBySender().Keys.Count.Should().Be(txPoolConfig.Size - 1);

            // send 1 cheap tx from sender X
            PrivateKey privateKeyOfAttacker = TestItem.PrivateKeys[txPoolConfig.Size];
            Transaction cheapTx = Build.A.Transaction
                .WithNonce(0)
                .WithValue(0)
                .WithGasPrice(1)
                .WithTo(TestItem.AddressB)
                .SignedAndResolved(_ethereumEcdsa, privateKeyOfAttacker).TestObject;

            EnsureSenderBalance(privateKeyOfAttacker.Address, UInt256.MaxValue);
            AcceptTxResult cheapTxResult = _txPool.SubmitTx(cheapTx, TxHandlingOptions.PersistentBroadcast);

            cheapTxResult.Should().Be(AcceptTxResult.Accepted);
            _txPool.GetPendingTransactionsCount().Should().Be(txPoolConfig.Size);
            _txPool.GetPendingTransactionsBySender().Keys.Count.Should().Be(txPoolConfig.Size);

            // send (size - 1) expensive txs from sender X
            for (int i = 0; i < txPoolConfig.Size - 1; i++)
            {
                Transaction tx = Build.A.Transaction
                    .WithNonce((UInt256)(i + 1))
                    .WithValue(0)
                    .WithGasPrice(1000)
                    .WithTo(TestItem.AddressB)
                    .SignedAndResolved(_ethereumEcdsa, privateKeyOfAttacker).TestObject;

                AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
                result.Should().Be(AcceptTxResult.FeeTooLowToCompete);

                // newly coming txs should evict themselves
                _txPool.GetPendingTransactionsBySender().Keys.Count.Should().Be(txPoolConfig.Size);
            }

            _txPool.GetPendingTransactionsCount().Should().Be(txPoolConfig.Size);
        }

        [Test]
        public void Should_not_replace_ready_txs_by_nonce_gap_ones()
        {
            TxPoolConfig txPoolConfig = new() { Size = 128 };
            _txPool = CreatePool(txPoolConfig);

            // send (size - 1) standard txs from different senders
            for (int i = 0; i < txPoolConfig.Size - 1; i++)
            {
                Transaction tx = Build.A.Transaction
                    .WithNonce(0)
                    .WithGasPrice(10)
                    .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeys[i]).TestObject;

                EnsureSenderBalance(TestItem.PrivateKeys[i].Address, UInt256.MaxValue);
                _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast).Should().Be(AcceptTxResult.Accepted);
            }

            _txPool.GetPendingTransactionsCount().Should().Be(txPoolConfig.Size - 1);
            _txPool.GetPendingTransactionsBySender().Keys.Count.Should().Be(txPoolConfig.Size - 1);

            const int nonceGap = 100;
            // send 1 expensive nonce-gap tx from sender X
            PrivateKey privateKeyOfAttacker = TestItem.PrivateKeys[txPoolConfig.Size];
            Transaction nonceGapTx = Build.A.Transaction
                .WithNonce(nonceGap)
                .WithGasPrice(1000)
                .SignedAndResolved(_ethereumEcdsa, privateKeyOfAttacker).TestObject;

            EnsureSenderBalance(privateKeyOfAttacker.Address, UInt256.MaxValue);
            _txPool.SubmitTx(nonceGapTx, TxHandlingOptions.PersistentBroadcast).Should().Be(AcceptTxResult.Accepted);

            _txPool.GetPendingTransactionsCount().Should().Be(txPoolConfig.Size);
            _txPool.GetPendingTransactionsBySender().Keys.Count.Should().Be(txPoolConfig.Size);

            // send (size - 1) expensive txs from sender X with consecutive nonces
            for (int i = 0; i < txPoolConfig.Size - 1; i++)
            {
                Transaction tx = Build.A.Transaction
                    .WithNonce((UInt256)(i + 1 + nonceGap))
                    .WithGasPrice(1000)
                    .SignedAndResolved(_ethereumEcdsa, privateKeyOfAttacker).TestObject;

                _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast).Should().Be(AcceptTxResult.FeeTooLowToCompete);

                // newly coming txs should evict themselves
                _txPool.GetPendingTransactionsBySender().Keys.Count.Should().Be(txPoolConfig.Size);
            }

            _txPool.GetPendingTransactionsCount().Should().Be(txPoolConfig.Size);
        }

        [TestCase(9, false)]
        [TestCase(11, true)]
        public void Should_not_add_underpaid_tx_even_if_lower_nonces_are_expensive(int gasPrice, bool expectedResult)
        {
            TxPoolConfig txPoolConfig = new TxPoolConfig { Size = 128 };
            _txPool = CreatePool(txPoolConfig);

            // send standard txs from different senders
            for (int i = 1; i < txPoolConfig.Size; i++)
            {
                Transaction tx = Build.A.Transaction
                    .WithNonce(0)
                    .WithValue(0)
                    .WithGasPrice(10)
                    .WithTo(TestItem.AddressB)
                    .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeys[i]).TestObject;

                EnsureSenderBalance(TestItem.PrivateKeys[i].Address, UInt256.MaxValue);
                _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast).Should().Be(AcceptTxResult.Accepted);
            }
            _txPool.GetPendingTransactionsCount().Should().Be(txPoolConfig.Size - 1);

            // send first tx from sender X - expensive
            Transaction firstTx = Build.A.Transaction
                .WithNonce(0)
                .WithValue(0)
                .WithGasPrice(11)
                .WithTo(TestItem.AddressB)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeys[0]).TestObject;

            EnsureSenderBalance(TestItem.PrivateKeys[0].Address, UInt256.MaxValue);
            _txPool.SubmitTx(firstTx, TxHandlingOptions.PersistentBroadcast).Should().Be(AcceptTxResult.Accepted);
            _txPool.GetPendingTransactionsCount().Should().Be(txPoolConfig.Size);

            // sender X is sending another tx with different gasprice
            Transaction secondTx = Build.A.Transaction
                .WithNonce(1)
                .WithValue(0)
                .WithGasPrice((UInt256)gasPrice)
                .WithTo(TestItem.AddressB)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeys[0]).TestObject;

            AcceptTxResult result = _txPool.SubmitTx(secondTx, TxHandlingOptions.PersistentBroadcast);

            result.Should().Be(expectedResult ? AcceptTxResult.Accepted : AcceptTxResult.FeeTooLowToCompete);
        }

        [Test]
        public void Should_correctly_add_tx_to_local_pool_when_underpaid([Values] TxType txType)
        {
            // Should only add non-blob transactions to local pool when underpaid
            bool expectedResult = txType != TxType.Blob;

            // No need to check for deposit tx
            if (txType == TxType.DepositTx) return;

            ISpecProvider specProvider = GetPragueSpecProvider();
            TxPoolConfig txPoolConfig = new TxPoolConfig { Size = 30, PersistentBlobStorageSize = 0 };
            _txPool = CreatePool(txPoolConfig, specProvider);

            Transaction[] transactions = GetTransactions(GetPeers(3), true, false);

            foreach (Address address in transactions.Select(static t => t.SenderAddress).Distinct())
            {
                EnsureSenderBalance(address, UInt256.MaxValue);
            }

            // setup full tx pool
            foreach (Transaction transaction in transactions)
            {
                transaction.GasPrice = 10.GWei();
                _txPool.SubmitTx(transaction, TxHandlingOptions.None);
            }

            _txPool.GetPendingTransactionsCount().Should().Be(30);

            Transaction testTx = Build.A.Transaction
                .WithNonce(0)
                .WithType(txType)
                .WithShardBlobTxTypeAndFieldsIfBlobTx()
                .WithAuthorizationCodeIfAuthorizationListTx()
                .WithMaxFeePerGas(9.GWei())
                .WithMaxPriorityFeePerGas(9.GWei())
                .WithGasLimit(txType != TxType.SetCode ? GasCostOf.Transaction : GasCostOf.Transaction + GasCostOf.NewAccount)
                .WithTo(TestItem.AddressB)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

            EnsureSenderBalance(TestItem.PrivateKeyA.Address, UInt256.MaxValue);

            AcceptTxResult result = _txPool.SubmitTx(testTx, TxHandlingOptions.PersistentBroadcast);
            result.Should().Be(expectedResult ? AcceptTxResult.Accepted : AcceptTxResult.FeeTooLowToCompete);
            _txPool.GetOwnPendingTransactions().Length.Should().Be(expectedResult ? 1 : 0);
            _txPool.GetPendingBlobTransactionsCount().Should().Be(0);
            _txPool.GetPendingTransactions().Should().NotContain(testTx);
        }

        static IEnumerable<(byte[], AcceptTxResult)> CodeCases()
        {
            yield return (new byte[16], AcceptTxResult.SenderIsContract);
            //Delegation code
            yield return ([.. Eip7702Constants.DelegationHeader, .. new byte[20]], AcceptTxResult.Accepted);
        }
        [TestCaseSource(nameof(CodeCases))]
        public void Sender_account_has_delegation_and_normal_code((byte[] code, AcceptTxResult expected) testCase)
        {
            ISpecProvider specProvider = GetPragueSpecProvider();
            TxPoolConfig txPoolConfig = new TxPoolConfig { Size = 30, PersistentBlobStorageSize = 0 };
            _txPool = CreatePool(txPoolConfig, specProvider);

            Transaction testTx = Build.A.Transaction
                .WithNonce(0)
                .WithMaxFeePerGas(9.GWei())
                .WithMaxPriorityFeePerGas(9.GWei())
                .WithGasLimit(100_000)
                .WithTo(TestItem.AddressB)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

            EnsureSenderBalance(TestItem.PrivateKeyA.Address, UInt256.MaxValue);

            _stateProvider.InsertCode(TestItem.PrivateKeyA.Address, testCase.code, Prague.Instance);

            AcceptTxResult result = _txPool.SubmitTx(testTx, TxHandlingOptions.PersistentBroadcast);
            result.Should().Be(testCase.expected);
        }

        private static IEnumerable<object> DifferentOrderNonces()
        {
            yield return new object[] { 0, 1, AcceptTxResult.Accepted, AcceptTxResult.NotCurrentNonceForDelegation };
            yield return new object[] { 2, 5, AcceptTxResult.NotCurrentNonceForDelegation, AcceptTxResult.NotCurrentNonceForDelegation };
            yield return new object[] { 1, 0, AcceptTxResult.NotCurrentNonceForDelegation, AcceptTxResult.Accepted };
            yield return new object[] { 5, 0, AcceptTxResult.NotCurrentNonceForDelegation, AcceptTxResult.Accepted };
        }

        [TestCaseSource(nameof(DifferentOrderNonces))]
        public void Delegated_account_can_only_have_one_tx_with_current_account_nonce(int firstNonce, int secondNonce, AcceptTxResult firstExpectation, AcceptTxResult secondExpectation)
        {
            ISpecProvider specProvider = GetPragueSpecProvider();
            TxPoolConfig txPoolConfig = new TxPoolConfig { Size = 30, PersistentBlobStorageSize = 0 };
            _txPool = CreatePool(txPoolConfig, specProvider);

            PrivateKey signer = TestItem.PrivateKeyA;
            _stateProvider.CreateAccount(signer.Address, UInt256.MaxValue);
            byte[] delegation = [.. Eip7702Constants.DelegationHeader, .. TestItem.AddressC.Bytes];
            _stateProvider.InsertCode(signer.Address, delegation.AsMemory(), Prague.Instance);

            Transaction firstTx = Build.A.Transaction
                .WithNonce((UInt256)firstNonce)
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas(9.GWei())
                .WithMaxPriorityFeePerGas(9.GWei())
                .WithGasLimit(GasCostOf.Transaction)
                .WithTo(TestItem.AddressB)
                .SignedAndResolved(_ethereumEcdsa, signer).TestObject;

            AcceptTxResult result = _txPool.SubmitTx(firstTx, TxHandlingOptions.PersistentBroadcast);
            result.Should().Be(firstExpectation);

            Transaction secondTx = Build.A.Transaction
                .WithNonce((UInt256)secondNonce)
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas(9.GWei())
                .WithMaxPriorityFeePerGas(9.GWei())
                .WithGasLimit(GasCostOf.Transaction)
                .WithTo(TestItem.AddressB)
                .SignedAndResolved(_ethereumEcdsa, signer).TestObject;

            result = _txPool.SubmitTx(secondTx, TxHandlingOptions.PersistentBroadcast);

            result.Should().Be(secondExpectation);
        }


        private static object[] NonceAndRemovedCases =
        {
            new object[]{ true, 1, AcceptTxResult.Accepted },
            new object[]{ true, 0, AcceptTxResult.Accepted},
            new object[]{ false, 0, AcceptTxResult.Accepted},
            new object[]{ false, 1, AcceptTxResult.NotCurrentNonceForDelegation},
        };
        [TestCaseSource(nameof(NonceAndRemovedCases))]
        public void Tx_with_conflicting_pending_delegation_is_rejected_then_is_accepted_after_delegation_removal(bool withRemoval, int secondNonce, AcceptTxResult expected)
        {
            ISpecProvider specProvider = GetPragueSpecProvider();
            TxPoolConfig txPoolConfig = new TxPoolConfig { Size = 30, PersistentBlobStorageSize = 0 };
            _txPool = CreatePool(txPoolConfig, specProvider);

            PrivateKey signer = TestItem.PrivateKeyA;
            PrivateKey sponsor = TestItem.PrivateKeyB;
            _stateProvider.CreateAccount(signer.Address, UInt256.MaxValue);
            _stateProvider.CreateAccount(sponsor.Address, UInt256.MaxValue);

            EthereumEcdsa ecdsa = new EthereumEcdsa(_specProvider.ChainId);

            Transaction firstTx = Build.A.Transaction
                .WithNonce(0)
                .WithType(TxType.SetCode)
                .WithMaxFeePerGas(9.GWei())
                .WithMaxPriorityFeePerGas(9.GWei())
                .WithGasLimit(100_000)
                .WithAuthorizationCode(ecdsa.Sign(signer, specProvider.ChainId, TestItem.AddressC, 0))
                .WithTo(TestItem.AddressB)
                .SignedAndResolved(_ethereumEcdsa, sponsor).TestObject;

            AcceptTxResult result = _txPool.SubmitTx(firstTx, TxHandlingOptions.PersistentBroadcast);
            result.Should().Be(AcceptTxResult.Accepted);

            Transaction secondTx = Build.A.Transaction
                .WithNonce((UInt256)secondNonce)
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas(12.GWei())
                .WithMaxPriorityFeePerGas(12.GWei())
                .WithGasLimit(GasCostOf.Transaction)
                .WithTo(TestItem.AddressB)
                .SignedAndResolved(_ethereumEcdsa, signer).TestObject;

            if (withRemoval)
            {
                _txPool.RemoveTransaction(firstTx.Hash);

                result = _txPool.SubmitTx(secondTx, TxHandlingOptions.PersistentBroadcast);

                result.Should().Be(expected);
            }
            else
            {
                result = _txPool.SubmitTx(secondTx, TxHandlingOptions.PersistentBroadcast);

                result.Should().Be(expected);
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void SetCode_tx_has_authority_with_pending_transaction_is_rejected_then_is_accepted_after_tx_removal(bool withRemoval)
        {
            ISpecProvider specProvider = GetPragueSpecProvider();
            TxPoolConfig txPoolConfig = new TxPoolConfig { Size = 30, PersistentBlobStorageSize = 0 };
            _txPool = CreatePool(txPoolConfig, specProvider);

            PrivateKey signer = TestItem.PrivateKeyA;
            _stateProvider.CreateAccount(signer.Address, UInt256.MaxValue);

            EthereumEcdsa ecdsa = new EthereumEcdsa(_specProvider.ChainId);

            Transaction firstTx = Build.A.Transaction
                .WithNonce(0)
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas(9.GWei())
                .WithMaxPriorityFeePerGas(9.GWei())
                .WithGasLimit(GasCostOf.Transaction)
                .WithTo(TestItem.AddressB)
                .SignedAndResolved(_ethereumEcdsa, signer).TestObject;

            AcceptTxResult result = _txPool.SubmitTx(firstTx, TxHandlingOptions.PersistentBroadcast);
            result.Should().Be(AcceptTxResult.Accepted);

            Transaction secondTx = Build.A.Transaction
                .WithNonce(0)
                .WithType(TxType.SetCode)
                .WithMaxFeePerGas(9.GWei())
                .WithMaxPriorityFeePerGas(9.GWei())
                .WithGasLimit(100_000)
                .WithAuthorizationCode(ecdsa.Sign(signer, specProvider.ChainId, TestItem.AddressC, 0))
                .WithTo(TestItem.AddressB)
                .SignedAndResolved(_ethereumEcdsa, signer).TestObject;

            if (withRemoval)
            {
                _txPool.RemoveTransaction(firstTx.Hash);

                result = _txPool.SubmitTx(secondTx, TxHandlingOptions.PersistentBroadcast);

                result.Should().Be(AcceptTxResult.Accepted);
            }
            else
            {
                result = _txPool.SubmitTx(secondTx, TxHandlingOptions.PersistentBroadcast);

                result.Should().Be(AcceptTxResult.DelegatorHasPendingTx);
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Tx_is_accepted_if_conflicting_pending_delegation_is_only_local(bool isLocalDelegation)
        {
            // tx pool capacity is only 1. As a first step, we add a transaction named poolTxFiller to fill the transaction pool, but it is not related to the test.
            // Then sending firstTx with delegation which is underpaid if isLocalDelegation is true.
            // when isLocalDelegation is false (not underpaid), tx is added to standard tx pool and secondTx is rejected
            // when isLocalDelegation is true (underpaid), tx is added only to local txs. Expensive secondTx is accepted
            ISpecProvider specProvider = GetPragueSpecProvider();
            TxPoolConfig txPoolConfig = new TxPoolConfig { Size = 1, PersistentBlobStorageSize = 0 };
            _txPool = CreatePool(txPoolConfig, specProvider);

            PrivateKey signer = TestItem.PrivateKeyA;
            PrivateKey sponsor = TestItem.PrivateKeyB;
            _stateProvider.CreateAccount(signer.Address, UInt256.MaxValue);
            _stateProvider.CreateAccount(sponsor.Address, UInt256.MaxValue);

            EthereumEcdsa ecdsa = new EthereumEcdsa(_specProvider.ChainId);

            // filling transaction pool
            _stateProvider.CreateAccount(TestItem.PrivateKeyC.Address, UInt256.MaxValue);
            Transaction poolFillerTx = Build.A.Transaction
                .WithNonce(0)
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas(15.GWei())
                .WithMaxPriorityFeePerGas(15.GWei())
                .WithGasLimit(GasCostOf.Transaction)
                .WithTo(TestItem.AddressB)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyC).TestObject;

            AcceptTxResult result = _txPool.SubmitTx(poolFillerTx, TxHandlingOptions.None);
            result.Should().Be(AcceptTxResult.Accepted);

            // should be added only to local txs if isLocalDelegation is true
            Transaction firstTx = Build.A.Transaction
                .WithNonce(0)
                .WithType(TxType.SetCode)
                .WithMaxFeePerGas((isLocalDelegation ? 10 : 20).GWei())
                .WithMaxPriorityFeePerGas((isLocalDelegation ? 10 : 20).GWei())
                .WithGasLimit(100_000)
                .WithAuthorizationCode(ecdsa.Sign(signer, specProvider.ChainId, TestItem.AddressC, 0))
                .WithTo(TestItem.AddressB)
                .SignedAndResolved(_ethereumEcdsa, sponsor).TestObject;

            result = _txPool.SubmitTx(firstTx, TxHandlingOptions.PersistentBroadcast);
            result.Should().Be(AcceptTxResult.Accepted);

            // should be accepted if pending delegation is only local
            Transaction secondTx = Build.A.Transaction
                .WithNonce(1) // nonce is 1 otherwise it would always be accepted
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas(25.GWei())
                .WithMaxPriorityFeePerGas(25.GWei())
                .WithGasLimit(GasCostOf.Transaction)
                .WithTo(TestItem.AddressB)
                .SignedAndResolved(_ethereumEcdsa, signer).TestObject;

            result = _txPool.SubmitTx(secondTx, TxHandlingOptions.PersistentBroadcast);
            result.Should().Be(isLocalDelegation ? AcceptTxResult.Accepted : AcceptTxResult.NotCurrentNonceForDelegation);
        }

        private static IEnumerable<object[]> SetCodeReplacedTxCases()
        {
            yield return new object[]
            {
                //Not self sponsored
                TestItem.PrivateKeyB,
                (IWorldState state, Address account, IReleaseSpec spec) =>
                {
                    state.CreateAccount(account, UInt256.MaxValue);
                    state.CreateAccount(TestItem.AddressB, UInt256.MaxValue);
                },
                AcceptTxResult.Accepted
            };
            yield return new object[]
            {
                //Self sponsored
                TestItem.PrivateKeyA,
                (IWorldState state, Address account, IReleaseSpec spec) =>
                {
                    state.CreateAccount(account, UInt256.MaxValue);
                },
                AcceptTxResult.Accepted
            };
            yield return new object[]
            {
                //Self sponsored
                TestItem.PrivateKeyA,
                //Account is delegated so the last transaction should not be accepted
                (IWorldState state, Address account, IReleaseSpec spec) =>
                {
                    state.CreateAccount(account, UInt256.MaxValue);
                    byte[] delegation = [..Eip7702Constants.DelegationHeader, ..TestItem.AddressB.Bytes];
                    state.InsertCode(account, delegation, spec);
                },
                AcceptTxResult.NotCurrentNonceForDelegation
            };
        }

        [TestCaseSource(nameof(SetCodeReplacedTxCases))]
        public void SetCode_tx_can_be_replaced_and_remove_pending_delegation_restriction(
            PrivateKey sponsor, Action<IWorldState, Address, IReleaseSpec> accountSetup, AcceptTxResult lastExpectation)
        {
            ISpecProvider specProvider = GetPragueSpecProvider();
            TxPoolConfig txPoolConfig = new TxPoolConfig { Size = 30, PersistentBlobStorageSize = 0 };
            _txPool = CreatePool(txPoolConfig, specProvider);

            PrivateKey signer = TestItem.PrivateKeyA;
            accountSetup(_stateProvider, signer.Address, Prague.Instance);

            EthereumEcdsa ecdsa = new EthereumEcdsa(_specProvider.ChainId);

            Transaction firstSetcodeTx = Build.A.Transaction
                .WithNonce(0)
                .WithType(TxType.SetCode)
                .WithMaxFeePerGas(9.GWei())
                .WithMaxPriorityFeePerGas(9.GWei())
                .WithGasLimit(100_000)
                .WithAuthorizationCode(ecdsa.Sign(signer, specProvider.ChainId, TestItem.AddressC, 0))
                .WithTo(TestItem.AddressB)
                .SignedAndResolved(_ethereumEcdsa, sponsor).TestObject;

            AcceptTxResult result = _txPool.SubmitTx(firstSetcodeTx, TxHandlingOptions.PersistentBroadcast);
            result.Should().Be(AcceptTxResult.Accepted);

            Transaction replacementTx = Build.A.Transaction
                .WithNonce(0)
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas(12.GWei())
                .WithMaxPriorityFeePerGas(12.GWei())
                .WithGasLimit(GasCostOf.Transaction)
                .WithTo(TestItem.AddressB)
                .SignedAndResolved(_ethereumEcdsa, sponsor).TestObject;

            result = _txPool.SubmitTx(replacementTx, TxHandlingOptions.PersistentBroadcast);

            result.Should().Be(AcceptTxResult.Accepted);

            Transaction thirdTx = Build.A.Transaction
            .WithNonce(1)
            .WithType(TxType.EIP1559)
            .WithMaxFeePerGas(9.GWei())
            .WithMaxPriorityFeePerGas(9.GWei())
            .WithGasLimit(GasCostOf.Transaction)
            .WithTo(TestItem.AddressB)
            .SignedAndResolved(_ethereumEcdsa, signer).TestObject;

            result = _txPool.SubmitTx(thirdTx, TxHandlingOptions.PersistentBroadcast);

            result.Should().Be(lastExpectation);
        }

        [TestCase(1ul, 2ul)]
        [TestCase(0ul, 0ul)]
        [TestCase(ulong.MaxValue, ulong.MaxValue)]
        [TestCase(0ul, ulong.MaxValue)]
        [TestCase(ulong.MaxValue, 0ul)]
        public void when_delegation_is_pending_sender_can_always_replace_tx_with_current_nonce(ulong authNonce, ulong authChainId)
        {
            ISpecProvider specProvider = GetPragueSpecProvider();
            TxPoolConfig txPoolConfig = new TxPoolConfig { Size = 10, PersistentBlobStorageSize = 10 };
            _txPool = CreatePool(txPoolConfig, specProvider);

            PrivateKey signer = TestItem.PrivateKeyA;
            PrivateKey sponsor = TestItem.PrivateKeyB;
            _stateProvider.CreateAccount(signer.Address, UInt256.MaxValue);
            _stateProvider.CreateAccount(sponsor.Address, UInt256.MaxValue);

            EthereumEcdsa ecdsa = new EthereumEcdsa(_specProvider.ChainId);

            AuthorizationTuple authTuple = ecdsa.Sign(signer, authChainId, TestItem.AddressC, authNonce);

            Transaction setCodeTx = Build.A.Transaction
                .WithNonce(0)
                .WithType(TxType.SetCode)
                .WithMaxFeePerGas((20).GWei())
                .WithMaxPriorityFeePerGas((20).GWei())
                .WithGasLimit(100_000)
                .WithAuthorizationCode(authTuple)
                .WithTo(TestItem.AddressB)
                .SignedAndResolved(_ethereumEcdsa, sponsor).TestObject;

            //Submit SetCode tx so signer has pending delegation
            AcceptTxResult result = _txPool.SubmitTx(setCodeTx, TxHandlingOptions.PersistentBroadcast);
            result.Should().Be(AcceptTxResult.Accepted);

            //Submit a replacement tx of each type with current nonce
            foreach (byte type in ((byte[])Enum.GetValues(typeof(TxType))))
            {
                UInt256 feeCap;
                1.GWei().Multiply((UInt256)type, out feeCap);
                TransactionBuilder<Transaction> builder = Build.A.Transaction
                .WithNonce(0)
                .WithType((TxType)type)
                .WithMaxFeePerGas(feeCap)
                .WithMaxPriorityFeePerGas(feeCap)
                .WithGasLimit(100_000)
                .WithTo(TestItem.AddressB);
                switch ((TxType)type)
                {
                    case TxType.Legacy:
                        break;
                    case TxType.EIP1559:
                        break;
                    case TxType.Blob:
                        //Blob tx are not allowed when another type is already in the pool
                        continue;
                    case TxType.SetCode:
                        builder.WithAuthorizationCodeIfAuthorizationListTx();
                        break;
                    case TxType.DepositTx:
                        continue;
                }
                builder.SignedAndResolved(_ethereumEcdsa, signer);

                //Signer submits a tx of all every type with current nonce
                result = _txPool.SubmitTx(builder.TestObject, TxHandlingOptions.PersistentBroadcast);
                result.Should().Be(AcceptTxResult.Accepted);
            }
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
            IIncomingTxFilter incomingTxFilter = null,
            IBlobTxStorage txStorage = null,
            bool thereIsPriorityContract = false)
        {
            specProvider ??= MainnetSpecProvider.Instance;
            ITransactionComparerProvider transactionComparerProvider =
                new TransactionComparerProvider(specProvider, _blockTree);
            txStorage ??= new BlobTxStorage();

            _headInfo = chainHeadInfoProvider;
            _headInfo ??= new ChainHeadInfoProvider(specProvider, _blockTree, _stateProvider, new CodeInfoRepository());

            return new TxPool(
                _ethereumEcdsa,
                txStorage,
                _headInfo,
                config ?? new TxPoolConfig() { GasLimit = _txGasLimit },
                new TxValidator(_specProvider.ChainId),
                _logManager,
                transactionComparerProvider.GetDefaultComparer(),
                ShouldGossip.Instance,
                incomingTxFilter,
                thereIsPriorityContract);
        }

        private ITxPoolPeer GetPeer(PublicKey publicKey)
        {
            ITxPoolPeer peer = Substitute.For<ITxPoolPeer>();
            peer.Id.Returns(publicKey);

            return peer;
        }

        private static ISpecProvider GetLondonSpecProvider()
        {
            var specProvider = Substitute.For<ISpecProvider>();
            specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(London.Instance);
            return specProvider;
        }

        private static ISpecProvider GetCancunSpecProvider()
        {
            var specProvider = Substitute.For<ISpecProvider>();
            specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(Cancun.Instance);
            return specProvider;
        }

        private static ISpecProvider GetPragueSpecProvider()
        {
            var specProvider = Substitute.For<ISpecProvider>();
            specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(Prague.Instance);
            return specProvider;
        }

        private static ISpecProvider GetOsakaSpecProvider()
        {
            var specProvider = Substitute.For<ISpecProvider>();
            specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(Osaka.Instance);
            return specProvider;
        }

        private Transaction[] AddTransactionsToPool(bool sameTransactionSenderPerPeer = true, bool sameNoncePerPeer = false, int transactionsPerPeer = 10)
        {
            var transactions = GetTransactions(GetPeers(transactionsPerPeer), sameTransactionSenderPerPeer, sameNoncePerPeer);

            foreach (Address address in transactions.Select(static t => t.SenderAddress).Distinct())
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
                    transactions.Add(GetTransaction(sameTransactionSenderPerPeer ? privateKey : Build.A.PrivateKey.TestObject, Address.FromNumber((UInt256)i), sameNoncePerPeer ? UInt256.Zero : (UInt256?)i));
                }
            }

            return transactions.ToArray();
        }

        private Transaction GetTransaction(PrivateKey privateKey, Address to = null, UInt256? nonce = null)
        {
            Transaction transaction = GetTransaction(nonce ?? UInt256.Zero, GasCostOf.Transaction, (nonce ?? 999) + 1, to, [], privateKey);
            EnsureSenderBalance(transaction);
            return transaction;
        }

        private void EnsureSenderBalance(Transaction transaction)
        {
            UInt256 requiredBalance;
            if (transaction.Supports1559)
            {
                if (UInt256.MultiplyOverflow(transaction.MaxFeePerGas, (UInt256)transaction.GasLimit, out requiredBalance))
                {
                    requiredBalance = UInt256.MaxValue;
                }
                if (UInt256.AddOverflow(requiredBalance, transaction.Value, out requiredBalance))
                {
                    requiredBalance = UInt256.MaxValue;
                }
            }
            else
            {
                if (UInt256.MultiplyOverflow(transaction.GasPrice, (UInt256)transaction.GasLimit, out requiredBalance))
                {
                    requiredBalance = UInt256.MaxValue;
                }
                if (UInt256.AddOverflow(requiredBalance, transaction.Value, out requiredBalance))
                {
                    requiredBalance = UInt256.MaxValue;
                }
            }

            EnsureSenderBalance(transaction.SenderAddress, requiredBalance);
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
                .SignedAndResolved(_ethereumEcdsa, privateKey)
                .TestObject;

        private async Task RaiseBlockAddedToMainAndWaitForTransactions(int txCount, Block block = null, Block previousBlock = null)
        {
            BlockReplacementEventArgs blockReplacementEventArgs = previousBlock is null
                ? new BlockReplacementEventArgs(block ?? Build.A.Block.TestObject)
                : new BlockReplacementEventArgs(block ?? Build.A.Block.TestObject, previousBlock);

            SemaphoreSlim semaphoreSlim = new(0, txCount);
            _txPool.NewPending += (o, e) => semaphoreSlim.Release();
            _blockTree.BlockAddedToMain += Raise.EventWith(blockReplacementEventArgs);
            for (int i = 0; i < txCount; i++)
            {
                await semaphoreSlim.WaitAsync(10);
            }
        }

        private async Task RaiseBlockAddedToMainAndWaitForNewHead(Block block, Block previousBlock = null)
        {
            BlockReplacementEventArgs blockReplacementEventArgs = previousBlock is null
                ? new BlockReplacementEventArgs(block ?? Build.A.Block.TestObject)
                : new BlockReplacementEventArgs(block ?? Build.A.Block.TestObject, previousBlock);

            Task waitTask = Wait.ForEventCondition<Block>(
                default,
                e => _txPool.TxPoolHeadChanged += e,
                e => _txPool.TxPoolHeadChanged -= e,
                e => e.Number == block.Number
            );

            _blockTree.BlockAddedToMain += Raise.EventWith(blockReplacementEventArgs);
            await waitTask;
        }

        [Test]
        public async Task should_bring_back_reorganized_txs()
        {
            const long blockNumber = 358;

            ITxPoolConfig txPoolConfig = new TxPoolConfig()
            {
                Size = 128,
                BlobsSupport = BlobsSupportMode.Disabled
            };
            _txPool = CreatePool(txPoolConfig, GetCancunSpecProvider());

            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.AddressB, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.AddressC, UInt256.MaxValue);

            Transaction[] txsA = { GetTx(TestItem.PrivateKeyA), GetTx(TestItem.PrivateKeyB) };
            Transaction[] txsB = { GetTx(TestItem.PrivateKeyC) };

            _txPool.SubmitTx(txsA[0], TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);
            _txPool.SubmitTx(txsA[1], TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);
            _txPool.SubmitTx(txsB[0], TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);

            _txPool.GetPendingTransactionsCount().Should().Be(txsA.Length + txsB.Length);
            _txPool.GetPendingBlobTransactionsCount().Should().Be(0);

            // adding block A
            Block blockA = Build.A.Block.WithNumber(blockNumber).WithTransactions(txsA).TestObject;
            await RaiseBlockAddedToMainAndWaitForNewHead(blockA);

            _txPool.GetPendingTransactionsCount().Should().Be(txsB.Length);
            _txPool.GetPendingBlobTransactionsCount().Should().Be(0);
            _txPool.TryGetPendingTransaction(txsA[0].Hash!, out _).Should().BeFalse();
            _txPool.TryGetPendingTransaction(txsA[1].Hash!, out _).Should().BeFalse();
            _txPool.TryGetPendingTransaction(txsB[0].Hash!, out _).Should().BeTrue();

            // reorganized from block A to block B
            Block blockB = Build.A.Block.WithNumber(blockNumber).WithTransactions(txsB).TestObject;
            await RaiseBlockAddedToMainAndWaitForNewHead(blockB, blockA);

            // tx from block B should be removed from tx pool
            _txPool.TryGetPendingTransaction(txsB[0].Hash!, out _).Should().BeFalse();

            // txs from reorganized blockA should be readded to tx pool
            _txPool.GetPendingTransactionsCount().Should().Be(txsA.Length);
            _txPool.TryGetPendingTransaction(txsA[0].Hash!, out Transaction tx1).Should().BeTrue();
            _txPool.TryGetPendingTransaction(txsA[1].Hash!, out Transaction tx2).Should().BeTrue();

            tx1.Should().BeEquivalentTo(txsA[0], static options => options
                .Excluding(static t => t.PoolIndex));      // ...as well as PoolIndex

            tx2.Should().BeEquivalentTo(txsA[1], static options => options
                .Excluding(static t => t.PoolIndex));      // ...as well as PoolIndex
        }

        private Transaction GetTx(PrivateKey sender)
        {
            return Build.A.Transaction
                .WithMaxFeePerGas(1.GWei())
                .WithMaxPriorityFeePerGas(1.GWei())
                .WithNonce(UInt256.Zero)
                .SignedAndResolved(_ethereumEcdsa, sender).TestObject;
        }
    }
}
