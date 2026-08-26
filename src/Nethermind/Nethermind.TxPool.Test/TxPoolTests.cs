// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Spec;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Events;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using Nethermind.TxPool.Filters;
using NSubstitute;
using NSubstitute.ReceivedExtensions;
using NUnit.Framework;

namespace Nethermind.TxPool.Test
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    [FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
    public partial class TxPoolTests
    {
        private const int Timeout = 10000;
        private ILogManager _logManager;
        private IEthereumEcdsa _ethereumEcdsa;
        private ISpecProvider _specProvider;
        private TxPool _txPool;
        private TestReadOnlyStateProvider _stateProvider;
        private TestBlockTree _blockTree;

        private const int TxGasLimit = 1_000_000;

        [OneTimeSetUp]
        public static void OneTimeSetup() => KzgPolynomialCommitments.InitializeAsync().Wait();

        [SetUp]
        public void Setup()
        {
            _logManager = LimboLogs.Instance;
            _specProvider = MainnetSpecProvider.Instance;
            _ethereumEcdsa = new EthereumEcdsa(_specProvider.ChainId);
            _stateProvider = new TestReadOnlyStateProvider();
            _blockTree = new TestBlockTree();
            Block block = Build.A.Block.WithNumber(10000000 - 1).WithBaseFeePerGas(0).TestObject;
            _blockTree.Head = block;
            _blockTree.BestSuggestedHeader = Build.A.BlockHeader.WithNumber(10000000).WithBaseFee(0).TestObject;
        }

        [TestCase(false, TestName = "should_add_peers")]
        [TestCase(true, TestName = "should_add_and_delete_peers")]
        public void should_manage_peers(bool removePeers)
        {
            _txPool = CreatePool();
            IDictionary<ITxPoolPeer, PrivateKey> peers = GetPeers();

            foreach ((ITxPoolPeer peer, _) in peers)
            {
                _txPool.AddPeer(peer);
            }

            if (removePeers)
            {
                foreach ((ITxPoolPeer peer, _) in peers)
                {
                    _txPool.RemovePeer(peer.Id);
                }
            }
        }

        [Test]
        public void should_ignore_transactions_with_different_chain_id()
        {
            _txPool = CreatePool(null, new TestSpecProvider(Shanghai.Instance));
            EthereumEcdsa ecdsa = new(BlockchainIds.Sepolia); // default is mainnet, we're passing sepolia
            Transaction tx = Build.A.Transaction.SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(0));
                Assert.That(result, Is.EqualTo(AcceptTxResult.Invalid));
            }
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
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(0));
                Assert.That(result, Is.EqualTo(AcceptTxResult.Invalid));
            }
        }

        [TestCase(true, false)]
        [TestCase(true, true)]
        [TestCase(false, false)]
        [TestCase(false, true)]
        public void should_validate_eip2780_intrinsic_gas_after_sender_recovery(bool selfTransfer, bool valueTransfer)
        {
            _txPool = CreatePool(null, new TestSpecProvider(Amsterdam.Instance));
            Address sender = TestItem.PrivateKeyA.Address;
            Transaction tx = Build.A.Transaction
                .WithTo(selfTransfer ? sender : TestItem.AddressB)
                .WithValue(valueTransfer ? 1 : 0)
                .WithGasLimit(GasCostOf.TransactionEip2780)
                .Signed(_ethereumEcdsa, TestItem.PrivateKeyA)
                .TestObject;
            EnsureSenderBalance(sender, UInt256.MaxValue);

            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            AcceptTxResult expected = selfTransfer ? AcceptTxResult.Accepted : AcceptTxResult.Invalid;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(tx.SenderAddress, Is.EqualTo(sender));
                Assert.That(result, Is.EqualTo(expected));
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(selfTransfer ? 1 : 0));
            }
        }

        [Test]
        public void should_reject_irrecoverable_eip2780_intrinsic_gas_before_sender_recovery()
        {
            Address sender = TestItem.PrivateKeyA.Address;
            Transaction tx = Build.A.Transaction
                .WithTo(sender)
                .WithValue(0)
                .WithGasLimit(GasCostOf.TransactionEip2780 - 1)
                .Signed(_ethereumEcdsa, TestItem.PrivateKeyA)
                .TestObject;
            _txPool = CreatePool(
                specProvider: new TestSpecProvider(Amsterdam.Instance),
                ethereumEcdsa: NullEthereumEcdsa.Instance);

            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);

            Assert.That(result, Is.EqualTo(AcceptTxResult.Invalid));
        }

        [Test]
        public void should_reject_wrong_chain_id_before_sender_recovery()
        {
            ulong wrongChainId = TestBlockchainIds.ChainId + 1;
            EthereumEcdsa wrongChainEcdsa = new(wrongChainId);
            Address sender = TestItem.PrivateKeyA.Address;
            Transaction tx = Build.A.Transaction
                .WithType(TxType.EIP1559)
                .WithTo(sender)
                .WithValue(0)
                .WithGasLimit(GasCostOf.TransactionEip2780)
                .Signed(wrongChainEcdsa, TestItem.PrivateKeyA)
                .TestObject;
            _txPool = CreatePool(
                specProvider: new TestSpecProvider(Amsterdam.Instance),
                ethereumEcdsa: NullEthereumEcdsa.Instance);

            Assert.That(tx.ChainId, Is.EqualTo(wrongChainId), "precondition: the transaction targets a different chain");
            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);

            Assert.That(result, Is.EqualTo(AcceptTxResult.Invalid));
        }

        [TestCase(true, GasCostOf.TransactionEip2780)]
        [TestCase(false, GasCostOf.TransactionEip2780 + Eip8038Constants.ColdAccountAccess)]
        public async Task should_build_eip2780_transaction_with_intrinsic_gas_after_txpool_sender_recovery(
            bool selfTransfer,
            ulong expectedGasUsed)
        {
            TestSpecProvider specProvider = new(Amsterdam.Instance) { AllowTestChainOverride = false };
            using BasicTestBlockchain chain = await BasicTestBlockchain.Create(b => b.AddSingleton<ISpecProvider>(specProvider));
            _txPool = CreatePool(null, specProvider);
            Address sender = TestItem.PrivateKeyB.Address;
            ulong nonce = chain.StateReader.GetNonce(chain.BlockTree.Head!.Header, sender);
            Transaction tx = Build.A.Transaction
                .WithTo(selfTransfer ? sender : TestItem.AddressC)
                .WithValue(0)
                .WithNonce(nonce)
                .WithGasLimit(100_000)
                .Signed(_ethereumEcdsa, TestItem.PrivateKeyB)
                .TestObject;
            EnsureSenderBalance(sender, UInt256.MaxValue);

            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            Block block = await chain.AddBlock(_txPool.GetPendingTransactions().Single());

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
                Assert.That(tx.SenderAddress, Is.EqualTo(sender));
                Assert.That(chain.ReceiptStorage.Get(block)[0].GasUsed, Is.EqualTo(expectedGasUsed));
                Assert.That(block.GasUsed, Is.EqualTo(expectedGasUsed));
            }
        }

        [Test]
        public void should_reject_unsigned_eip2780_transaction_before_sender_recovery()
        {
            Transaction tx = Build.A.Transaction
                .WithTo(TestItem.AddressA)
                .WithValue(0)
                .WithGasLimit(GasCostOf.TransactionEip2780)
                .Signed(_ethereumEcdsa, TestItem.PrivateKeyA)
                .TestObject;
            _ = tx.Hash;
            tx.Signature = null;
            _txPool = CreatePool(
                specProvider: new TestSpecProvider(Amsterdam.Instance),
                ethereumEcdsa: NullEthereumEcdsa.Instance);

            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);

            Assert.That(result, Is.EqualTo(AcceptTxResult.Invalid));
        }

        [Test]
        public void should_validate_eip2780_intrinsic_cap_after_sender_recovery()
        {
            const long maxTxSize = 1_100_000;
            OverridableReleaseSpec spec = new(Amsterdam.Instance)
            {
                IsEip7623Enabled = false,
                IsEip7976Enabled = false,
            };
            ulong dataCostPerByte = GasCostOf.TxDataZero * spec.GasCosts.TxDataNonZeroMultiplier;
            int dataLength = checked((int)((Eip7825Constants.DefaultTxGasLimitCap - GasCostOf.TransactionEip2780) / dataCostPerByte));
            byte[] data = new byte[dataLength];
            data.AsSpan().Fill(1);
            _txPool = CreatePool(new TxPoolConfig { MaxTxSize = maxTxSize }, new TestSpecProvider(spec));
            Address sender = TestItem.PrivateKeyA.Address;
            Transaction tx = Build.A.Transaction
                .WithTo(sender)
                .WithValue(0)
                .WithData(data)
                .WithGasLimit(Eip7825Constants.DefaultTxGasLimitCap)
                .Signed(_ethereumEcdsa, TestItem.PrivateKeyA)
                .TestObject;
            EnsureSenderBalance(sender, UInt256.MaxValue);

            TxValidator validator = new(_specProvider.ChainId);
            ValidationResult beforeRecovery = validator.IsWellFormed(tx, spec);
            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            ValidationResult afterRecovery = validator.IsWellFormed(tx, spec);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(beforeRecovery.AsBool(), Is.False);
                Assert.That(beforeRecovery.IsIntrinsicGasError, Is.True);
                Assert.That(tx.SenderAddress, Is.EqualTo(sender));
                Assert.That(afterRecovery.AsBool(), Is.True);
                Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(1));
            }
        }

        [Test]
        public void should_not_ignore_old_scheme_signatures()
        {
            _txPool = CreatePool();
            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, false).TestObject;
            EnsureSenderBalance(tx);
            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(1));
                Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
            }
        }

        [Test]
        public void should_ignore_already_known()
        {
            _txPool = CreatePool();
            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx);
            AcceptTxResult result1 = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            AcceptTxResult result2 = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(1));
                Assert.That(result1, Is.EqualTo(AcceptTxResult.Accepted));
                Assert.That(result2, Is.EqualTo(AcceptTxResult.AlreadyKnown));
            }
        }

        [Test]
        public void should_add_valid_transactions_recovering_its_address()
        {
            _txPool = CreatePool();
            Transaction tx = Build.A.Transaction
                .WithGasLimit(TxGasLimit)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx);
            tx.SenderAddress = null;
            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(1));
                Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
            }
        }

        [Test]
        public void should_reject_transactions_from_contract_address()
        {
            _txPool = CreatePool();
            Transaction tx = Build.A.Transaction
                .WithGasLimit(TxGasLimit)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx);
            _stateProvider.InsertCode(TestItem.AddressA, "A"u8.ToArray(), _specProvider.GetSpec((ForkActivation)1));
            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            Assert.That(result, Is.EqualTo(AcceptTxResult.SenderIsContract));
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
            TxPool txPool = CreatePool(null, specProvider);
            Transaction tx = Build.A.Transaction
                .WithType(TxType.EIP1559)
                .WithChainId(TestBlockchainIds.ChainId)
                .WithMaxFeePerGas(10.GWei)
                .WithMaxPriorityFeePerGas(5.GWei)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx);
            _blockTree.RaiseBlockAddedToMain(new BlockReplacementEventArgs(Build.A.Block.WithGasLimit(10000000).TestObject));
            AcceptTxResult result = txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(txPool.GetPendingTransactionsCount(), Is.EqualTo(eip1559Enabled ? 1 : 0));
                Assert.That(result, Is.EqualTo(eip1559Enabled ? AcceptTxResult.Accepted : AcceptTxResult.Invalid));
            }
        }

        [Test]
        public void should_not_ignore_insufficient_funds_for_eip1559_transactions()
        {
            ISpecProvider specProvider = GetLondonSpecProvider();
            TxPool txPool = CreatePool(null, specProvider);
            Transaction tx = Build.A.Transaction
                .WithType(TxType.EIP1559).WithMaxFeePerGas(20)
                .WithChainId(TestBlockchainIds.ChainId)
                .WithValue(5).SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx.SenderAddress, tx.Value - 1); // we should have InsufficientFunds if balance < tx.Value + fee
            AcceptTxResult result = txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            Assert.That(txPool.GetPendingTransactionsCount(), Is.EqualTo(0));
            Assert.That(result, Is.EqualTo(AcceptTxResult.InsufficientFunds));
            EnsureSenderBalance(tx.SenderAddress, tx.Value);

            _blockTree.RaiseBlockAddedToMain(new BlockReplacementEventArgs(Build.A.Block.WithGasLimit(10000000).TestObject));

            // Head processing runs async via Task.Run; poll for hash cache to be cleared
            // (the observable side effect) rather than waiting for the TxPoolHeadChanged event
            SpinWait.SpinUntil(() => !txPool.IsKnown(tx.Hash), TimeSpan.FromSeconds(30));

            result = txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            Assert.That(result, Is.EqualTo(AcceptTxResult.InsufficientFunds));
            Assert.That(txPool.GetPendingTransactionsCount(), Is.EqualTo(0));
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
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(0));
                Assert.That(result, Is.EqualTo(AcceptTxResult.InsufficientFunds));
            }
        }

        [Test]
        public void should_ignore_old_nonce_transactions()
        {
            _txPool = CreatePool();
            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx);
            _stateProvider.IncrementNonce(tx.SenderAddress);
            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(0));
                Assert.That(result, Is.EqualTo(AcceptTxResult.OldNonce));
            }
        }

        [Test]
        public void get_next_pending_nonce()
        {
            _txPool = CreatePool();

            // LatestPendingNonce=0, when account does not exist
            _ = _txPool.GetLatestPendingNonce(TestItem.AddressA);

            _stateProvider.CreateAccount(TestItem.AddressA, 10.Ether);

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
            Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
            latestNonce = _txPool.GetLatestPendingNonce(TestItem.AddressA);
            Assert.That((UInt256)1, Is.EqualTo(latestNonce));

            // LatestPendingNonce=5, when added pending transactions up to nonce=4
            tx = Build.A.Transaction.WithNonce(1).SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
            tx = Build.A.Transaction.WithNonce(3).SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
            tx = Build.A.Transaction.WithNonce(4).SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
            latestNonce = _txPool.GetLatestPendingNonce(TestItem.AddressA);
            Assert.That((UInt256)5, Is.EqualTo(latestNonce));

            //LatestPendingNonce=5, when added a new pending transaction with a gap in nonce (skipped nonce=5)
            tx = Build.A.Transaction.WithNonce(6).SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
            latestNonce = _txPool.GetLatestPendingNonce(TestItem.AddressA);
            Assert.That((UInt256)5, Is.EqualTo(latestNonce));
        }

        [TestCase(false, TestName = "should_ignore_overflow_transactions")]
        [TestCase(true, TestName = "should_ignore_overflow_transactions_gas_premium_and_fee_cap")]
        public void should_ignore_overflow_transactions(bool eip1559)
        {
            ISpecProvider specProvider = eip1559 ? GetLondonSpecProvider() : _specProvider;
            TxPool txPool = CreatePool(null, specProvider);

            TransactionBuilder<Transaction> builder = Build.A.Transaction
                .WithGasPrice(UInt256.MaxValue / Transaction.BaseTxGasCost)
                .WithGasLimit(Transaction.BaseTxGasCost)
                .WithValue(Transaction.BaseTxGasCost);

            if (eip1559)
            {
                builder
                    .WithMaxFeePerGas(UInt256.MaxValue - 10)
                    .WithMaxPriorityFeePerGas((UInt256)15)
                    .WithType(TxType.EIP1559);
            }

            Transaction tx = builder.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            if (eip1559)
                EnsureSenderBalance(tx.SenderAddress, UInt256.MaxValue);
            else
                EnsureSenderBalance(tx);
            AcceptTxResult result = txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(txPool.GetPendingTransactionsCount(), Is.EqualTo(0));
                Assert.That(result, Is.EqualTo(AcceptTxResult.Int256Overflow));
            }
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
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(0));
                Assert.That(result, Is.EqualTo(AcceptTxResult.GasLimitExceeded));
            }
        }

        [Test]
        public void should_reject_tx_if_max_size_is_exceeded([Values(true, false)] bool sizeExceeded)
        {
            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx);

            TxPoolConfig txPoolConfig = new() { MaxTxSize = tx.GetLength() - (sizeExceeded ? 1 : 0) };
            _txPool = CreatePool(txPoolConfig);

            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result, Is.EqualTo(sizeExceeded ? AcceptTxResult.MaxTxSizeExceeded : AcceptTxResult.Accepted));
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(sizeExceeded ? 0 : 1));
            }
        }

        [Test]
        public void should_accept_tx_when_base_fee_is_high()
        {
            ISpecProvider specProvider = new OverridableSpecProvider(new TestSpecProvider(London.Instance), static r => new OverridableReleaseSpec(r) { Eip1559TransitionBlock = 1 });
            BlocksConfig blocksConfig = new()
            {
                MinGasPrice = 1.GWei
            };
            IIncomingTxFilter incomingTxFilter = new TxFilterAdapter(_blockTree, new MinGasPriceTxFilter(blocksConfig), LimboLogs.Instance, specProvider);
            _txPool = CreatePool(specProvider: specProvider, incomingTxFilter: incomingTxFilter);
            Transaction tx = Build.A.Transaction
                .WithGasLimit(Transaction.BaseTxGasCost)
                .WithGasPrice(2.GWei)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx);
            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(1));
            }
        }

        [Test]
        public void should_ignore_tx_gas_limit_exceeded()
        {
            _txPool = CreatePool();
            Transaction tx = Build.A.Transaction
                .WithGasLimit(TxGasLimit + 1)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx);
            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(0));
                Assert.That(result, Is.EqualTo(AcceptTxResult.GasLimitExceeded));
            }
        }

        [Test]
        public void should_ignore_tx_gas_limit_exceeded_for_eip7825()
        {
            ISpecProvider specProvider = new OverridableSpecProvider(
                new TestSpecProvider(London.Instance),
                static r => new OverridableReleaseSpec(r) { IsEip7825Enabled = true });

            TxPoolConfig config = new() { GasLimit = long.MaxValue };
            _txPool = CreatePool(config, specProvider);
            Transaction tx = Build.A.Transaction
                .WithGasLimit(Eip7825Constants.DefaultTxGasLimitCap + 1)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx);
            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(0));
                Assert.That(result, Is.EqualTo(AcceptTxResult.Invalid));
            }
        }

        private static IEnumerable<TestCaseData> FullTxPoolCases()
        {
            yield return new TestCaseData(4, 0, AcceptTxResult.FeeTooLow);
            yield return new TestCaseData(4, 11, AcceptTxResult.FeeTooLow);
            yield return new TestCaseData(4, 12, AcceptTxResult.FeeTooLow);
            yield return new TestCaseData(5, 0, AcceptTxResult.FeeTooLow);
            yield return new TestCaseData(5, 10, AcceptTxResult.FeeTooLow);
            yield return new TestCaseData(5, 11, AcceptTxResult.FeeTooLow);
            yield return new TestCaseData(9, 0, AcceptTxResult.Accepted);
            yield return new TestCaseData(9, 6, AcceptTxResult.Accepted);
            yield return new TestCaseData(9, 7, AcceptTxResult.InsufficientFunds);
            yield return new TestCaseData(9, 45, AcceptTxResult.InsufficientFunds);
            yield return new TestCaseData(11, 0, AcceptTxResult.Accepted);
            yield return new TestCaseData(11, 4, AcceptTxResult.Accepted);
            yield return new TestCaseData(11, 5, AcceptTxResult.InsufficientFunds);
            yield return new TestCaseData(15, 0, AcceptTxResult.Accepted);
            yield return new TestCaseData(16, 0, AcceptTxResult.InsufficientFunds);
            yield return new TestCaseData(16, 90, AcceptTxResult.InsufficientFunds);
        }

        [TestCaseSource(nameof(FullTxPoolCases))]
        public void should_handle_adding_tx_to_full_txPool_properly(int gasPrice, int value, AcceptTxResult expected)
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
            tx.Value = (ulong)value * tx.GasLimit;
            EnsureSenderBalance(tx.SenderAddress, (UInt256)(15UL * tx.GasLimit));
            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(30));
            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.None);
            Assert.That(result, Is.EqualTo(expected));
        }

        private static IEnumerable<TestCaseData> Full1559TxPoolCases()
        {
            yield return new TestCaseData(5, 10, AcceptTxResult.FeeTooLow);
            yield return new TestCaseData(5, 11, AcceptTxResult.FeeTooLow);
            yield return new TestCaseData(10, 0, AcceptTxResult.FeeTooLow);
            yield return new TestCaseData(10, 5, AcceptTxResult.FeeTooLow);
            yield return new TestCaseData(10, 6, AcceptTxResult.FeeTooLow);
            yield return new TestCaseData(11, 0, AcceptTxResult.Accepted);
            yield return new TestCaseData(11, 4, AcceptTxResult.Accepted);
            yield return new TestCaseData(11, 5, AcceptTxResult.InsufficientFunds);
            yield return new TestCaseData(15, 0, AcceptTxResult.Accepted);
            yield return new TestCaseData(15, 1, AcceptTxResult.InsufficientFunds);
            yield return new TestCaseData(16, 0, AcceptTxResult.Invalid);
            yield return new TestCaseData(16, 15, AcceptTxResult.Invalid);
            yield return new TestCaseData(50, 16, AcceptTxResult.Invalid);
        }

        [TestCaseSource(nameof(Full1559TxPoolCases))]
        public void should_handle_adding_1559_tx_to_full_txPool_properly(int gasPremium, int value, AcceptTxResult expected)
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
            tx.Value = (ulong)value * tx.GasLimit;
            EnsureSenderBalance(tx.SenderAddress, (UInt256)(15UL * tx.GasLimit));
            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(30));
            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.None);
            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(30));
            Assert.That(result, Is.EqualTo(expected));
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
            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(30));
            Assert.That(_txPool.GetOwnPendingTransactions().Length, Is.EqualTo(0));
            AcceptTxResult result = _txPool.SubmitTx(tx, txHandlingOptions);
            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(30));
            Assert.That(_txPool.GetOwnPendingTransactions().Length, Is.EqualTo(isLocal ? 1 : 0));
            Assert.That(result, Is.EqualTo(isLocal ? AcceptTxResult.Accepted : AcceptTxResult.FeeTooLow));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(10)]
        public void should_not_add_tx_if_already_pending_lower_nonces_are_exhausting_balance(int numberOfTxsPossibleToExecuteBeforeGasExhaustion)
        {
            const int gasPrice = 10;
            const int value = 1;
            int oneTxPrice = TxGasLimit * gasPrice + value;
            _txPool = CreatePool();
            Transaction[] transactions = new Transaction[10];

            EnsureSenderBalance(TestItem.AddressA, (UInt256)(oneTxPrice * numberOfTxsPossibleToExecuteBeforeGasExhaustion));

            Parallel.For(0, 10, i =>
            {
                transactions[i] = Build.A.Transaction
                    .WithSenderAddress(TestItem.AddressA)
                    .WithNonce(i)
                    .WithGasPrice((UInt256)gasPrice)
                    .WithGasLimit(TxGasLimit)
                    .WithValue(value)
                    .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            });

            for (int i = 0; i < 10; i++)
            {
                _txPool.SubmitTx(transactions[i], TxHandlingOptions.PersistentBroadcast);
            }

            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(numberOfTxsPossibleToExecuteBeforeGasExhaustion));
        }

        [TestCase(1, 0)]
        [TestCase(2, 1)]
        [TestCase(5, 5)]
        [TestCase(10, 3)]
        public void should_not_count_txs_with_stale_nonces_when_calculating_cumulative_cost(int numberOfTxsPossibleToExecuteBeforeGasExhaustion, int numberOfStaleTxsInBucket)
        {
            const int gasPrice = 10;
            const int value = 1;
            int oneTxPrice = TxGasLimit * gasPrice + value;
            _txPool = CreatePool();

            EnsureSenderBalance(TestItem.AddressA, (UInt256)(oneTxPrice * numberOfTxsPossibleToExecuteBeforeGasExhaustion));

            int count = numberOfTxsPossibleToExecuteBeforeGasExhaustion * 2;
            using ArrayPoolList<Transaction> transactions = new(count, count);
            Parallel.For(0, count, i =>
            {
                transactions[i] = Build.A.Transaction
                    .WithSenderAddress(TestItem.AddressA)
                    .WithNonce(i)
                    .WithGasPrice((UInt256)gasPrice)
                    .WithGasLimit(TxGasLimit)
                    .WithValue(value)
                    .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            });

            for (int i = 0; i < count; i++)
            {
                _txPool.SubmitTx(transactions[i], TxHandlingOptions.PersistentBroadcast);

                if (i < numberOfStaleTxsInBucket)
                {
                    _stateProvider.IncrementNonce(TestItem.AddressA);
                    _txPool.ResetAddress(TestItem.AddressA);
                }
            }

            int numberOfTxsInTxPool = _txPool.GetPendingTransactionsCount();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(numberOfTxsInTxPool, Is.EqualTo(numberOfTxsPossibleToExecuteBeforeGasExhaustion));
                Assert.That(_txPool.GetPendingTransactions()[numberOfTxsInTxPool - 1].Nonce, Is.EqualTo((ulong)(numberOfTxsInTxPool - 1 + numberOfStaleTxsInBucket)));
            }
        }

        [Test]
        public void should_add_tx_if_cost_of_executing_all_txs_in_bucket_exceeds_balance_but_these_with_lower_nonces_do_not()
        {
            const int count = 10;
            const int gasPrice = 10;
            const int value = 1;
            int oneTxPrice = TxGasLimit * gasPrice + value;
            _txPool = CreatePool();
            Transaction[] transactions = new Transaction[count];

            EnsureSenderBalance(TestItem.AddressA, (UInt256)(oneTxPrice * 8));

            Parallel.For(0, count, i =>
            {
                transactions[i] = Build.A.Transaction
                    .WithSenderAddress(TestItem.AddressA)
                    .WithNonce(i)
                    .WithGasPrice((UInt256)gasPrice)
                    .WithGasLimit(TxGasLimit)
                    .WithValue(value)
                    .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            });

            for (int i = 0; i < count; i++)
            {
                if (i != 7)
                {
                    _txPool.SubmitTx(transactions[i], TxHandlingOptions.PersistentBroadcast);
                }
            }

            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(8)); // nonces 0-6 and 8
            Assert.That(_txPool.GetPendingTransactions().Last().Nonce, Is.EqualTo(8UL));

            Assert.That(_txPool.SubmitTx(transactions[8], TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.AlreadyKnown));
            Assert.That(_txPool.SubmitTx(transactions[7], TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Accepted));

            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(8)); // nonces 0-7 - 8 was removed because of not enough balance
            Assert.That(_txPool.GetPendingTransactions().Last().Nonce, Is.EqualTo(7UL));
            Assert.That(_txPool.GetPendingTransactions(), Is.EqualTo(transactions.SkipLast(2)));
        }

        [Test]
        public void should_discard_tx_because_of_overflow_of_cumulative_cost_of_this_tx_and_all_txs_with_lower_nonces()
        {
            _txPool = CreatePool();

            Transaction[] transactions = new Transaction[3];

            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            UInt256.MaxValue.Divide(GasCostOf.Transaction * 2, out UInt256 halfOfMaxGasPriceWithoutOverflow);

            Parallel.For(0, 3, i =>
            {
                transactions[i] = Build.A.Transaction
                    .WithSenderAddress(TestItem.AddressA)
                    .WithNonce(i)
                    .WithGasPrice(halfOfMaxGasPriceWithoutOverflow)
                    .WithGasLimit(GasCostOf.Transaction)
                    .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            });

            _txPool.SubmitTx(transactions[0], TxHandlingOptions.PersistentBroadcast);
            _txPool.SubmitTx(transactions[1], TxHandlingOptions.PersistentBroadcast);

            transactions[2].GasPrice = 5;
            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(2));
            Assert.That(_txPool.SubmitTx(transactions[2], TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Int256Overflow));
        }

        [Test]
        public async Task should_not_dump_GasBottleneck_of_all_txs_in_bucket_if_first_tx_in_bucket_has_insufficient_balance_but_has_old_nonce()
        {
            _txPool = CreatePool();
            Transaction[] transactions = new Transaction[5];

            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            Parallel.For(0, 5, i =>
            {
                transactions[i] = Build.A.Transaction
                    .WithSenderAddress(TestItem.AddressA)
                    .WithNonce(i)
                    .WithGasPrice((UInt256)(i + 2))
                    .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
                _txPool.SubmitTx(transactions[i], TxHandlingOptions.PersistentBroadcast);
            });

            for (int i = 0; i < 3; i++)
            {
                _stateProvider.IncrementNonce(TestItem.AddressA);
            }

            transactions[0].Value = 100000;

            await RaiseBlockAddedToMainAndWaitForTransactions(5);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.GetPendingTransactions().Count(static t => t.GasBottleneck == 0), Is.EqualTo(0));
                Assert.That(_txPool.GetPendingTransactions().Max(static t => t.GasBottleneck), Is.EqualTo((UInt256)5));
            }
        }

        [Test]
        public async Task should_not_fail_if_there_is_no_current_nonce_in_bucket()
        {
            _txPool = CreatePool();
            Transaction[] transactions = new Transaction[5];

            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            Parallel.For(0, 3, i =>
            {
                transactions[i] = Build.A.Transaction
                    .WithSenderAddress(TestItem.AddressA)
                    .WithNonce(i + 4)
                    .WithGasPrice((UInt256)(i + 2))
                    .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
                _txPool.SubmitTx(transactions[i], TxHandlingOptions.PersistentBroadcast);
            });

            for (int i = 0; i < 3; i++)
            {
                _stateProvider.IncrementNonce(TestItem.AddressA);
            }

            await RaiseBlockAddedToMainAndWaitForTransactions(3);
            Assert.That(_txPool.GetPendingTransactions().Count(static t => t.GasBottleneck == 0), Is.EqualTo(0));
        }

        [Test]
        public async Task should_remove_txHash_from_hashCache_when_tx_removed_because_of_txPool_size_exceeded()
        {
            _txPool = CreatePool(new TxPoolConfig() { Size = 1 });
            Transaction transaction = Build.A.Transaction
                .WithSenderAddress(TestItem.AddressA)
                .WithGasPrice(2)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(transaction);
            _txPool.SubmitTx(transaction, TxHandlingOptions.PersistentBroadcast);

            Assert.That(_txPool.IsKnown(transaction.Hash), Is.True);

            Transaction higherPriorityTx = Build.A.Transaction
                .WithSenderAddress(TestItem.AddressB)
                .WithGasPrice(100)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyB).TestObject;
            EnsureSenderBalance(higherPriorityTx);
            _txPool.SubmitTx(higherPriorityTx, TxHandlingOptions.PersistentBroadcast);

            await RaiseBlockAddedToMainAndWaitForNewHead(Build.A.Block.TestObject);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.IsKnown(higherPriorityTx.Hash), Is.True);
                Assert.That(_txPool.IsKnown(transaction.Hash), Is.False);
            }
        }

        [Test]
        public async Task EvictTransaction_surfaces_a_drop_and_clears_the_hash_cache_so_the_tx_can_re_enter()
        {
            _txPool = CreatePool();
            Transaction transaction = Build.A.Transaction
                .WithSenderAddress(TestItem.AddressA)
                .WithGasPrice(2)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(transaction);
            _txPool.SubmitTx(transaction, TxHandlingOptions.PersistentBroadcast);

            await RaiseBlockAddedToMainAndWaitForNewHead(Build.A.Block.TestObject);
            Assert.That(_txPool.IsKnown(transaction.Hash), Is.True);

            Transaction dropped = null;
            _txPool.EvictedPending += (_, e) => dropped = e.Transaction;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.EvictTransaction(transaction), Is.True);
                Assert.That(dropped, Is.SameAs(transaction), "the drop is surfaced to EvictedPending subscribers");
                Assert.That(_txPool.IsKnown(transaction.Hash), Is.False, "the long-term cache is cleared so the tx can re-enter");
            }
        }

        [Test]
        public void should_calculate_gasBottleneck_properly()
        {
            _txPool = CreatePool();
            Transaction[] transactions = new Transaction[5];

            Parallel.For(0, 5, i =>
            {
                transactions[i] = Build.A.Transaction
                    .WithSenderAddress(TestItem.AddressA)
                    .WithNonce(i)
                    .WithGasPrice((UInt256)(i + 2))
                    .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            });

            for (int i = 0; i < 5; i++)
            {
                EnsureSenderBalance(transactions[i]);
                _txPool.SubmitTx(transactions[i], TxHandlingOptions.PersistentBroadcast);
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.GetPendingTransactions().Min(static t => t.GasBottleneck), Is.EqualTo((UInt256)2));
                Assert.That(_txPool.GetPendingTransactions().Max(static t => t.GasBottleneck), Is.EqualTo((UInt256)2));
            }
        }

        [Test]
        public async Task should_remove_txs_with_old_nonces_when_updating_GasBottleneck()
        {
            _txPool = CreatePool();
            Transaction[] transactions = new Transaction[5];
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            Parallel.For(0, 5, i =>
            {
                transactions[i] = Build.A.Transaction
                    .WithSenderAddress(TestItem.AddressA)
                    .WithNonce(i)
                    .WithGasPrice((UInt256)(i + 2))
                    .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
                _txPool.SubmitTx(transactions[i], TxHandlingOptions.PersistentBroadcast);
            });
            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(5));

            for (int i = 0; i < 3; i++)
            {
                _stateProvider.IncrementNonce(TestItem.AddressA);
            }

            await RaiseBlockAddedToMainAndWaitForTransactions(5);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(2));
                Assert.That(_txPool.GetPendingTransactions().Count(static t => t.GasBottleneck == 0), Is.EqualTo(0));
                Assert.That(_txPool.GetPendingTransactions().Max(static t => t.GasBottleneck), Is.EqualTo((UInt256)5));
            }
        }

        [TestCase(false, 1, TestName = "should_broadcast_own_transactions")]
        [TestCase(true, 0, TestName = "should_not_broadcast_own_transactions_that_faded_out_and_came_back")]
        public void should_handle_own_transaction_broadcasting(bool removeAndResubmit, int expectedOwnCount)
        {
            _txPool = CreatePool();
            Transaction transaction = AddTransactionToPool();

            if (removeAndResubmit)
            {
                _txPool.RemoveTransaction(transaction.Hash);
                _txPool.RemoveTransaction(TestItem.KeccakA);
                _txPool.SubmitTx(transaction, TxHandlingOptions.None);
            }

            Assert.That(_txPool.GetOwnPendingTransactions().Length, Is.EqualTo(expectedOwnCount));
        }

        [TestCase(1, 0)]
        [TestCase(2, 0)]
        [TestCase(2, 1)]
        [TestCase(10, 0)]
        [TestCase(10, 1)]
        [TestCase(10, 5)]
        [TestCase(10, 8)]
        [TestCase(10, 9)]
        public async Task should_remove_stale_txs_from_persistent_transactions(int numberOfTxs, int nonceIncludedInBlock)
        {
            _txPool = CreatePool();

            Transaction[] transactions = new Transaction[numberOfTxs];
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            Parallel.For(0, numberOfTxs, i =>
            {
                transactions[i] = Build.A.Transaction
                    .WithNonce(i)
                    .WithGasLimit(GasCostOf.Transaction)
                    .WithGasPrice(10.GWei)
                    .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
                    .TestObject;
                _txPool.SubmitTx(transactions[i], TxHandlingOptions.PersistentBroadcast);
            });
            Assert.That(_txPool.GetOwnPendingTransactions().Length, Is.EqualTo(numberOfTxs));

            Block block = Build.A.Block.WithTransactions(transactions[nonceIncludedInBlock]).TestObject;
            await RaiseBlockAddedToMainAndWaitForNewHead(block);

            // transactions[nonceIncludedInBlock] was included in the block and should be removed, as well as all lower nonces.
            Assert.That(_txPool.GetOwnPendingTransactions().Length, Is.EqualTo(numberOfTxs - nonceIncludedInBlock - 1));
        }

        [Test]
        public async Task broadcaster_should_work_well_when_there_are_no_txs_in_persistent_txs_from_sender_of_tx_included_in_block()
        {
            _txPool = CreatePool();

            Transaction transactionA = Build.A.Transaction
                .WithNonce(0)
                .WithGasLimit(GasCostOf.Transaction)
                .WithGasPrice(10.GWei)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
                .TestObject;
            EnsureSenderBalance(transactionA);
            _txPool.SubmitTx(transactionA, TxHandlingOptions.None);

            Transaction transactionB = Build.A.Transaction
                .WithNonce(0)
                .WithGasLimit(GasCostOf.Transaction)
                .WithGasPrice(10.GWei)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyB)
                .TestObject;
            EnsureSenderBalance(transactionB);
            _txPool.SubmitTx(transactionB, TxHandlingOptions.PersistentBroadcast);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(2));
                Assert.That(_txPool.GetOwnPendingTransactions().Length, Is.EqualTo(1));
            }

            Block block = Build.A.Block.WithTransactions(transactionA).TestObject;
            await RaiseBlockAddedToMainAndWaitForNewHead(block);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(1));
                Assert.That(_txPool.GetOwnPendingTransactions().Length, Is.EqualTo(1));
            }
        }

        [Test]
        public async Task should_remove_transactions_concurrently()
        {
            int maxTryCount = 5;
            for (int i = 0; i < maxTryCount; ++i)
            {
                _txPool = CreatePool();
                int transactionsPerPeer = 5;
                Transaction[] transactions = AddTransactionsToPool(true, false, transactionsPerPeer);
                Transaction[] transactionsForFirstTask = transactions.Where(t => t.Nonce == 8).ToArray();
                Transaction[] transactionsForSecondTask = transactions.Where(t => t.Nonce == 6).ToArray();
                Transaction[] transactionsForThirdTask = transactions.Where(t => t.Nonce == 7).ToArray();
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(transactions, Has.Length.EqualTo(transactionsPerPeer * 10));
                    Assert.That(transactionsForFirstTask, Has.Length.EqualTo(transactionsPerPeer));
                }
                Task firstTask = Task.Run(() => DeleteTransactionsFromPool(transactionsForFirstTask));
                Task secondTask = Task.Run(() => DeleteTransactionsFromPool(transactionsForSecondTask));
                Task thirdTask = Task.Run(() => DeleteTransactionsFromPool(transactionsForThirdTask));
                await Task.WhenAll(firstTask, secondTask, thirdTask);
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(transactionsPerPeer * 7));
            }
        }

        [Test]
        public void should_add_transactions_concurrently()
        {
            int size = 3;
            TxPoolConfig config = new() { GasLimit = TxGasLimit, Size = size };
            _txPool = CreatePool(config);

            foreach (PrivateKey privateKey in TestItem.PrivateKeys)
            {
                EnsureSenderBalance(privateKey.Address, 10.Ether);
            }

            Parallel.ForEach(TestItem.PrivateKeys, k =>
            {
                for (uint i = 0; i < 100; i++)
                {
                    Transaction tx = GetTransaction(i, GasCostOf.Transaction, 10.GWei, TestItem.AddressA, [], k);
                    _txPool.SubmitTx(tx, TxHandlingOptions.None);
                }
            });

            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(size));
        }

        [TestCase(true, true, 10)]
        [TestCase(false, true, 100)]
        [TestCase(true, false, 100)]
        [TestCase(false, false, 100)]
        public void should_add_pending_transactions(bool sameTransactionSenderPerPeer, bool sameNoncePerPeer, int expectedTransactions)
        {
            _txPool = CreatePool();
            AddTransactionsToPool(sameTransactionSenderPerPeer, sameNoncePerPeer);
            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(expectedTransactions));
        }

        [TestCase(true, true, 10)]
        [TestCase(false, true, 100)]
        [TestCase(true, false, 100)]
        [TestCase(false, false, 100)]
        public async Task should_remove_tx_from_txPool_when_included_in_block(bool sameTransactionSenderPerPeer, bool sameNoncePerPeer, int expectedTransactions)
        {
            _txPool = CreatePool();

            AddTransactionsToPool(sameTransactionSenderPerPeer, sameNoncePerPeer);
            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(expectedTransactions));

            Transaction[] transactions = _txPool.GetPendingTransactions();
            Block block = Build.A.Block.WithTransactions(transactions).TestObject;
            await RaiseBlockAddedToMainAndWaitForNewHead(block);

            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(0));
        }

        [TestCase(true, true, 10)]
        [TestCase(false, true, 100)]
        [TestCase(true, false, 100)]
        [TestCase(false, false, 100)]
        public async Task should_not_remove_txHash_from_hashCache_when_tx_removed_because_of_including_in_block(bool sameTransactionSenderPerPeer, bool sameNoncePerPeer, int expectedTransactions)
        {
            _txPool = CreatePool();

            AddTransactionsToPool(sameTransactionSenderPerPeer, sameNoncePerPeer);
            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(expectedTransactions));

            Transaction[] transactions = _txPool.GetPendingTransactions();
            Block block = Build.A.Block.WithTransactions(transactions).TestObject;
            await RaiseBlockAddedToMainAndWaitForNewHead(block);

            foreach (Transaction transaction in transactions)
            {
                Assert.That(_txPool.IsKnown(transaction.Hash), Is.True);
            }
        }

        [Test]
        public void should_delete_pending_transactions()
        {
            _txPool = CreatePool();
            Transaction[] transactions = AddTransactionsToPool();
            DeleteTransactionsFromPool(transactions);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.GetPendingTransactions(), Is.Empty);
                Assert.That(_txPool.GetOwnPendingTransactions(), Is.Empty);
            }
        }

        [Test]
        public void should_return_ReplacementNotAllowed_when_trying_to_send_transaction_with_same_nonce_and_same_fee_for_same_address()
        {
            _txPool = CreatePool();
            AcceptTxResult result1 = _txPool.SubmitTx(GetTransaction(TestItem.PrivateKeyA, TestItem.AddressA), TxHandlingOptions.PersistentBroadcast | TxHandlingOptions.ManagedNonce);
            Assert.That(result1, Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(_txPool.GetOwnPendingTransactions().Length, Is.EqualTo(1));
            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(1));
            AcceptTxResult result2 = _txPool.SubmitTx(GetTransaction(TestItem.PrivateKeyA, TestItem.AddressB), TxHandlingOptions.PersistentBroadcast | TxHandlingOptions.ManagedNonce);
            Assert.That(result2, Is.EqualTo(AcceptTxResult.ReplacementNotAllowed));
            Assert.That(_txPool.GetOwnPendingTransactions().Length, Is.EqualTo(1));
            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(1));
        }

        [Test]
        public void should_retrieve_added_transaction_correctly()
        {
            Transaction transaction = Build.A.Transaction.SignedAndResolved().TestObject;
            EnsureSenderBalance(transaction);
            _specProvider = Substitute.For<ISpecProvider>();
            _specProvider.ChainId.Returns(transaction.Signature.ChainId.Value);
            _txPool = CreatePool();
            Assert.That(_txPool.SubmitTx(transaction, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(_txPool.TryGetPendingTransaction(transaction.Hash, out Transaction retrievedTransaction), Is.True);
            Assert.That(retrievedTransaction, Is.EqualTo(transaction));
        }

        [Test]
        public void should_not_retrieve_not_added_transaction()
        {
            Transaction transaction = Build.A.Transaction.SignedAndResolved().TestObject;
            _txPool = CreatePool();
            bool found = _txPool.TryGetPendingTransaction(transaction.Hash, out Transaction retrievedTransaction);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(found, Is.False);
                Assert.That(retrievedTransaction, Is.Null);
            }
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
            Assert.That(_txPool.SubmitTx(transaction, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(_txPool.TryGetPendingTransaction(transaction.Hash, out Transaction retrievedTransaction), Is.True);
            Assert.That(retrievedTransaction, Is.EqualTo(transaction));

            EnsureSenderBalance(transactionWithHigherFee);
            _txPool.ResetAddress(transactionWithHigherFee.SenderAddress);
            Assert.That(_txPool.SubmitTx(transactionWithHigherFee, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(_txPool.TryGetPendingTransaction(transactionWithHigherFee.Hash, out Transaction retrievedTransactionWithHigherFee), Is.True);
            Assert.That(retrievedTransactionWithHigherFee, Is.EqualTo(transactionWithHigherFee));

            // now transaction with lower fee should be evicted from pending txs and should still be present in persistentTxs
            Assert.That(_txPool.TryGetPendingTransaction(transaction.Hash, out Transaction retrievedTransactionWithLowerFee), Is.True);
            Assert.That(retrievedTransactionWithLowerFee, Is.EqualTo(transaction));
        }

        [Test]
        public void should_notify_added_peer_of_own_tx_when_we_are_synced([Values(0u, 1u)] uint headNumber)
        {
            _txPool = CreatePool();
            _ = AddTransactionToPool();
            ITxPoolPeer txPoolPeer = Substitute.For<ITxPoolPeer>();
            txPoolPeer.HeadNumber.Returns(headNumber);
            txPoolPeer.Id.Returns(TestItem.PublicKeyA);
            _txPool.AddPeer(txPoolPeer);
            txPoolPeer.Received((int)headNumber).SendNewTransactions(Arg.Any<IEnumerable<Transaction>>(), false);
        }

        [Test]
        public void should_notify_peer_only_once()
        {
            _txPool = CreatePool();
            ITxPoolPeer txPoolPeer = Substitute.For<ITxPoolPeer>();
            txPoolPeer.Id.Returns(TestItem.PublicKeyA);
            _txPool.AddPeer(txPoolPeer);
            _ = AddTransactionToPool();
            Assert.That(() => txPoolPeer.ReceivedCallsMatching(p => p.SendNewTransaction(Arg.Any<Transaction>())), Is.True.After(500, 10));
            txPoolPeer.DidNotReceive().SendNewTransactions(Arg.Any<IEnumerable<Transaction>>(), false);
        }

        [TestCase(true, TestName = "should_send_to_peers_full_newly_added_local_tx")]
        [TestCase(false, TestName = "should_not_send_to_peers_full_newly_added_external_tx")]
        public void should_handle_sending_newly_added_tx_to_peers(bool isLocal)
        {
            _txPool = CreatePool();
            ITxPoolPeer txPoolPeer = Substitute.For<ITxPoolPeer>();
            txPoolPeer.Id.Returns(TestItem.PublicKeyA);
            _txPool.AddPeer(txPoolPeer);
            Transaction tx = AddTransactionToPool(isLocal);

            if (isLocal)
            {
                txPoolPeer.Received().SendNewTransaction(tx);
            }
            else
            {
                txPoolPeer.DidNotReceive().SendNewTransaction(tx);
            }
        }

        [Test]
        public void should_accept_access_list_transactions_only_when_eip2930_enabled([Values(false, true)] bool eip2930Enabled)
        {
            if (!eip2930Enabled)
            {
                _blockTree.BestSuggestedHeader = Build.A.BlockHeader.WithNumber(MainnetSpecProvider.BerlinBlockNumber - 1).TestObject;
                Block block = Build.A.Block.WithNumber(MainnetSpecProvider.BerlinBlockNumber - 2).TestObject;
                _blockTree.Head = block;
            }

            _txPool = CreatePool(null, new TestSpecProvider(eip2930Enabled ? Berlin.Instance : Istanbul.Instance));
            Transaction tx = Build.A.Transaction
                .WithType(TxType.AccessList)
                .WithChainId(TestBlockchainIds.ChainId)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx);
            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(eip2930Enabled ? 1 : 0));
                Assert.That(result, Is.EqualTo(eip2930Enabled ? AcceptTxResult.Accepted : AcceptTxResult.Invalid));
            }
        }

        [Test]
        public void should_accept_only_when_synced([Values(false, true)] bool isSynced, [Values(false, true)] bool isLocal)
        {
            if (!isSynced)
            {
                _blockTree.BestSuggestedHeader = Build.A.BlockHeader.WithNumber(MainnetSpecProvider.BerlinBlockNumber - 1).TestObject;
                Block block = Build.A.Block.WithNumber(1).TestObject;
                _blockTree.Head = block;
            }

            _txPool = CreatePool(null, new TestSpecProvider(Berlin.Instance));
            Transaction tx = Build.A.Transaction
                .WithChainId(TestBlockchainIds.ChainId)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx);
            AcceptTxResult result = _txPool.SubmitTx(tx, isLocal ? TxHandlingOptions.PersistentBroadcast : TxHandlingOptions.None);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo((isSynced || isLocal) ? 1 : 0));
                Assert.That(result, Is.EqualTo((isSynced || isLocal) ? AcceptTxResult.Accepted : AcceptTxResult.Syncing));
            }
        }

        [Test]
        public void When_MaxFeePerGas_is_lower_than_MaxPriorityFeePerGas_tx_is_invalid()
        {
            _txPool = CreatePool();
            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
                .WithMaxPriorityFeePerGas(10.GWei)
                .WithMaxFeePerGas(5.GWei)
                .WithType(TxType.EIP1559)
                .TestObject;
            EnsureSenderBalance(tx);
            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(0));
                Assert.That(result, Is.EqualTo(AcceptTxResult.Invalid));
            }
        }

        [TestCase(0u, 1, TestName = "should_accept_zero_MaxFeePerGas_and_zero_MaxPriorityFee_1559_tx")]
        [TestCase(1u, 0, TestName = "should_reject_zero_MaxFeePerGas_and_positive_MaxPriorityFee_1559_tx")]
        public void should_handle_zero_MaxFeePerGas_1559_tx(uint maxPriorityFeePerGas, int expectedPending)
        {
            ISpecProvider specProvider = GetLondonSpecProvider();
            _txPool = CreatePool(null, specProvider);
            Transaction tx = Build.A.Transaction
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas(UInt256.Zero)
                .WithMaxPriorityFeePerGas((UInt256)maxPriorityFeePerGas)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);
            _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(expectedPending));
        }

        [TestCase(true, TestName = "should_return_true_when_asking_for_txHash_existing_in_pool")]
        [TestCase(false, TestName = "should_return_false_when_asking_for_not_known_txHash")]
        public void should_check_if_txHash_is_known(bool addTxFirst)
        {
            _txPool = CreatePool();
            if (addTxFirst)
            {
                Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
                EnsureSenderBalance(tx);
                _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
                Assert.That(_txPool.IsKnown(tx.Hash), Is.EqualTo(true));
                Assert.That(_txPool.RemoveTransaction(tx.Hash), Is.EqualTo(true));
            }
            else
            {
                Assert.That(_txPool.IsKnown(TestItem.KeccakA), Is.EqualTo(false));
                Transaction tx = Build.A.Transaction.WithHash(TestItem.KeccakA).TestObject;
                Assert.That(_txPool.RemoveTransaction(tx.Hash), Is.EqualTo(false));
            }
        }

        [Test]
        public void should_return_false_when_trying_to_remove_tx_with_null_txHash()
        {
            _txPool = CreatePool();
            Assert.That(_txPool.RemoveTransaction(null), Is.EqualTo(false));
        }

        [Test]
        public void should_refresh_pending_transactions_snapshot_after_removing_transaction()
        {
            _txPool = CreatePool();
            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx);

            Assert.That(_txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(_txPool.GetPendingTransactions(), Has.One.Matches<Transaction>(transaction => transaction.Hash == tx.Hash));
            Assert.That(_txPool.RemoveTransaction(tx.Hash), Is.True);

            Transaction[] snapshot = _txPool.GetPendingTransactions();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(snapshot, Is.Empty);
                Assert.That(snapshot.Length, Is.EqualTo(_txPool.GetPendingTransactionsCount()));
            }
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

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(1));
                Assert.That(_txPool.GetPendingTransactions().First(), Is.EqualTo(replaced ? newTx : oldTx));
            }
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

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(1));
                Assert.That(_txPool.GetPendingTransactions().First(), Is.EqualTo(replaced ? newTx : oldTx));
            }
        }

        [TestCase(TxType.Legacy, 0)]
        [TestCase(TxType.Legacy, 1)]
        [TestCase(TxType.Legacy, 1000000)]
        [TestCase(TxType.EIP1559, 0)]
        [TestCase(TxType.EIP1559, 1)]
        [TestCase(TxType.EIP1559, 1000000)]
        public void should_always_replace_zero_fee_tx(TxType txType, int newFee)
        {
            ISpecProvider specProvider = GetLondonSpecProvider();
            _txPool = CreatePool(null, specProvider);

            TransactionBuilder<Transaction> oldBuilder = Build.A.Transaction
                .WithNonce(0)
                .WithType(txType)
                .WithTo(TestItem.AddressB);
            TransactionBuilder<Transaction> newBuilder = Build.A.Transaction
                .WithNonce(0)
                .WithType(txType)
                .WithTo(TestItem.AddressC);

            if (txType == TxType.EIP1559)
            {
                oldBuilder.WithMaxFeePerGas(UInt256.Zero).WithMaxPriorityFeePerGas(UInt256.Zero);
                newBuilder.WithMaxFeePerGas((UInt256)newFee).WithMaxPriorityFeePerGas(UInt256.Zero);
            }
            else
            {
                oldBuilder.WithGasPrice(UInt256.Zero);
                newBuilder.WithGasPrice((UInt256)newFee);
            }

            Transaction oldTx = oldBuilder.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            Transaction newTx = newBuilder.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            _txPool.SubmitTx(oldTx, TxHandlingOptions.PersistentBroadcast);
            _txPool.SubmitTx(newTx, TxHandlingOptions.PersistentBroadcast);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(1));
                Assert.That(_txPool.GetPendingTransactions().First(), Is.EqualTo(newTx));
            }
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

            Assert.That(_txPool.SubmitTx(zeroCostTx, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Accepted));

            // Cumulative cost should be 1
            Transaction expensiveTx = Build.A.Transaction
                .WithNonce(1)
                .WithValue(1)
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas(0)
                .WithMaxPriorityFeePerGas(0)
                .WithTo(TestItem.AddressB)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyF).TestObject;
            Assert.That(_txPool.SubmitTx(expensiveTx, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Accepted));
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

            Assert.That(_txPool.SubmitTx(firstTx, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(_txPool.SubmitTx(secondTx, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.GetPendingTransactions(), Does.Contain(firstTx));
                Assert.That(_txPool.GetPendingTransactions(), Does.Contain(secondTx));
                Assert.That(_txPool.GetOwnPendingTransactions(), Does.Not.Contain(firstTx));
                Assert.That(_txPool.GetOwnPendingTransactions(), Does.Not.Contain(secondTx));
            }

            // Send cheap transaction => Not included in txPool
            Transaction cheapTx = Build.A.Transaction
                .WithNonce(2)
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas(1)
                .WithMaxPriorityFeePerGas(1)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            Assert.That(_txPool.SubmitTx(cheapTx, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Accepted));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.GetPendingTransactions(), Does.Not.Contain(cheapTx));
                Assert.That(_txPool.GetOwnPendingTransactions(), Does.Contain(cheapTx));
            }
            peer.Received().SendNewTransaction(cheapTx);

            // Send transaction with increased nonce => NonceGap should not appear as previous transaction is broadcasted, should be accepted
            Transaction fourthTx = Build.A.Transaction
                .WithNonce(3)
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas(1)
                .WithMaxPriorityFeePerGas(1)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            Assert.That(_txPool.SubmitTx(fourthTx, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Accepted));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.GetPendingTransactions(), Does.Not.Contain(fourthTx));
                Assert.That(_txPool.GetOwnPendingTransactions(), Does.Contain(fourthTx));
            }
            peer.Received().SendNewTransaction(fourthTx);
        }

        [Test]
        [NonParallelizable]
        public void should_include_transaction_after_removal()
        {
            ISpecProvider specProvider = GetLondonSpecProvider();
            _txPool = CreatePool(new TxPoolConfig { Size = 2 }, specProvider);

            Transaction txA = Build.A.Transaction
                .WithNonce(0)
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas(1)
                .WithMaxPriorityFeePerGas(1)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyB).TestObject;
            EnsureSenderBalance(TestItem.AddressB, UInt256.MaxValue);

            Assert.That(_txPool.SubmitTx(txA, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));

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

            Assert.That(_txPool.SubmitTx(expensiveTx1, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(_txPool.SubmitTx(expensiveTx2, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));

            ManualResetEventSlim headProcessed = new();
            _txPool.TxPoolHeadChanged += (_, _) => headProcessed.Set();
            _blockTree.RaiseBlockAddedToMain(new BlockReplacementEventArgs(Build.A.Block.WithTransactions(expensiveTx1).TestObject));
            Assert.That(headProcessed.Wait(TimeSpan.FromSeconds(30)), Is.True, "Pool did not finish processing head change");

            Assert.That(_txPool.SubmitTx(txA, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
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
                Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
            }
            else
            {
                Assert.That(result, Is.Not.EqualTo(AcceptTxResult.Accepted));
            }
        }

        [Test]
        public void Should_not_replace_better_txs_by_worse_ones()
        {
            TxPoolConfig txPoolConfig = new() { Size = 128 };
            _txPool = CreatePool(txPoolConfig);

            using ArrayPoolList<Transaction> transactions = new(txPoolConfig.Size, txPoolConfig.Size);
            // send (size - 1) standard txs from different senders
            Parallel.For(0, txPoolConfig.Size, i =>
            {
                transactions[i] = Build.A.Transaction
                    .WithNonce(0)
                    .WithValue(0)
                    .WithGasPrice(10)
                    .WithTo(TestItem.AddressB)
                    .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeys[i]).TestObject;
            });

            for (int i = 0; i < txPoolConfig.Size - 1; i++)
            {
                Transaction tx = transactions[i];
                EnsureSenderBalance(TestItem.PrivateKeys[i].Address, UInt256.MaxValue);
                AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);

                Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(txPoolConfig.Size - 1));
                Assert.That(_txPool.GetPendingTransactionsBySender().Keys.Count, Is.EqualTo(txPoolConfig.Size - 1));
            }

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

            using (Assert.EnterMultipleScope())
            {
                Assert.That(cheapTxResult, Is.EqualTo(AcceptTxResult.Accepted));
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(txPoolConfig.Size));
                Assert.That(_txPool.GetPendingTransactionsBySender().Keys.Count, Is.EqualTo(txPoolConfig.Size));
            }

            using ArrayPoolList<Transaction> txs = new(txPoolConfig.Size, txPoolConfig.Size);
            // send (size - 1) standard txs from different senders
            Parallel.For(0, txPoolConfig.Size, i =>
            {
                txs[i] = Build.A.Transaction
                    .WithNonce(i + 1)
                    .WithValue(0)
                    .WithGasPrice(1000)
                    .WithTo(TestItem.AddressB)
                    .SignedAndResolved(_ethereumEcdsa, privateKeyOfAttacker).TestObject;
            });

            // send (size - 1) expensive txs from sender X
            for (int i = 0; i < txPoolConfig.Size - 1; i++)
            {
                Transaction tx = txs[i];

                AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
                Assert.That(result, Is.EqualTo(AcceptTxResult.FeeTooLowToCompete));

                // newly coming txs should evict themselves
                Assert.That(_txPool.GetPendingTransactionsBySender().Keys.Count, Is.EqualTo(txPoolConfig.Size));
            }

            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(txPoolConfig.Size));
        }

        [Test]
        public void Should_not_replace_ready_txs_by_nonce_gap_ones()
        {
            TxPoolConfig txPoolConfig = new() { Size = 128 };
            _txPool = CreatePool(txPoolConfig);

            using ArrayPoolList<Transaction> txs = new(txPoolConfig.Size, txPoolConfig.Size);
            // send (size - 1) standard txs from different senders
            Parallel.For(0, txPoolConfig.Size, i =>
            {
                txs[i] = Build.A.Transaction
                    .WithNonce(0)
                    .WithGasPrice(10)
                    .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeys[i]).TestObject;
            });

            // send (size - 1) standard txs from different senders
            for (int i = 0; i < txPoolConfig.Size - 1; i++)
            {
                Transaction tx = txs[i];

                EnsureSenderBalance(TestItem.PrivateKeys[i].Address, UInt256.MaxValue);
                Assert.That(_txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Accepted));
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(txPoolConfig.Size - 1));
                Assert.That(_txPool.GetPendingTransactionsBySender().Keys.Count, Is.EqualTo(txPoolConfig.Size - 1));
            }

            const int nonceGap = 100;
            // send 1 expensive nonce-gap tx from sender X
            PrivateKey privateKeyOfAttacker = TestItem.PrivateKeys[txPoolConfig.Size];
            Transaction nonceGapTx = Build.A.Transaction
                .WithNonce(nonceGap)
                .WithGasPrice(1000)
                .SignedAndResolved(_ethereumEcdsa, privateKeyOfAttacker).TestObject;

            EnsureSenderBalance(privateKeyOfAttacker.Address, UInt256.MaxValue);
            Assert.That(_txPool.SubmitTx(nonceGapTx, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Accepted));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(txPoolConfig.Size));
                Assert.That(_txPool.GetPendingTransactionsBySender().Keys.Count, Is.EqualTo(txPoolConfig.Size));
            }

            using ArrayPoolList<Transaction> txs2 = new(txPoolConfig.Size, txPoolConfig.Size);
            Parallel.For(0, txPoolConfig.Size, i =>
            {
                txs2[i] = Build.A.Transaction
                    .WithNonce(i + 1 + nonceGap)
                    .WithGasPrice(1000)
                    .SignedAndResolved(_ethereumEcdsa, privateKeyOfAttacker).TestObject;
            });
            // send (size - 1) expensive txs from sender X with consecutive nonces
            for (int i = 0; i < txPoolConfig.Size - 1; i++)
            {
                Transaction tx = txs2[i];
                Assert.That(_txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.FeeTooLowToCompete));

                // newly coming txs should evict themselves
                Assert.That(_txPool.GetPendingTransactionsBySender().Keys.Count, Is.EqualTo(txPoolConfig.Size));
            }

            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(txPoolConfig.Size));
        }

        [TestCase(9, false)]
        [TestCase(11, true)]
        public void Should_not_add_underpaid_tx_even_if_lower_nonces_are_expensive(int gasPrice, bool expectedResult)
        {
            TxPoolConfig txPoolConfig = new() { Size = 128 };
            _txPool = CreatePool(txPoolConfig);

            using ArrayPoolList<Transaction> txs = new(txPoolConfig.Size, txPoolConfig.Size);
            Parallel.For(1, txPoolConfig.Size, i =>
            {
                txs[i] = Build.A.Transaction
                    .WithNonce(0)
                    .WithValue(0)
                    .WithGasPrice(10)
                    .WithTo(TestItem.AddressB)
                    .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeys[i]).TestObject;
            });

            // send standard txs from different senders
            for (int i = 1; i < txPoolConfig.Size; i++)
            {
                Transaction tx = txs[i];
                EnsureSenderBalance(TestItem.PrivateKeys[i].Address, UInt256.MaxValue);
                Assert.That(_txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Accepted));
            }
            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(txPoolConfig.Size - 1));

            // send first tx from sender X - expensive
            Transaction firstTx = Build.A.Transaction
                .WithNonce(0)
                .WithValue(0)
                .WithGasPrice(11)
                .WithTo(TestItem.AddressB)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeys[0]).TestObject;

            EnsureSenderBalance(TestItem.PrivateKeys[0].Address, UInt256.MaxValue);
            Assert.That(_txPool.SubmitTx(firstTx, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(txPoolConfig.Size));

            // sender X is sending another tx with different gasprice
            Transaction secondTx = Build.A.Transaction
                .WithNonce(1)
                .WithValue(0)
                .WithGasPrice((UInt256)gasPrice)
                .WithTo(TestItem.AddressB)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeys[0]).TestObject;

            AcceptTxResult result = _txPool.SubmitTx(secondTx, TxHandlingOptions.PersistentBroadcast);

            Assert.That(result, Is.EqualTo(expectedResult ? AcceptTxResult.Accepted : AcceptTxResult.FeeTooLowToCompete));
        }

        [Test]
        public void Should_correctly_add_tx_to_local_pool_when_underpaid([Values] TxType txType)
        {
            // Should only add non-blob transactions to local pool when underpaid
            bool expectedResult = txType != TxType.Blob;

            // No need to check for deposit tx
            if (txType == TxType.DepositTx) return;

            // Frame txs are rejected at ingress under Prague; EIP-8141 activates at Bogota
            if (txType == TxType.FrameTx) return;

            ISpecProvider specProvider = GetPragueSpecProvider();
            TxPoolConfig txPoolConfig = new() { Size = 30, PersistentBlobStorageSize = 0 };
            _txPool = CreatePool(txPoolConfig, specProvider);

            Transaction[] transactions = GetTransactions(GetPeers(3), true, false);

            foreach (Address address in transactions.Select(static t => t.SenderAddress).Distinct())
            {
                EnsureSenderBalance(address, UInt256.MaxValue);
            }

            // setup full tx pool
            foreach (Transaction transaction in transactions)
            {
                transaction.GasPrice = 10.GWei;
                _txPool.SubmitTx(transaction, TxHandlingOptions.None);
            }

            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(30));

            Transaction testTx = Build.A.Transaction
                .WithNonce(0)
                .WithType(txType)
                .WithShardBlobTxTypeAndFieldsIfBlobTx()
                .WithAuthorizationCodeIfAuthorizationListTx()
                .WithMaxFeePerGas(9.GWei)
                .WithMaxPriorityFeePerGas(9.GWei)
                .WithGasLimit(txType != TxType.SetCode ? GasCostOf.Transaction : GasCostOf.Transaction + GasCostOf.NewAccount)
                .WithTo(TestItem.AddressB)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

            EnsureSenderBalance(TestItem.PrivateKeyA.Address, UInt256.MaxValue);

            AcceptTxResult result = _txPool.SubmitTx(testTx, TxHandlingOptions.PersistentBroadcast);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result, Is.EqualTo(expectedResult ? AcceptTxResult.Accepted : AcceptTxResult.FeeTooLowToCompete));
                Assert.That(_txPool.GetOwnPendingTransactions().Length, Is.EqualTo(expectedResult ? 1 : 0));
                Assert.That(_txPool.GetPendingBlobTransactionsCount(), Is.EqualTo(0));
                Assert.That(_txPool.GetPendingTransactions(), Does.Not.Contain(testTx));
            }
        }

        [Test]
        public void SubmitTx_FrameTransaction_RejectedAtIngressAsNotSupported()
        {
            _txPool = CreatePool();
            Transaction frameTx = Build.A.Transaction
                .WithType(TxType.FrameTx)
                .WithNonce(0)
                .WithMaxFeePerGas(1.GWei)
                .WithMaxPriorityFeePerGas(1.GWei)
                .WithGasLimit(100_000)
                .WithTo(TestItem.AddressB)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(TestItem.PrivateKeyA.Address, UInt256.MaxValue);

            AcceptTxResult result = _txPool.SubmitTx(frameTx, TxHandlingOptions.PersistentBroadcast);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result, Is.EqualTo(AcceptTxResult.NotSupportedTxType), "frame transactions must be rejected at pool ingress");
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(0), "no frame transaction may enter the pool");
            }
        }

        [Test]
        public void SubmitTx_FrameTransaction_AcceptedWhenEip8141Active()
        {
            // MAX_VERIFY_GAS disabled: this covers payer resolution, not the verify-gas bound.
            _txPool = CreatePool(new TxPoolConfig { FrameTxMaxVerifyGas = 0 }, new TestSpecProvider(Eip8141Prototype.Instance));
            // A default-code self_verify frame tx: the sender is its own payer, resolved natively.
            Transaction frameTx = new()
            {
                Type = TxType.FrameTx,
                ChainId = _specProvider.ChainId,
                Nonce = 0,
                SenderAddress = TestItem.PrivateKeyA.Address,
                Frames = [new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: 100_000, UInt256.Zero, Array.Empty<byte>())],
                FrameSignatures = [],
                GasLimit = 1_000_000,
                GasPrice = 1.GWei,
                DecodedMaxFeePerGas = 1.GWei,
            };
            frameTx.FrameSignatures = [FrameSignature(frameTx, FrameSignatureDefect.None)];
            frameTx.Hash = frameTx.CalculateHash();
            EnsureSenderBalance(TestItem.PrivateKeyA.Address, UInt256.MaxValue);

            AcceptTxResult result = _txPool.SubmitTx(frameTx, TxHandlingOptions.PersistentBroadcast);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted), "frame transactions must enter the pool once the EIP-8141 fork is active");
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(1), "the frame transaction must be pending");
                Assert.That(frameTx.PayerAddress, Is.EqualTo(TestItem.PrivateKeyA.Address), "the self_verify payer must be resolved to the sender");
            }
        }

        // Both filters are wired into the pool, and the placement filter runs ahead of the one that would
        // otherwise claim the same layout — deleting either line leaves every filter fixture green.
        [Test]
        public void SubmitTx_FrameTransactionWithAVerifyFrameBehindThePrefix_IsRejected()
        {
            _txPool = CreatePool(new TxPoolConfig { FrameTxMaxVerifyGas = 0 }, new TestSpecProvider(Eip8141Prototype.Instance));
            Transaction frameTx = SelfVerifyFrameTx(
                new TxFrame(TxFrame.ModeSender, TxFrame.ApproveScopeNone, TestItem.AddressB, gasLimit: 1_000, UInt256.Zero, Array.Empty<byte>()),
                new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecution, target: null, gasLimit: 1_000, UInt256.Zero, Array.Empty<byte>()));

            Assert.That(_txPool.SubmitTx(frameTx, TxHandlingOptions.PersistentBroadcast),
                Is.EqualTo(AcceptTxResult.FrameTxVerifyAfterPrefix));
        }

        [Test]
        public void SubmitTx_FrameTransactionWithAMisplacedExpiryFrame_IsRejectedOnItsPlacement()
        {
            _txPool = CreatePool(new TxPoolConfig { FrameTxMaxVerifyGas = 0 }, new TestSpecProvider(Eip8141Prototype.Instance));
            Transaction frameTx = SelfVerifyFrameTx(FrameTxTestFrames.ExpiryAt(deadline: 1_000));

            // The placement verdict, not the one the trailing VERIFY frame would otherwise earn.
            Assert.That(_txPool.SubmitTx(frameTx, TxHandlingOptions.PersistentBroadcast),
                Is.EqualTo(AcceptTxResult.FrameTxMisplacedExpiryFrame));
        }

        private Transaction SelfVerifyFrameTx(params TxFrame[] trailingFrames)
        {
            Transaction frameTx = new()
            {
                Type = TxType.FrameTx,
                ChainId = _specProvider.ChainId,
                Nonce = 0,
                SenderAddress = TestItem.PrivateKeyA.Address,
                Frames =
                [
                    new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: 100_000, UInt256.Zero, Array.Empty<byte>()),
                    .. trailingFrames,
                ],
                FrameSignatures = [],
                GasLimit = 1_000_000,
                GasPrice = 1.GWei,
                DecodedMaxFeePerGas = 1.GWei,
            };
            frameTx.FrameSignatures = [FrameSignature(frameTx, FrameSignatureDefect.None)];
            frameTx.Hash = frameTx.CalculateHash();
            EnsureSenderBalance(TestItem.PrivateKeyA.Address, UInt256.MaxValue);
            return frameTx;
        }

        [TestCase(100_000UL, 0UL, 0, true)]
        [TestCase(118_000UL, 0UL, 0, false)]
        [TestCase(10_000UL, 0UL, 4000, false)]
        [TestCase(70_000UL, 70_000UL, 0, true)]
        public void SubmitTx_FrameTransaction_IsGatedOnBlockDimensions(
            ulong executionGasLimit,
            ulong stateGasLimit,
            int frameDataLength,
            bool expectedAccepted)
        {
            _txPool = CreatePool(null, new TestSpecProvider(Eip8141Prototype.Instance));
            _headInfo.BlockGasLimit = 130_000;

            byte[] frameData = Enumerable.Repeat((byte)1, frameDataLength).ToArray();
            Transaction frameTx = new()
            {
                Type = TxType.FrameTx,
                ChainId = _specProvider.ChainId,
                Nonce = 0,
                SenderAddress = TestItem.PrivateKeyA.Address,
                Frames = [new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, executionGasLimit, stateGasLimit, UInt256.Zero, frameData)],
                FrameSignatures = [],
                GasLimit = executionGasLimit + stateGasLimit,
                GasPrice = 1.GWei,
                DecodedMaxFeePerGas = 1.GWei,
            };
            frameTx.Hash = frameTx.CalculateHash();
            EnsureSenderBalance(TestItem.PrivateKeyA.Address, UInt256.MaxValue);

            AcceptTxResult result = _txPool.SubmitTx(frameTx, TxHandlingOptions.PersistentBroadcast);

            Assert.That(result, expectedAccepted
                ? Is.EqualTo(AcceptTxResult.Accepted)
                : Is.EqualTo(AcceptTxResult.GasLimitExceeded));
        }

        // EIP-8141: expired frame txs must be evicted on the new head; deadline == timestamp is still valid
        // (the predeploy reverts only on strictly greater-than).
        [TestCase(1_000UL, 1_500UL, 0, TestName = "deadline in the past is dropped")]
        [TestCase(2_000UL, 1_500UL, 1, TestName = "deadline in the future is retained")]
        [TestCase(1_500UL, 1_500UL, 1, TestName = "deadline equal to head timestamp is retained")]
        public async Task Expired_frame_transaction_is_dropped_on_new_head(ulong deadline, ulong headTimestamp, int expectedPending)
        {
            _txPool = CreatePool(null, new TestSpecProvider(Eip8141Prototype.Instance));
            Transaction frameTx = BuildFrameTx(nonce: 0, TestItem.PrivateKeyA.Address, deadline);
            EnsureSenderBalance(TestItem.PrivateKeyA.Address, UInt256.MaxValue);

            AcceptTxResult result = _txPool.SubmitTx(frameTx, TxHandlingOptions.PersistentBroadcast);
            Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted), "the frame transaction must first enter the pool");
            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(1));

            await RaiseBlockAddedToMainAndWaitForNewHead(Build.A.Block.WithNumber(1).WithTimestamp(headTimestamp).TestObject);

            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(expectedPending),
                "expired frame transactions must be evicted on the new head, unexpired ones retained");
        }

        // The on-head expiry sweep is a removal path like any other: a reservation outliving the transaction
        // locks the payer out of the pool until restart.
        [Test]
        public async Task Expired_frame_transaction_releases_its_payer_exposure_on_eviction()
        {
            _txPool = CreatePool(new TxPoolConfig { FrameTxMaxVerifyGas = 200_000 }, new TestSpecProvider(Eip8141Prototype.Instance));

            Transaction SignedFrameTx(ulong deadline)
            {
                Transaction tx = BuildFrameTx(nonce: 0, TestItem.PrivateKeyA.Address, deadline);
                tx.FrameSignatures = [FrameSignature(tx, FrameSignatureDefect.None)];
                tx.Hash = tx.CalculateHash();
                return tx;
            }

            await AssertExpiredFrameTxReleasesItsPayerExposure(SignedFrameTx, TxHandlingOptions.PersistentBroadcast);
        }

        // A head under the deadline first, so the DEBUG bookkeeping check meets a live reservation rather than an
        // empty ledger; then one past it, and a resubmission only a leaked reservation would reject.
        private async Task AssertExpiredFrameTxReleasesItsPayerExposure(Func<ulong, Transaction> signedFrameTx, TxHandlingOptions options)
        {
            Transaction first = signedFrameTx(1_000);
            int Pending() => first.CarriesBlobs ? _txPool.GetPendingBlobTransactionsCount() : _txPool.GetPendingTransactionsCount();

            // Balance for exactly one such transaction, so a reservation outliving the first rejects the second.
            UInt256 blobCost = (UInt256)(Eip4844Constants.GasPerBlob * (ulong)(first.BlobVersionedHashes?.Length ?? 0))
                * (first.MaxFeePerBlobGas ?? UInt256.Zero);
            EnsureSenderBalance(TestItem.PrivateKeyA.Address, (UInt256)first.GasLimit * first.MaxFeePerGas + blobCost);

            Assert.That(_txPool.SubmitTx(first, options), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(first.PayerAddress, Is.EqualTo(TestItem.PrivateKeyA.Address), "no reservation is taken unless the payer resolves");

            await RaiseBlockAddedToMainAndWaitForNewHead(Build.A.Block.WithNumber(1).WithTimestamp(500).TestObject);
            Assert.That(Pending(), Is.EqualTo(1), "a deadline ahead of the head must not be swept");

            await RaiseBlockAddedToMainAndWaitForNewHead(Build.A.Block.WithNumber(2).WithTimestamp(1_500).TestObject);
            Assert.That(Pending(), Is.EqualTo(0), "the expired frame transaction must be evicted");

            // Same payer and same cost, told apart only by its deadline: only a leaked reservation rejects it.
            Assert.That(_txPool.SubmitTx(signedFrameTx(2_000), options), Is.EqualTo(AcceptTxResult.Accepted));
        }

        // No expiry frame means no deadline, so the expiry pass (and the count guard that gates it) must never
        // evict it, whatever the head timestamp.
        [Test]
        public async Task Frame_transaction_without_expiry_frame_survives_new_head()
        {
            _txPool = CreatePool(null, new TestSpecProvider(Eip8141Prototype.Instance));
            Transaction frameTx = BuildFrameTx(nonce: 0, TestItem.PrivateKeyA.Address, deadline: null);
            EnsureSenderBalance(TestItem.PrivateKeyA.Address, UInt256.MaxValue);

            AcceptTxResult result = _txPool.SubmitTx(frameTx, TxHandlingOptions.PersistentBroadcast);
            Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted), "the frame transaction must first enter the pool");
            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(1));

            await RaiseBlockAddedToMainAndWaitForNewHead(Build.A.Block.WithNumber(1).WithTimestamp(ulong.MaxValue).TestObject);

            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(1),
                "a frame transaction without an expiry frame has no deadline and must never be evicted by the expiry pass");
        }

        // Fast path: a non-frame tx is never counted or evicted by the expiry pass, even with the fork active
        // and an extreme head timestamp.
        [Test]
        public async Task Regular_transaction_survives_expiry_pass_when_fork_active()
        {
            _txPool = CreatePool(null, new TestSpecProvider(Eip8141Prototype.Instance));
            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx);

            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(1));

            await RaiseBlockAddedToMainAndWaitForNewHead(Build.A.Block.WithNumber(1).WithTimestamp(ulong.MaxValue).TestObject);

            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(1),
                "the expiry pass must only ever touch frame transactions carrying a deadline");
        }

        // Replacing A with B fires Removed(A) and Inserted(B) in one InsertCore call; if those netted
        // _expiringFrameTxCount to zero the expiry pass would be skipped and B would outlive its deadline.
        [Test]
        public async Task Replaced_expiring_frame_transaction_is_still_evicted_on_new_head()
        {
            _txPool = CreatePool(null, new TestSpecProvider(Eip8141Prototype.Instance));
            EnsureSenderBalance(TestItem.PrivateKeyA.Address, UInt256.MaxValue);

            Transaction a = BuildFrameTx(nonce: 0, TestItem.PrivateKeyA.Address, deadline: 1_000);
            Assert.That(_txPool.SubmitTx(a, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Accepted),
                "the original expiring frame transaction must first enter the pool");

            // Same sender + nonce + deadline, both fees bumped well past the 10% replacement threshold.
            Transaction b = BuildFrameTx(nonce: 0, TestItem.PrivateKeyA.Address, deadline: 1_000, maxPriorityFeePerGas: 2.GWei, maxFeePerGas: 2.GWei);
            Assert.That(_txPool.SubmitTx(b, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Accepted),
                "the fee-bumped replacement must be accepted");
            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(1), "the replacement must displace the original");

            await RaiseBlockAddedToMainAndWaitForNewHead(Build.A.Block.WithNumber(1).WithTimestamp(1_500).TestObject);

            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(0),
                "the replacement inherits the deadline and must still be evicted by the expiry pass");
        }

        // EIP-8141: a deadline already behind the head is rejected at submit, mirroring the on-head eviction
        // predicate; deadline == head timestamp is the boundary the expiry verifier still accepts (strict >).
        [TestCase(1_000UL, 1_500UL, false, TestName = "already-expired frame tx is rejected at ingress")]
        [TestCase(2_000UL, 1_500UL, true, TestName = "not-yet-expired frame tx is accepted at ingress")]
        [TestCase(1_500UL, 1_500UL, true, TestName = "boundary deadline equal to head timestamp is accepted at ingress")]
        public async Task Expired_frame_transaction_is_rejected_at_ingress(ulong deadline, ulong headTimestamp, bool expectedAccepted)
        {
            _txPool = CreatePool(null, new TestSpecProvider(Eip8141Prototype.Instance));
            EnsureSenderBalance(TestItem.PrivateKeyA.Address, UInt256.MaxValue);
            ITxPoolPeer peer = Substitute.For<ITxPoolPeer>();
            peer.Id.Returns(TestItem.PublicKeyA);
            _txPool.AddPeer(peer);

            // Advance the head so the ingress filter has a current timestamp to compare the deadline against.
            await RaiseBlockAddedToMainAndWaitForNewHead(Build.A.Block.WithNumber(1).WithTimestamp(headTimestamp).TestObject);

            Transaction frameTx = BuildFrameTx(nonce: 0, TestItem.PrivateKeyA.Address, deadline);
            AcceptTxResult result = _txPool.SubmitTx(frameTx, TxHandlingOptions.PersistentBroadcast);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result, Is.EqualTo(expectedAccepted ? AcceptTxResult.Accepted : AcceptTxResult.FrameTxExpired));
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(expectedAccepted ? 1 : 0),
                    "an already-expired frame transaction must not enter the pool");
            }

            if (expectedAccepted)
            {
                peer.Received().SendNewTransaction(frameTx);
            }
            else
            {
                peer.DidNotReceive().SendNewTransaction(frameTx);
            }
        }

        [Test]
        public void Frame_transaction_from_a_contract_sender_is_not_rejected_by_eip3607()
        {
            _txPool = CreatePool(null, new TestSpecProvider(Eip8141Prototype.Instance));
            EnsureSenderBalance(TestItem.PrivateKeyA.Address, UInt256.MaxValue);
            // A smart-account sender is the normal case for a frame transaction: its code runs in the
            // validation prefix and authorises the transaction there.
            _stateProvider.InsertCode(TestItem.AddressA, "A"u8.ToArray(), Eip8141Prototype.Instance);

            Transaction frameTx = BuildFrameTx(nonce: 0, TestItem.AddressA, deadline: null);
            AcceptTxResult result = _txPool.SubmitTx(frameTx, TxHandlingOptions.PersistentBroadcast);

            // A legacy transaction from the same sender under the same spec pins that the exemption is
            // by transaction type, not a disabled filter.
            Transaction legacyTx = Build.A.Transaction
                .WithGasLimit(TxGasLimit)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            AcceptTxResult legacyResult = _txPool.SubmitTx(legacyTx, TxHandlingOptions.PersistentBroadcast);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(1));
                Assert.That(legacyResult, Is.EqualTo(AcceptTxResult.SenderIsContract));
            }
        }

        // A protocol-validated signature is invalid for every future chain state, so pooling one and
        // gossiping it costs peers work they must repeat and can only end in a peer-side rejection.
        [TestCase(FrameSignatureDefect.None, true)]
        [TestCase(FrameSignatureDefect.HighS, false)]
        [TestCase(FrameSignatureDefect.LegacyRecoveryId, false)]
        [TestCase(FrameSignatureDefect.ForeignSigner, false)]
        public void Frame_transaction_signatures_are_verified_at_ingress(FrameSignatureDefect defect, bool expectedAccepted)
        {
            _txPool = CreatePool(null, new TestSpecProvider(Eip8141Prototype.Instance));
            EnsureSenderBalance(TestItem.PrivateKeyA.Address, UInt256.MaxValue);

            Transaction frameTx = BuildFrameTx(nonce: 0, TestItem.PrivateKeyA.Address, deadline: null);
            frameTx.FrameSignatures = [FrameSignature(frameTx, defect)];
            frameTx.Hash = frameTx.CalculateHash();

            AcceptTxResult result = _txPool.SubmitTx(frameTx, TxHandlingOptions.PersistentBroadcast);

            Assert.That(result == AcceptTxResult.Accepted, Is.EqualTo(expectedAccepted), result.ToString());
            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(expectedAccepted ? 1 : 0));
        }

        // The EVM resolves P256VERIFY through the code-info repository, so gating it on a fork flag here would
        // refuse — and disconnect the peer over — a transaction the processor accepts.
        [TestCase(true, false, true, TestName = "P256VERIFY reached through EIP-7951")]
        [TestCase(false, true, true, TestName = "P256VERIFY reached through RIP-7212")]
        [TestCase(false, false, false, TestName = "P256VERIFY absent from the active precompiles")]
        public void Frame_transaction_with_a_valid_p256_signature_is_pooled(bool eip7951, bool rip7212, bool expectedAccepted)
        {
            OverridableReleaseSpec spec = new(Eip8141Prototype.Instance) { IsEip7951Enabled = eip7951, IsRip7212Enabled = rip7212 };
            _txPool = CreatePool(null, new TestSpecProvider(spec));
            EnsureSenderBalance(TestItem.PrivateKeyA.Address, UInt256.MaxValue);

            Transaction frameTx = BuildFrameTx(nonce: 0, TestItem.PrivateKeyA.Address, deadline: null);
            frameTx.FrameSignatures = [P256Signature(frameTx)];
            frameTx.Hash = frameTx.CalculateHash();

            AcceptTxResult result = _txPool.SubmitTx(frameTx, TxHandlingOptions.PersistentBroadcast);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result == AcceptTxResult.Accepted, Is.EqualTo(expectedAccepted), result.ToString());
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(expectedAccepted ? 1 : 0));
            }
        }

        private static TxFrameSignature P256Signature(Transaction tx)
        {
            using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            ECPoint q = key.ExportParameters(false).Q;
            byte[] publicKey = [.. q.X!, .. q.Y!];
            Address signer = new(Keccak.Compute(publicKey).Bytes[12..]);

            tx.FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeP256, signer, default, default)];
            byte[] signature = key.SignHash(FrameTxSigHash.ComputeValue(tx).Bytes);

            UInt256 s = new(signature.AsSpan(32, 32), isBigEndian: true);
            if (s > SecP256r1Curve.HalfN)
            {
                (SecP256r1Curve.N - s).ToBigEndian(signature.AsSpan(32, 32));
            }

            byte[] raw = [.. signature, .. publicKey];
            return new TxFrameSignature(TxFrameSignature.SchemeP256, signer, default, raw);
        }

        public enum FrameSignatureDefect { None, HighS, LegacyRecoveryId, ForeignSigner }

        private static TxFrameSignature FrameSignature(Transaction tx, FrameSignatureDefect defect)
        {
            PrivateKey key = defect == FrameSignatureDefect.ForeignSigner ? TestItem.PrivateKeyB : TestItem.PrivateKeyA;
            Address signer = TestItem.PrivateKeyA.Address;
            // compute_sig_hash covers the entry's scheme/signer/msg and elides only the raw bytes, so
            // the hash is taken with the entry already installed.
            tx.FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, signer, default, default)];
            Signature signature = new Ecdsa().Sign(key, FrameTxSigHash.ComputeValue(tx));

            byte[] bytes = new byte[TxFrameSignature.Secp256k1SignatureLength];
            bytes[0] = signature.RecoveryId;
            signature.Bytes.CopyTo(bytes.AsSpan(1));

            switch (defect)
            {
                case FrameSignatureDefect.HighS:
                    bytes[0] ^= 1;
                    UInt256 s = new(bytes.AsSpan(33, 32), isBigEndian: true);
                    (SecP256k1Curve.N - s).ToBigEndian(bytes.AsSpan(33, 32));
                    break;
                case FrameSignatureDefect.LegacyRecoveryId:
                    bytes[0] += 27;
                    break;
            }

            return new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, signer, default, bytes);
        }

        // MAX_VERIFY_GAS is a public-mempool DoS bound, not a validity rule: a prefix over the ceiling is
        // still consensus-valid, so the pool must refuse it at ingress rather than the validator.
        [TestCase(100_000UL, false, true, TestName = "prefix exactly at MAX_VERIFY_GAS is accepted")]
        [TestCase(100_001UL, false, false, TestName = "prefix one gas over MAX_VERIFY_GAS is rejected")]
        [TestCase(100_000UL, true, false, TestName = "signature verification cost pushes the prefix over the ceiling")]
        [TestCase(97_200UL, true, true, TestName = "prefix plus signature cost exactly at the ceiling is accepted")]
        public void Frame_transaction_prefix_is_bounded_by_max_verify_gas(ulong verifyGasLimit, bool withSignature, bool expectedAccepted)
        {
            _txPool = CreatePool(new TxPoolConfig { FrameTxMaxVerifyGas = 100_000 }, new TestSpecProvider(Eip8141Prototype.Instance));
            EnsureSenderBalance(TestItem.PrivateKeyA.Address, UInt256.MaxValue);
            ITxPoolPeer peer = Substitute.For<ITxPoolPeer>();
            peer.Id.Returns(TestItem.PublicKeyA);
            _txPool.AddPeer(peer);

            Transaction frameTx = BuildFrameTx(nonce: 0, TestItem.PrivateKeyA.Address, deadline: null, verifyGasLimit: verifyGasLimit);
            if (withSignature)
            {
                // A secp256k1 entry verifies for 2 800 gas, deciding the outcome on its own at a 97 200-gas
                // prefix. It must actually verify: the pool rejects a bad one before comparing the budget.
                frameTx.FrameSignatures = [FrameSignature(frameTx, FrameSignatureDefect.None)];
                frameTx.Hash = frameTx.CalculateHash();
            }

            AcceptTxResult result = _txPool.SubmitTx(frameTx, TxHandlingOptions.PersistentBroadcast);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result, Is.EqualTo(expectedAccepted ? AcceptTxResult.Accepted : AcceptTxResult.FrameTxVerifyGasTooHigh));
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(expectedAccepted ? 1 : 0));
            }

            // Propagation is what the bound exists to stop, so the non-broadcast half is asserted too.
            if (expectedAccepted)
            {
                peer.Received().SendNewTransaction(frameTx);
            }
            else
            {
                peer.DidNotReceive().SendNewTransaction(frameTx);
            }
        }

        [Test]
        public void Frame_transaction_verify_gas_limit_of_zero_lifts_the_bound()
        {
            _txPool = CreatePool(new TxPoolConfig { FrameTxMaxVerifyGas = 0 }, new TestSpecProvider(Eip8141Prototype.Instance));
            EnsureSenderBalance(TestItem.PrivateKeyA.Address, UInt256.MaxValue);

            Transaction frameTx = BuildFrameTx(nonce: 0, TestItem.PrivateKeyA.Address, deadline: null, verifyGasLimit: 15_000_000);

            Assert.That(_txPool.SubmitTx(frameTx, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Accepted));
        }

        [Test]
        public void Frame_transaction_execution_gas_is_outside_the_verify_budget()
        {
            _txPool = CreatePool(new TxPoolConfig { FrameTxMaxVerifyGas = 100_000 }, new TestSpecProvider(Eip8141Prototype.Instance));
            EnsureSenderBalance(TestItem.PrivateKeyA.Address, UInt256.MaxValue);

            // The validation prefix ends at the frame that approves payment; the execution frame after it
            // is paid for out of the transaction's own gas and must not count against the budget.
            Transaction frameTx = BuildFrameTx(nonce: 0, TestItem.PrivateKeyA.Address, deadline: null, verifyGasLimit: 100_000);
            frameTx.Frames =
            [
                frameTx.Frames![0],
                new TxFrame(TxFrame.ModeSender, TxFrame.ApproveScopeNone, TestItem.AddressB, gasLimit: 5_000_000, UInt256.Zero, default),
            ];
            frameTx.Hash = frameTx.CalculateHash();

            Assert.That(_txPool.SubmitTx(frameTx, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Accepted));
        }

        /// <summary>Block production takes the ready-filtered bucket snapshot, so a sender's EIP-8250 keyed
        /// transactions must all appear there; filtering on the account nonce drops the whole bucket.</summary>
        [Test]
        public void Keyed_transactions_of_one_sender_are_all_ready_for_block_production()
        {
            _txPool = CreatePool(null, new TestSpecProvider(new OverridableReleaseSpec(Eip8141Prototype.Instance) { IsEip8250Enabled = true }));
            Address sender = TestItem.PrivateKeyA.Address;
            EnsureSenderBalance(sender, UInt256.MaxValue);
            _stateProvider.CreateAccount(sender, UInt256.MaxValue, AccountNonceUnrelatedToKeyedSequences);

            Transaction[] keyed =
            [
                BuildFrameTx(nonce: 0, sender, deadline: null, nonceKeys: [1]),
                BuildFrameTx(nonce: 0, sender, deadline: null, nonceKeys: [2]),
            ];

            foreach (Transaction tx in keyed)
            {
                Assert.That(_txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Accepted));
            }

            IDictionary<AddressAsKey, Transaction[]> ready = _txPool.GetPendingTransactionsBySender(filterToReadyTx: true);

            Assert.That(ready.TryGetValue(sender, out Transaction[] readyForSender), Is.True,
                "a bucket whose lowest entry is keyed must not be filtered out wholesale");
            Assert.That(readyForSender, Has.Length.EqualTo(keyed.Length));
        }

        /// <summary>
        /// Keyed sequences start at 0 per key while account nonces grow, so the bucket's nonce ordering puts a keyed
        /// frame transaction ahead of the sender's ordinary ones. The whole bucket is then admitted on that entry's
        /// keyed currency, which is why a consumer cannot read the first survivor as the next account nonce.
        /// </summary>
        [Test]
        public void Keyed_frame_tx_heads_the_bucket_ahead_of_an_ordinary_tx_at_the_account_nonce()
        {
            _txPool = CreatePool(null, KeyedNonceSpecProvider());
            Address sender = TestItem.PrivateKeyA.Address;
            EnsureSenderBalance(sender, UInt256.MaxValue);
            _stateProvider.CreateAccount(sender, UInt256.MaxValue, AccountNonceAheadOfKeyedSequences);

            Transaction keyed = BuildKeyedFrameTx(sender, nonceKey: 1, seq: 0, value: UInt256.Zero, maxFee: 1.GWei);
            Transaction atAccountNonce = Build.A.Transaction
                .WithNonce(AccountNonceAheadOfKeyedSequences)
                .WithMaxFeePerGas(1.GWei)
                .WithMaxPriorityFeePerGas(1.GWei)
                .WithGasLimit(21_000)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

            Assert.That(_txPool.SubmitTx(keyed, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(_txPool.SubmitTx(atAccountNonce, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Accepted));

            IDictionary<AddressAsKey, Transaction[]> ready = _txPool.GetPendingTransactionsBySender(filterToReadyTx: true);

            Assert.That(ready.TryGetValue(sender, out Transaction[] readyForSender), Is.True);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(readyForSender[0].Hash, Is.EqualTo(keyed.Hash), "the keyed sequence number sorts ahead of the account nonce");
                Assert.That(readyForSender[1].Hash, Is.EqualTo(atAccountNonce.Hash));
            }
        }

        /// <summary>An account nonce past the keyed sequences, which is the ordinary shape once a sender has sent anything.</summary>
        private const ulong AccountNonceAheadOfKeyedSequences = 100;

        /// <summary>The sender's account nonce, deliberately unequal to the sequence the keyed transactions declare.</summary>
        private const ulong AccountNonceUnrelatedToKeyedSequences = 7;

        [Test]
        public void SubmitTx_FrameTransactions_SharingSimulatedPayer_BoundByPayerBalance_ReleasedOnRemoval()
        {
            // Distinct senders share one opaque-prefix sponsor, so the exposure gate bounds its summed
            // pending cost to its balance, and removing a tx releases the reservation.
            Address sponsor = TestItem.AddressD;
            IFrameTxPrefixSimulator simulator = Substitute.For<IFrameTxPrefixSimulator>();
            simulator.Simulate(Arg.Any<Transaction>()).Returns(FrameTxSimulationResult.Accept(sponsor));
            // The verify-gas bound is out of scope here; disable it so the exposure gate is what binds.
            _txPool = CreatePool(new TxPoolConfig { FrameTxMaxVerifyGas = 0 }, new TestSpecProvider(Eip8141Prototype.Instance), frameTxPrefixSimulator: simulator);

            UInt256 maxCost = (UInt256)1.GWei * 1_000_000;
            EnsureSenderBalance(TestItem.PrivateKeyA.Address, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.PrivateKeyB.Address, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.PrivateKeyC.Address, UInt256.MaxValue);
            EnsureSenderBalance(sponsor, maxCost + maxCost / 2); // fits one tx, not two

            Transaction first = SponsoredFrameTx(TestItem.PrivateKeyA, TestItem.PrivateKeyD);
            Transaction second = SponsoredFrameTx(TestItem.PrivateKeyB, TestItem.PrivateKeyD);
            Transaction third = SponsoredFrameTx(TestItem.PrivateKeyC, TestItem.PrivateKeyD);

            AcceptTxResult firstResult = _txPool.SubmitTx(first, TxHandlingOptions.PersistentBroadcast);
            AcceptTxResult secondResult = _txPool.SubmitTx(second, TxHandlingOptions.PersistentBroadcast);

            _txPool.RemoveTransaction(first.Hash);
            AcceptTxResult thirdResult = _txPool.SubmitTx(third, TxHandlingOptions.PersistentBroadcast);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(firstResult, Is.EqualTo(AcceptTxResult.Accepted), "first sponsored frame tx is within the sponsor's balance");
                Assert.That(secondResult, Is.EqualTo(AcceptTxResult.FrameTxPayerExposureExceeded), "the summed exposure of both txs exceeds the sponsor's balance");
                Assert.That(thirdResult, Is.EqualTo(AcceptTxResult.Accepted), "removing the first tx released the reservation");
            }
        }

        [Test]
        public void Frame_transaction_payer_reservation_is_taken_through_the_pool_and_released_on_removal()
        {
            // BalanceTooLowFilter sums only nonces below tx.Nonce, so a same-nonce replacement is the one
            // shape reaching the exposure gate here.
            _txPool = CreatePool(null, new TestSpecProvider(Eip8141Prototype.Instance));
            EnsureSenderBalance(TestItem.PrivateKeyA.Address, (UInt256)12 * TxGasLimit);

            Transaction first = SelfPayingFrameTx(nonce: 0, feePerGas: 6);
            Transaction bumped = SelfPayingFrameTx(nonce: 0, feePerGas: 7);
            Transaction afterRelease = SelfPayingFrameTx(nonce: 0, feePerGas: 8);

            AcceptTxResult firstResult = _txPool.SubmitTx(first, TxHandlingOptions.None);
            // 6 + 7 exceeds the balance, but the bump displaces the incumbent rather than joining it, so
            // the pending set never holds both and the payer's exposure ends at 7.
            AcceptTxResult bumpedResult = _txPool.SubmitTx(bumped, TxHandlingOptions.None);
            // Within the bound but too small a bump to replace: no Removed fires, so only AddCore's
            // explicit release keeps the payer from leaking.
            AcceptTxResult unreplaceableResult = _txPool.SubmitTx(SelfPayingFrameTx(nonce: 0, feePerGas: 6, distinctHash: true), TxHandlingOptions.None);
            _txPool.RemoveTransaction(bumped.Hash);
            AcceptTxResult afterReleaseResult = _txPool.SubmitTx(afterRelease, TxHandlingOptions.None);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(firstResult, Is.EqualTo(AcceptTxResult.Accepted));
                Assert.That(bumpedResult, Is.EqualTo(AcceptTxResult.Accepted), "a fee bump must not be gated on the reservation it displaces");
                Assert.That(unreplaceableResult, Is.EqualTo(AcceptTxResult.ReplacementNotAllowed));
                Assert.That(afterReleaseResult, Is.EqualTo(AcceptTxResult.Accepted), "both the refused replacement and the removed tx must have released");
            }
        }

        [Test]
        public void Frame_transaction_payer_exposure_counts_pending_nonces_above_the_replaced_one()
        {
            // The gate's teeth beyond BalanceTooLowFilter, which sums only nonces below tx.Nonce: here it
            // admits the bump on its own count while the payer's summed pending cost exceeds the balance.
            _txPool = CreatePool(null, new TestSpecProvider(Eip8141Prototype.Instance));

            // Sized off the reservation itself, so a repricing of max_cost moves the balance with it:
            // 3 pending at fee 3 fit within 10, the bump's 3 undiscounted plus its own 7 do not.
            SelfPayingFrameTx(nonce: 0, feePerGas: 1).IsOverflowInTxCostAndValue(out UInt256 unitCost);
            EnsureSenderBalance(TestItem.PrivateKeyA.Address, (UInt256)10 * unitCost);

            for (ulong nonce = 0; nonce < 3; nonce++)
            {
                Assert.That(_txPool.SubmitTx(SelfPayingFrameTx(nonce, feePerGas: 3), TxHandlingOptions.None),
                    Is.EqualTo(AcceptTxResult.Accepted), $"pending nonce {nonce} is within the payer's balance");
            }

            // Displacing the nonce-0 tx frees only its 3 of the 9 pending, so the bump is priced at 6 + 7.
            AcceptTxResult overBound = _txPool.SubmitTx(SelfPayingFrameTx(nonce: 0, feePerGas: 7), TxHandlingOptions.None);

            Assert.That(overBound, Is.EqualTo(AcceptTxResult.FrameTxPayerExposureExceeded));
        }

        /// <summary>A self_verify frame tx the payer resolver settles natively, so the exposure gate sees a payer.</summary>
        private Transaction SelfPayingFrameTx(ulong nonce, uint feePerGas, bool distinctHash = false, UInt256[] nonceKeys = null)
        {
            Transaction tx = BuildFrameTx(nonce, TestItem.PrivateKeyA.Address, deadline: null,
                maxPriorityFeePerGas: feePerGas, maxFeePerGas: feePerGas, nonceKeys: nonceKeys);
            // Naming the sender explicitly is still a self_verify frame and prices identically, so this
            // varies the hash without touching any input the reservation is computed from.
            if (distinctHash)
            {
                int i = Array.FindIndex(tx.Frames!, f => f.Flags == TxFrame.ApproveExecutionAndPayment);
                Assert.That(i, Is.GreaterThanOrEqualTo(0), "the helper must still build a self_verify frame to retarget");
                TxFrame frame = tx.Frames![i];
                tx.Frames[i] = new TxFrame(frame.Mode, frame.Flags, TestItem.PrivateKeyA.Address, frame.GasLimit, frame.Value, frame.Data);
            }
            tx.FrameSignatures = [FrameSignature(tx, FrameSignatureDefect.None)];
            tx.Hash = tx.CalculateHash();
            return tx;
        }
        private Transaction BuildFrameTx(ulong nonce, Address sender, ulong? deadline, UInt256? maxPriorityFeePerGas = null, UInt256? maxFeePerGas = null, ulong verifyGasLimit = 50_000, UInt256[] nonceKeys = null)
        {
            List<TxFrame> frames =
            [
                new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: verifyGasLimit, UInt256.Zero, default),
            ];

            if (deadline is not null)
            {
                // An expiry verifier frame may appear only as the first frame (EIP-8141 "Expiry Verifier Frame").
                frames.Insert(0, FrameTxTestFrames.ExpiryAt(deadline.Value, gasLimit: 50_000));
            }

            Transaction tx = new()
            {
                Type = TxType.FrameTx,
                ChainId = _specProvider.ChainId,
                Nonce = nonce,
                SenderAddress = sender,
                Frames = [.. frames],
                NonceKeys = nonceKeys,
                FrameSignatures = [],
                GasLimit = TxGasLimit,
                GasPrice = maxPriorityFeePerGas ?? 1.GWei,
                DecodedMaxFeePerGas = maxFeePerGas ?? 1.GWei,
            };
            tx.Hash = tx.CalculateHash();
            return tx;
        }

        [Test]
        public void Frame_transaction_payer_exposure_does_not_discount_a_different_keyed_nonce_domain()
        {
            // EIP-8250: a same-nonce transaction in another nonce-key domain does not compete, so both stay
            // pending and the payer owes both. Discounting it would admit exposure beyond the balance.
            _txPool = CreatePool(null, KeyedNonceSpecProvider());

            SelfPayingFrameTx(nonce: 0, feePerGas: 1).IsOverflowInTxCostAndValue(out UInt256 unitCost);
            EnsureSenderBalance(TestItem.PrivateKeyA.Address, (UInt256)4 * unitCost); // fits one at fee 3, not two

            AcceptTxResult first = _txPool.SubmitTx(
                SelfPayingFrameTx(nonce: 0, feePerGas: 3, nonceKeys: [(UInt256)0]), TxHandlingOptions.None);
            AcceptTxResult second = _txPool.SubmitTx(
                SelfPayingFrameTx(nonce: 0, feePerGas: 3, nonceKeys: [(UInt256)0xbeef]), TxHandlingOptions.None);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(first, Is.EqualTo(AcceptTxResult.Accepted));
                Assert.That(second, Is.EqualTo(AcceptTxResult.FrameTxPayerExposureExceeded),
                    "the keyed transaction joins the pending set rather than displacing the account-domain one");
            }
        }

        // An only_verify|pay prefix naming the sponsor: opaque to native resolution, so it is simulated.
        private Transaction SponsoredFrameTx(PrivateKey senderKey, PrivateKey sponsorKey)
        {
            Transaction tx = new()
            {
                Type = TxType.FrameTx,
                ChainId = _specProvider.ChainId,
                Nonce = 0,
                SenderAddress = senderKey.Address,
                Frames =
                [
                    new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecution, target: null, gasLimit: 100_000, UInt256.Zero, Array.Empty<byte>()),
                    new TxFrame(TxFrame.ModeVerify, TxFrame.ApprovePayment, target: sponsorKey.Address, gasLimit: 0, UInt256.Zero, Array.Empty<byte>()),
                ],
                FrameSignatures =
                [
                    new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, signer: null, default, default),
                    new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, signer: sponsorKey.Address, default, default),
                ],
                GasLimit = 1_000_000,
                GasPrice = 1.GWei,
                DecodedMaxFeePerGas = 1.GWei,
            };
            // compute_sig_hash covers each entry's scheme/signer/msg and elides only the raw bytes, so the
            // signatures are taken with the entries already installed, then their bytes are filled in.
            ValueHash256 sigHash = FrameTxSigHash.ComputeValue(tx);
            tx.FrameSignatures =
            [
                Secp256k1FrameSignature(senderKey, in sigHash, signer: null),
                Secp256k1FrameSignature(sponsorKey, in sigHash, signer: sponsorKey.Address),
            ];
            tx.Hash = tx.CalculateHash();
            return tx;
        }

        private const ulong KeyedFrameTxGasLimit = 1_000_000;

        private static ISpecProvider KeyedNonceSpecProvider() =>
            new TestSpecProvider(new OverridableReleaseSpec(Eip8141Prototype.Instance) { IsEip8250Enabled = true });

        private Transaction BuildKeyedFrameTx(Address sender, UInt256 nonceKey, ulong seq, UInt256 value, UInt256 maxFee)
        {
            Transaction tx = new()
            {
                Type = TxType.FrameTx,
                ChainId = _specProvider.ChainId,
                Nonce = seq,
                SenderAddress = sender,
                NonceKeys = [nonceKey],
                Frames = [new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: 100_000, UInt256.Zero, default)],
                FrameSignatures = [],
                GasLimit = KeyedFrameTxGasLimit,
                Value = value,
                GasPrice = maxFee,
                DecodedMaxFeePerGas = maxFee,
            };
            tx.Hash = tx.CalculateHash();
            return tx;
        }

        private static TxFrameSignature Secp256k1FrameSignature(PrivateKey key, in ValueHash256 sigHash, Address signer)
        {
            Signature signature = new Ecdsa().Sign(key, sigHash);
            byte[] bytes = new byte[TxFrameSignature.Secp256k1SignatureLength];
            bytes[0] = signature.RecoveryId;
            signature.Bytes.CopyTo(bytes.AsSpan(1));
            return new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, signer, default, bytes);
        }

        [Test]
        public async Task Over_value_keyed_tx_does_not_dump_a_valid_plain_tx_from_the_same_sender()
        {
            _txPool = CreatePool(null, KeyedNonceSpecProvider());
            Address sender = TestItem.PrivateKeyA.Address;
            EnsureSenderBalance(sender, 100.Ether);

            Transaction plain = Build.A.Transaction
                .WithNonce(0)
                .WithValue(1.Ether)
                .WithMaxFeePerGas(1.GWei)
                .WithMaxPriorityFeePerGas(1.GWei)
                .WithGasLimit(21_000)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            Assert.That(_txPool.SubmitTx(plain, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Accepted));

            Transaction keyed = BuildKeyedFrameTx(sender, nonceKey: 0xbeef, seq: 0, value: 10.Ether, maxFee: 10.GWei);
            Assert.That(_txPool.SubmitTx(keyed, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Accepted));

            EnsureSenderBalance(sender, 5.Ether);

            await RaiseBlockAddedToMainAndWaitForNewHead(Build.A.Block.WithNumber(1).TestObject);

            Assert.That(_txPool.IsKnown(plain.Hash), Is.True,
                "a valid plain transaction must survive an over-value keyed transaction sharing its sender bucket");
        }

        [Test]
        public async Task Keyed_tx_that_can_no_longer_fund_its_gas_is_evicted_and_may_re_enter()
        {
            _txPool = CreatePool(null, KeyedNonceSpecProvider());
            Address sender = TestItem.PrivateKeyA.Address;
            EnsureSenderBalance(sender, 100.Ether);

            UInt256 maxFee = 1.GWei;
            Transaction keyed = BuildKeyedFrameTx(sender, nonceKey: 0xbeef, seq: 0, value: UInt256.Zero, maxFee: maxFee);
            Assert.That(_txPool.SubmitTx(keyed, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(1));

            UInt256 gasCost = maxFee * (UInt256)KeyedFrameTxGasLimit;
            EnsureSenderBalance(sender, gasCost - UInt256.One);

            await RaiseBlockAddedToMainAndWaitForNewHead(Build.A.Block.WithNumber(1).TestObject);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(0),
                    "a keyed transaction the sender can no longer fund must be evicted, not left pending until block production");
                Assert.That(_txPool.IsKnown(keyed.Hash), Is.False,
                    "the eviction clears the long-term cache so the transaction can re-enter once the balance recovers");
            }
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
            TxPoolConfig txPoolConfig = new() { Size = 30, PersistentBlobStorageSize = 0 };
            _txPool = CreatePool(txPoolConfig, specProvider);

            Transaction testTx = Build.A.Transaction
                .WithNonce(0)
                .WithMaxFeePerGas(9.GWei)
                .WithMaxPriorityFeePerGas(9.GWei)
                .WithGasLimit(100_000)
                .WithTo(TestItem.AddressB)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

            EnsureSenderBalance(TestItem.PrivateKeyA.Address, UInt256.MaxValue);

            _stateProvider.InsertCode(TestItem.PrivateKeyA.Address, testCase.code, Prague.Instance);

            AcceptTxResult result = _txPool.SubmitTx(testTx, TxHandlingOptions.PersistentBroadcast);
            Assert.That(result, Is.EqualTo(testCase.expected));
        }

        private static IEnumerable<object> DifferentOrderNonces()
        {
            yield return new object[] { 0UL, 1UL, AcceptTxResult.Accepted, AcceptTxResult.NotCurrentNonceForDelegation };
            yield return new object[] { 2UL, 5UL, AcceptTxResult.NotCurrentNonceForDelegation, AcceptTxResult.NotCurrentNonceForDelegation };
            yield return new object[] { 1UL, 0UL, AcceptTxResult.NotCurrentNonceForDelegation, AcceptTxResult.Accepted };
            yield return new object[] { 5UL, 0UL, AcceptTxResult.NotCurrentNonceForDelegation, AcceptTxResult.Accepted };
        }

        [TestCaseSource(nameof(DifferentOrderNonces))]
        public void Delegated_account_can_only_have_one_tx_with_current_account_nonce(ulong firstNonce, ulong secondNonce, AcceptTxResult firstExpectation, AcceptTxResult secondExpectation)
        {
            ISpecProvider specProvider = GetPragueSpecProvider();
            TxPoolConfig txPoolConfig = new() { Size = 30, PersistentBlobStorageSize = 0 };
            _txPool = CreatePool(txPoolConfig, specProvider);

            PrivateKey signer = TestItem.PrivateKeyA;
            _stateProvider.CreateAccount(signer.Address, UInt256.MaxValue);
            byte[] delegation = [.. Eip7702Constants.DelegationHeader, .. TestItem.AddressC.Bytes];
            _stateProvider.InsertCode(signer.Address, delegation.AsMemory(), Prague.Instance);

            Transaction firstTx = Build.A.Transaction
                .WithNonce(firstNonce)
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas(9.GWei)
                .WithMaxPriorityFeePerGas(9.GWei)
                .WithGasLimit(GasCostOf.Transaction)
                .WithTo(TestItem.AddressB)
                .SignedAndResolved(_ethereumEcdsa, signer).TestObject;

            AcceptTxResult result = _txPool.SubmitTx(firstTx, TxHandlingOptions.PersistentBroadcast);
            Assert.That(result, Is.EqualTo(firstExpectation));

            Transaction secondTx = Build.A.Transaction
                .WithNonce(secondNonce)
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas(9.GWei)
                .WithMaxPriorityFeePerGas(9.GWei)
                .WithGasLimit(GasCostOf.Transaction)
                .WithTo(TestItem.AddressB)
                .SignedAndResolved(_ethereumEcdsa, signer).TestObject;

            result = _txPool.SubmitTx(secondTx, TxHandlingOptions.PersistentBroadcast);

            Assert.That(result, Is.EqualTo(secondExpectation));
        }


        private static readonly object[] NonceAndRemovedCases =
        {
            new object[]{ true, 1UL, AcceptTxResult.Accepted },
            new object[]{ true, 0UL, AcceptTxResult.Accepted},
            new object[]{ false, 0UL, AcceptTxResult.Accepted},
            new object[]{ false, 1UL, AcceptTxResult.NotCurrentNonceForDelegation},
        };

        [TestCaseSource(nameof(NonceAndRemovedCases))]
        public void Tx_with_conflicting_pending_delegation_is_rejected_then_is_accepted_after_delegation_removal(bool withRemoval, ulong secondNonce, AcceptTxResult expected)
        {
            ISpecProvider specProvider = GetPragueSpecProvider();
            TxPoolConfig txPoolConfig = new() { Size = 30, PersistentBlobStorageSize = 0 };
            _txPool = CreatePool(txPoolConfig, specProvider);

            PrivateKey signer = TestItem.PrivateKeyA;
            PrivateKey sponsor = TestItem.PrivateKeyB;
            _stateProvider.CreateAccount(signer.Address, UInt256.MaxValue);
            _stateProvider.CreateAccount(sponsor.Address, UInt256.MaxValue);

            EthereumEcdsa ecdsa = new(_specProvider.ChainId);

            Transaction firstTx = Build.A.Transaction
                .WithNonce(0)
                .WithType(TxType.SetCode)
                .WithMaxFeePerGas(9.GWei)
                .WithMaxPriorityFeePerGas(9.GWei)
                .WithGasLimit(100_000)
                .WithAuthorizationCode(ecdsa.Sign(signer, specProvider.ChainId, TestItem.AddressC, 0))
                .WithTo(TestItem.AddressB)
                .SignedAndResolved(_ethereumEcdsa, sponsor).TestObject;

            AcceptTxResult result = _txPool.SubmitTx(firstTx, TxHandlingOptions.PersistentBroadcast);
            Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));

            if (withRemoval)
            {
                _txPool.RemoveTransaction(firstTx.Hash);
            }

            Transaction secondTx = Build.A.Transaction
                .WithNonce(secondNonce)
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas(12.GWei)
                .WithMaxPriorityFeePerGas(12.GWei)
                .WithGasLimit(GasCostOf.Transaction)
                .WithTo(TestItem.AddressB)
                .SignedAndResolved(_ethereumEcdsa, signer).TestObject;

            result = _txPool.SubmitTx(secondTx, TxHandlingOptions.PersistentBroadcast);
            Assert.That(result, Is.EqualTo(expected));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void SetCode_tx_has_authority_with_pending_transaction_is_rejected_then_is_accepted_after_tx_removal(bool withRemoval)
        {
            ISpecProvider specProvider = GetPragueSpecProvider();
            TxPoolConfig txPoolConfig = new() { Size = 30, PersistentBlobStorageSize = 0 };
            _txPool = CreatePool(txPoolConfig, specProvider);

            PrivateKey signer = TestItem.PrivateKeyA;
            _stateProvider.CreateAccount(signer.Address, UInt256.MaxValue);

            EthereumEcdsa ecdsa = new(_specProvider.ChainId);

            Transaction firstTx = Build.A.Transaction
                .WithNonce(0)
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas(9.GWei)
                .WithMaxPriorityFeePerGas(9.GWei)
                .WithGasLimit(GasCostOf.Transaction)
                .WithTo(TestItem.AddressB)
                .SignedAndResolved(_ethereumEcdsa, signer).TestObject;

            AcceptTxResult result = _txPool.SubmitTx(firstTx, TxHandlingOptions.PersistentBroadcast);
            Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));

            if (withRemoval)
            {
                _txPool.RemoveTransaction(firstTx.Hash);
            }

            Transaction secondTx = Build.A.Transaction
                .WithNonce(0)
                .WithType(TxType.SetCode)
                .WithMaxFeePerGas(9.GWei)
                .WithMaxPriorityFeePerGas(9.GWei)
                .WithGasLimit(100_000)
                .WithAuthorizationCode(ecdsa.Sign(signer, specProvider.ChainId, TestItem.AddressC, 0))
                .WithTo(TestItem.AddressB)
                .SignedAndResolved(_ethereumEcdsa, signer).TestObject;

            result = _txPool.SubmitTx(secondTx, TxHandlingOptions.PersistentBroadcast);
            Assert.That(result, Is.EqualTo(withRemoval ? AcceptTxResult.Accepted : AcceptTxResult.DelegatorHasPendingTx));
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
            TxPoolConfig txPoolConfig = new() { Size = 1, PersistentBlobStorageSize = 0 };
            _txPool = CreatePool(txPoolConfig, specProvider);

            PrivateKey signer = TestItem.PrivateKeyA;
            PrivateKey sponsor = TestItem.PrivateKeyB;
            _stateProvider.CreateAccount(signer.Address, UInt256.MaxValue);
            _stateProvider.CreateAccount(sponsor.Address, UInt256.MaxValue);

            EthereumEcdsa ecdsa = new(_specProvider.ChainId);

            // filling transaction pool
            _stateProvider.CreateAccount(TestItem.PrivateKeyC.Address, UInt256.MaxValue);
            Transaction poolFillerTx = Build.A.Transaction
                .WithNonce(0)
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas(15.GWei)
                .WithMaxPriorityFeePerGas(15.GWei)
                .WithGasLimit(GasCostOf.Transaction)
                .WithTo(TestItem.AddressB)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyC).TestObject;

            AcceptTxResult result = _txPool.SubmitTx(poolFillerTx, TxHandlingOptions.None);
            Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));

            // should be added only to local txs if isLocalDelegation is true
            Transaction firstTx = Build.A.Transaction
                .WithNonce(0)
                .WithType(TxType.SetCode)
                .WithMaxFeePerGas((isLocalDelegation ? 10 : 20).GWei)
                .WithMaxPriorityFeePerGas((isLocalDelegation ? 10 : 20).GWei)
                .WithGasLimit(100_000)
                .WithAuthorizationCode(ecdsa.Sign(signer, specProvider.ChainId, TestItem.AddressC, 0))
                .WithTo(TestItem.AddressB)
                .SignedAndResolved(_ethereumEcdsa, sponsor).TestObject;

            result = _txPool.SubmitTx(firstTx, TxHandlingOptions.PersistentBroadcast);
            Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));

            // should be accepted if pending delegation is only local
            Transaction secondTx = Build.A.Transaction
                .WithNonce(1) // nonce is 1 otherwise it would always be accepted
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas(25.GWei)
                .WithMaxPriorityFeePerGas(25.GWei)
                .WithGasLimit(GasCostOf.Transaction)
                .WithTo(TestItem.AddressB)
                .SignedAndResolved(_ethereumEcdsa, signer).TestObject;

            result = _txPool.SubmitTx(secondTx, TxHandlingOptions.PersistentBroadcast);
            Assert.That(result, Is.EqualTo(isLocalDelegation ? AcceptTxResult.Accepted : AcceptTxResult.NotCurrentNonceForDelegation));
        }

        private static IEnumerable<TestCaseData> SetCodeReplacedTxCases()
        {
            yield return new TestCaseData(
                TestItem.PrivateKeyB,
                (TestReadOnlyStateProvider state, Address account, IReleaseSpec spec) =>
                {
                    state.CreateAccount(account, UInt256.MaxValue);
                    state.CreateAccount(TestItem.AddressB, UInt256.MaxValue);
                },
                AcceptTxResult.Accepted
            ).SetName("Not self sponsored - Accepted");
            yield return new TestCaseData(
                TestItem.PrivateKeyA,
                (TestReadOnlyStateProvider state, Address account, IReleaseSpec spec) =>
                {
                    state.CreateAccount(account, UInt256.MaxValue);
                },
                AcceptTxResult.Accepted
            ).SetName("Self sponsored - Accepted");
            yield return new TestCaseData(
                TestItem.PrivateKeyA,
                (TestReadOnlyStateProvider state, Address account, IReleaseSpec spec) =>
                {
                    state.CreateAccount(account, UInt256.MaxValue);
                    byte[] delegation = [.. Eip7702Constants.DelegationHeader, .. TestItem.AddressB.Bytes];
                    state.InsertCode(account, delegation, spec);
                },
                AcceptTxResult.NotCurrentNonceForDelegation
            ).SetName("Self sponsored delegated - NotCurrentNonceForDelegation");
        }

        [TestCaseSource(nameof(SetCodeReplacedTxCases))]
        public void SetCode_tx_can_be_replaced_and_remove_pending_delegation_restriction(
            PrivateKey sponsor, Action<TestReadOnlyStateProvider, Address, IReleaseSpec> accountSetup, AcceptTxResult lastExpectation)
        {
            ISpecProvider specProvider = GetPragueSpecProvider();
            TxPoolConfig txPoolConfig = new() { Size = 30, PersistentBlobStorageSize = 0 };
            _txPool = CreatePool(txPoolConfig, specProvider);

            PrivateKey signer = TestItem.PrivateKeyA;
            accountSetup(_stateProvider, signer.Address, Prague.Instance);

            EthereumEcdsa ecdsa = new(_specProvider.ChainId);

            Transaction firstSetcodeTx = Build.A.Transaction
                .WithNonce(0)
                .WithType(TxType.SetCode)
                .WithMaxFeePerGas(9.GWei)
                .WithMaxPriorityFeePerGas(9.GWei)
                .WithGasLimit(100_000)
                .WithAuthorizationCode(ecdsa.Sign(signer, specProvider.ChainId, TestItem.AddressC, 0))
                .WithTo(TestItem.AddressB)
                .SignedAndResolved(_ethereumEcdsa, sponsor).TestObject;

            AcceptTxResult result = _txPool.SubmitTx(firstSetcodeTx, TxHandlingOptions.PersistentBroadcast);
            Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));

            Transaction replacementTx = Build.A.Transaction
                .WithNonce(0)
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas(12.GWei)
                .WithMaxPriorityFeePerGas(12.GWei)
                .WithGasLimit(GasCostOf.Transaction)
                .WithTo(TestItem.AddressB)
                .SignedAndResolved(_ethereumEcdsa, sponsor).TestObject;

            result = _txPool.SubmitTx(replacementTx, TxHandlingOptions.PersistentBroadcast);

            Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));

            Transaction thirdTx = Build.A.Transaction
                .WithNonce(1)
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas(9.GWei)
                .WithMaxPriorityFeePerGas(9.GWei)
                .WithGasLimit(GasCostOf.Transaction)
                .WithTo(TestItem.AddressB)
                .SignedAndResolved(_ethereumEcdsa, signer).TestObject;

            result = _txPool.SubmitTx(thirdTx, TxHandlingOptions.PersistentBroadcast);

            Assert.That(result, Is.EqualTo(lastExpectation));
        }

        [Test]
        public void Pending_delegation_guard_survives_removing_one_of_two_same_authority_delegations()
        {
            ISpecProvider specProvider = GetPragueSpecProvider();
            TxPoolConfig txPoolConfig = new() { Size = 30, PersistentBlobStorageSize = 0 };
            _txPool = CreatePool(txPoolConfig, specProvider);

            PrivateKey authority = TestItem.PrivateKeyA;
            PrivateKey sponsorA = TestItem.PrivateKeyB;
            PrivateKey sponsorB = TestItem.PrivateKeyC;
            _stateProvider.CreateAccount(authority.Address, UInt256.MaxValue);
            _stateProvider.CreateAccount(sponsorA.Address, UInt256.MaxValue);
            _stateProvider.CreateAccount(sponsorB.Address, UInt256.MaxValue);

            EthereumEcdsa ecdsa = new(_specProvider.ChainId);

            Transaction Delegation(PrivateKey sponsor) => Build.A.Transaction
                .WithNonce(0)
                .WithType(TxType.SetCode)
                .WithMaxFeePerGas(9.GWei)
                .WithMaxPriorityFeePerGas(9.GWei)
                .WithGasLimit(100_000)
                .WithAuthorizationCode(ecdsa.Sign(authority, specProvider.ChainId, TestItem.AddressD, 0))
                .WithTo(TestItem.AddressB)
                .SignedAndResolved(_ethereumEcdsa, sponsor).TestObject;

            Transaction firstDelegation = Delegation(sponsorA);
            Transaction secondDelegation = Delegation(sponsorB);
            Assert.That(_txPool.SubmitTx(firstDelegation, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(_txPool.SubmitTx(secondDelegation, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Accepted));

            _txPool.RemoveTransaction(firstDelegation.Hash);

            Transaction AuthorityTx(ulong nonce) => Build.A.Transaction
                .WithNonce(nonce)
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas(9.GWei)
                .WithMaxPriorityFeePerGas(9.GWei)
                .WithGasLimit(GasCostOf.Transaction)
                .WithTo(TestItem.AddressB)
                .SignedAndResolved(_ethereumEcdsa, authority).TestObject;

            Assert.That(_txPool.SubmitTx(AuthorityTx(0), TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(_txPool.SubmitTx(AuthorityTx(1), TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.NotCurrentNonceForDelegation));
        }

        [TestCase(1ul, 2ul)]
        [TestCase(0ul, 0ul)]
        [TestCase(ulong.MaxValue, ulong.MaxValue)]
        [TestCase(0ul, ulong.MaxValue)]
        [TestCase(ulong.MaxValue, 0ul)]
        public void when_delegation_is_pending_sender_can_always_replace_tx_with_current_nonce(ulong authNonce, ulong authChainId)
        {
            ISpecProvider specProvider = GetPragueSpecProvider();
            TxPoolConfig txPoolConfig = new() { Size = 10, PersistentBlobStorageSize = 10 };
            _txPool = CreatePool(txPoolConfig, specProvider);

            PrivateKey signer = TestItem.PrivateKeyA;
            PrivateKey sponsor = TestItem.PrivateKeyB;
            _stateProvider.CreateAccount(signer.Address, UInt256.MaxValue);
            _stateProvider.CreateAccount(sponsor.Address, UInt256.MaxValue);

            EthereumEcdsa ecdsa = new(_specProvider.ChainId);

            AuthorizationTuple authTuple = ecdsa.Sign(signer, authChainId, TestItem.AddressC, authNonce);

            Transaction setCodeTx = Build.A.Transaction
                .WithNonce(0)
                .WithType(TxType.SetCode)
                .WithMaxFeePerGas((20).GWei)
                .WithMaxPriorityFeePerGas((20).GWei)
                .WithGasLimit(100_000)
                .WithAuthorizationCode(authTuple)
                .WithTo(TestItem.AddressB)
                .SignedAndResolved(_ethereumEcdsa, sponsor).TestObject;

            //Submit SetCode tx so signer has pending delegation
            AcceptTxResult result = _txPool.SubmitTx(setCodeTx, TxHandlingOptions.PersistentBroadcast);
            Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));

            //Submit a replacement tx of each type with current nonce
            foreach (byte type in ((byte[])Enum.GetValues(typeof(TxType))))
            {
                1.GWei.Multiply((UInt256)type, out UInt256 feeCap);
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
                    case TxType.FrameTx:
                        //Frame txs are rejected at ingress under Prague; EIP-8141 activates at Bogota
                        continue;
                    case TxType.DepositTx:
                        continue;
                }
                builder.SignedAndResolved(_ethereumEcdsa, signer);

                //Signer submits a tx of all every type with current nonce
                result = _txPool.SubmitTx(builder.TestObject, TxHandlingOptions.PersistentBroadcast);
                Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
            }
        }

        private IDictionary<ITxPoolPeer, PrivateKey> GetPeers(int limit = 100)
        {
            Dictionary<ITxPoolPeer, PrivateKey> peers = [];
            for (int i = 0; i < limit; i++)
            {
                PrivateKey privateKey = Build.A.PrivateKey.TestObject;
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
            bool thereIsPriorityContract = false,
            IEthereumEcdsa ethereumEcdsa = null,
            IFrameTxPrefixSimulator frameTxPrefixSimulator = null)
        {
            specProvider ??= MainnetSpecProvider.Instance;
            ITransactionComparerProvider transactionComparerProvider =
                new TransactionComparerProvider(specProvider, _blockTree);
            txStorage ??= new BlobTxStorage();

            _headInfo = chainHeadInfoProvider;
            _headInfo ??= new ChainHeadInfoProvider(
                new ChainHeadSpecProvider(specProvider, _blockTree),
                _blockTree,
                _stateProvider);

            return new TxPool(
                ethereumEcdsa ?? _ethereumEcdsa,
                txStorage,
                _headInfo,
                config ?? new TxPoolConfig() { GasLimit = TxGasLimit },
                new TxValidator(_specProvider.ChainId),
                _logManager,
                transactionComparerProvider.GetDefaultComparer(),
                ShouldGossip.Instance,
                incomingTxFilter is null ? null : [incomingTxFilter],
                new HeadTxValidator(),
                thereIsPriorityContract,
                frameTxPrefixSimulator);
        }

        private ITxPoolPeer GetPeer(PublicKey publicKey)
        {
            ITxPoolPeer peer = Substitute.For<ITxPoolPeer>();
            peer.Id.Returns(publicKey);

            return peer;
        }

        private static ISpecProvider GetLondonSpecProvider() => new TestSpecProvider(London.Instance);

        private static ISpecProvider GetCancunSpecProvider() => new TestSpecProvider(Cancun.Instance);

        private static ISpecProvider GetPragueSpecProvider() => new TestSpecProvider(Prague.Instance);

        private static ISpecProvider GetOsakaSpecProvider() => new TestSpecProvider(Osaka.Instance);

        private Transaction[] AddTransactionsToPool(bool sameTransactionSenderPerPeer = true, bool sameNoncePerPeer = false, int transactionsPerPeer = 10)
        {
            Transaction[] transactions = GetTransactions(GetPeers(transactionsPerPeer), sameTransactionSenderPerPeer, sameNoncePerPeer);

            foreach (Address address in transactions.Select(static t => t.SenderAddress).Distinct())
            {
                EnsureSenderBalance(address, UInt256.MaxValue);
            }

            foreach (Transaction transaction in transactions)
            {
                _txPool.SubmitTx(transaction, TxHandlingOptions.PersistentBroadcast);
            }

            return transactions;
        }

        private Transaction AddTransactionToPool(bool isOwn = true)
        {
            Transaction transaction = GetTransaction(TestItem.PrivateKeyA, Address.Zero);
            _txPool.SubmitTx(transaction, isOwn ? TxHandlingOptions.PersistentBroadcast : TxHandlingOptions.None);
            return transaction;
        }

        private void DeleteTransactionsFromPool(params Transaction[] transactions)
        {
            foreach (Transaction transaction in transactions)
            {
                _txPool.RemoveTransaction(transaction.Hash);
            }
        }

        private Transaction[] GetTransactions(IDictionary<ITxPoolPeer, PrivateKey> peers, bool sameTransactionSenderPerPeer = true, bool sameNoncePerPeer = true, int transactionsPerPeer = 10)
        {
            List<Transaction> transactions = [];
            foreach ((_, PrivateKey privateKey) in peers)
            {
                for (int i = 0; i < transactionsPerPeer; i++)
                {
                    transactions.Add(GetTransaction(sameTransactionSenderPerPeer ? privateKey : Build.A.PrivateKey.TestObject, Address.FromNumber((UInt256)i), sameNoncePerPeer ? UInt256.Zero : (UInt256?)i));
                }
            }

            return transactions.ToArray();
        }

        private Transaction GetTransaction(PrivateKey privateKey, Address to = null, UInt256? nonce = null)
        {
            Transaction transaction = GetTransaction((ulong)(nonce ?? UInt256.Zero), GasCostOf.Transaction, (nonce ?? 999) + 1, to, [], privateKey);
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

        private void EnsureSenderBalance(Address address, UInt256 balance) => _stateProvider.CreateAccount(address, balance);

        private Transaction GetTransaction(ulong nonce, ulong gasLimit, UInt256 gasPrice, Address to, byte[] data,
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
            _blockTree.RaiseBlockAddedToMain(blockReplacementEventArgs);
            for (int i = 0; i < txCount; i++)
            {
                await semaphoreSlim.WaitAsync(1000);
            }
        }

        private async Task RaiseBlockAddedToMainAndWaitForNewHead(Block block, Block previousBlock = null)
        {
            BlockReplacementEventArgs blockReplacementEventArgs = previousBlock is null
                ? new BlockReplacementEventArgs(block ?? Build.A.Block.TestObject)
                : new BlockReplacementEventArgs(block ?? Build.A.Block.TestObject, previousBlock);

            Task waitTask = Wait.ForEventCondition<Block>(
                CancellationToken.None,
                e => _txPool.TxPoolHeadChanged += e,
                e => _txPool.TxPoolHeadChanged -= e,
                e => e.Number == block.Number
            );

            _blockTree.RaiseBlockAddedToMain(blockReplacementEventArgs);
            await waitTask;
        }

        [Test]
        public async Task should_bring_back_reorganized_txs()
        {
            const ulong blockNumber = 358;

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

            Assert.That(_txPool.SubmitTx(txsA[0], TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(_txPool.SubmitTx(txsA[1], TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(_txPool.SubmitTx(txsB[0], TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(txsA.Length + txsB.Length));
                Assert.That(_txPool.GetPendingBlobTransactionsCount(), Is.EqualTo(0));
            }

            // adding block A
            Block blockA = Build.A.Block.WithNumber(blockNumber).WithTransactions(txsA).TestObject;
            await RaiseBlockAddedToMainAndWaitForNewHead(blockA);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(txsB.Length));
                Assert.That(_txPool.GetPendingBlobTransactionsCount(), Is.EqualTo(0));
                Assert.That(_txPool.TryGetPendingTransaction(txsA[0].Hash!, out _), Is.False);
                Assert.That(_txPool.TryGetPendingTransaction(txsA[1].Hash!, out _), Is.False);
                Assert.That(_txPool.TryGetPendingTransaction(txsB[0].Hash!, out _), Is.True);
            }

            // reorganized from block A to block B
            Block blockB = Build.A.Block.WithNumber(blockNumber).WithTransactions(txsB).TestObject;
            await RaiseBlockAddedToMainAndWaitForNewHead(blockB, blockA);

            // tx from block B should be removed from tx pool
            Assert.That(_txPool.TryGetPendingTransaction(txsB[0].Hash!, out _), Is.False);

            // txs from reorganized blockA should be readded to tx pool
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(txsA.Length));
                Assert.That(_txPool.TryGetPendingTransaction(txsA[0].Hash!, out Transaction tx1), Is.True);
                Assert.That(_txPool.TryGetPendingTransaction(txsA[1].Hash!, out Transaction tx2), Is.True);

                Assert.That(tx1, Is.EqualTo(txsA[0]).UsingTransactionComparer(nameof(Transaction.PoolIndex)));

                Assert.That(tx2, Is.EqualTo(txsA[1]).UsingTransactionComparer(nameof(Transaction.PoolIndex)));
            }
        }

        [Test]
        [Category("Flaky"), Retry(3)]
        public async Task should_return_fresh_pending_transactions_snapshot_after_head_change()
        {
            const ulong blockNumber = 358;

            ITxPoolConfig txPoolConfig = new TxPoolConfig()
            {
                Size = 128,
                BlobsSupport = BlobsSupportMode.Disabled
            };
            _txPool = CreatePool(txPoolConfig, GetCancunSpecProvider());

            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.AddressB, UInt256.MaxValue);

            Transaction txA = GetTx(TestItem.PrivateKeyA);
            Transaction txB = GetTx(TestItem.PrivateKeyB);

            Assert.That(_txPool.SubmitTx(txA, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(_txPool.SubmitTx(txB, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));

            // Cache the snapshot before head change
            Transaction[] snapshotBefore = _txPool.GetPendingTransactions();
            Assert.That(snapshotBefore, Has.Length.EqualTo(2));

            // Process block that includes txA
            Block block = Build.A.Block.WithNumber(blockNumber).WithTransactions(txA).TestObject;
            await RaiseBlockAddedToMainAndWaitForNewHead(block);

            // Snapshot must reflect the updated pool state, not the stale cache
            Transaction[] snapshotAfter = _txPool.GetPendingTransactions();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(snapshotAfter, Has.Length.EqualTo(1));
                Assert.That(snapshotAfter, Has.One.Matches<Transaction>(t => t.Hash == txB.Hash));
                Assert.That(snapshotAfter, Has.None.Matches<Transaction>(t => t.Hash == txA.Hash));
            }
        }

        [Test]
        public async Task should_return_valid_snapshot_when_reading_concurrently_during_head_change()
        {
            const ulong blockNumber = 358;
            const int maxTryCount = 5;

            for (int attempt = 0; attempt < maxTryCount; attempt++)
            {
                ITxPoolConfig txPoolConfig = new TxPoolConfig()
                {
                    Size = 128,
                    BlobsSupport = BlobsSupportMode.Disabled
                };
                _txPool = CreatePool(txPoolConfig, GetCancunSpecProvider());

                EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);
                EnsureSenderBalance(TestItem.AddressB, UInt256.MaxValue);

                Transaction txA = GetTx(TestItem.PrivateKeyA);
                Transaction txB = GetTx(TestItem.PrivateKeyB);

                Assert.That(_txPool.SubmitTx(txA, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
                Assert.That(_txPool.SubmitTx(txB, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));

                // Warm up the snapshot cache
                Assert.That(_txPool.GetPendingTransactions(), Has.Length.EqualTo(2));

                // Start concurrent readers
                bool stopReading = false;
                Task[] readers = new Task[4];
                for (int i = 0; i < readers.Length; i++)
                {
                    readers[i] = Task.Run(() =>
                    {
                        while (!Volatile.Read(ref stopReading))
                        {
                            _txPool.GetPendingTransactions();
                        }
                    });
                }

                // Process block that includes txA
                Block block = Build.A.Block.WithNumber(blockNumber).WithTransactions(txA).TestObject;
                await RaiseBlockAddedToMainAndWaitForNewHead(block);

                Volatile.Write(ref stopReading, true);
                await Task.WhenAll(readers);

                // After head processing completes, snapshot must be up-to-date
                Transaction[] snapshot = _txPool.GetPendingTransactions();
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(snapshot, Has.Length.EqualTo(1));
                    Assert.That(snapshot, Has.One.Matches<Transaction>(t => t.Hash == txB.Hash));
                    Assert.That(snapshot, Has.None.Matches<Transaction>(t => t.Hash == txA.Hash));
                }

                // Re-create test state for the next attempt
                await _txPool.DisposeAsync();
                Setup();
            }
        }

        private Transaction GetTx(PrivateKey sender) => Build.A.Transaction
                .WithMaxFeePerGas(1.GWei)
                .WithMaxPriorityFeePerGas(1.GWei)
                .WithNonce(0UL)
                .SignedAndResolved(_ethereumEcdsa, sender).TestObject;
    }
}
