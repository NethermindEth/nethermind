// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
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
using Nethermind.Core.Eip2930;
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
        // Deliberately below Amsterdam's intrinsic gas requirement for the access list built below.
        private const ulong UnderGassedTransactionGasLimit = 42_400;

        private static AccessList BuildUnderGassedAccessList()
        {
            AccessList.Builder accessListBuilder = new();
            accessListBuilder.AddAddress(TestItem.AddressC);
            for (int i = 0; i < 10; i++)
            {
                accessListBuilder.AddStorage((UInt256)i);
            }

            return accessListBuilder.Build();
        }

        private static Signature FlipSignature(Signature signature)
        {
            UInt256 s = new(signature.SAsSpan, isBigEndian: true);
            UInt256 flippedS = SecP256k1Curve.N - s;
            return new Signature(signature.RAsSpan, flippedS.ToBigEndian(), signature.V == Signature.VOffset ? 28UL : 27UL);
        }

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
        public async Task should_evict_transactions_that_become_under_gassed_after_fork()
        {
            Block head = _blockTree.Head;
            _blockTree.BestSuggestedHeader = head.Header;

            TestSpecProvider provider = new(Osaka.Instance)
            {
                NextForkSpec = Amsterdam.Instance,
                ForkOnBlockNumber = head.Number + 1
            };

            Transaction transaction = Build.A.Transaction
                .WithType(TxType.AccessList)
                .WithAccessList(BuildUnderGassedAccessList())
                .WithGasLimit(UnderGassedTransactionGasLimit)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
                .TestObject;
            Transaction followUpTransaction = Build.A.Transaction
                .WithType(TxType.AccessList)
                .WithAccessList(BuildUnderGassedAccessList())
                .WithGasLimit(TxGasLimit)
                .WithNonce(transaction.Nonce + 1)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
                .TestObject;
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            _txPool = CreatePool(specProvider: provider);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.SubmitTx(transaction, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
                Assert.That(_txPool.SubmitTx(followUpTransaction, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(2));
            }

            await AddEmptyBlock();
            Assert.That(() => _txPool.IsRevalidatedFor(_blockTree.BestSuggestedHeader), Is.True.After(Timeout, 10));

            Assert.That(_txPool.GetPendingTransactionsCount(), Is.Zero);

            _blockTree.BestSuggestedHeader = head.Header;
            await RaiseBlockAddedToMainAndWaitForNewHead(head);

            Assert.That(_txPool.SubmitTx(transaction, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
        }

        [Test]
        public async Task should_revalidate_when_head_spec_changes_during_construction()
        {
            BlobTxStorage storage = new();
            _txPool = CreatePool(specProvider: new TestSpecProvider(Prague.Instance), txStorage: storage);
            Assert.That(
                () => ((ISpecChangeValidationStorage)storage).GetSpecChangeValidationMarker(),
                Is.Not.Null.After(Timeout, 10));
            await _txPool.DisposeAsync();

            IChainHeadSpecProvider changingSpecProvider = Substitute.For<IChainHeadSpecProvider>();
            // Both pools are empty, so UpdateBucketsWithoutRevalidation consumes no head-spec read:
            // ObserveHeadSpec and InitializeValidatedSpec see Prague; the final startup check sees Osaka.
            changingSpecProvider.GetCurrentHeadSpec().Returns(Prague.Instance, Prague.Instance, Osaka.Instance);
            changingSpecProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(Osaka.Instance);
            ChainHeadInfoProvider headInfo = new(changingSpecProvider, _blockTree, _stateProvider);

            _txPool = CreatePool(
                specProvider: new TestSpecProvider(Prague.Instance),
                chainHeadInfoProvider: headInfo,
                txStorage: storage);

            Assert.That(
                () => _txPool.IsRevalidatedFor(Build.A.BlockHeader.TestObject),
                Is.True.After(Timeout, 10));
        }

        [Test]
        public async Task should_remember_fork_invalidated_transaction_when_insufficient_balance_dumps_bucket()
        {
            Block head = _blockTree.Head;
            _blockTree.BestSuggestedHeader = head.Header;
            TestSpecProvider provider = new(Osaka.Instance)
            {
                NextForkSpec = Amsterdam.Instance,
                ForkOnBlockNumber = head.Number + 1
            };
            Transaction transaction = Build.A.Transaction
                .WithType(TxType.AccessList)
                .WithAccessList(BuildUnderGassedAccessList())
                .WithGasLimit(UnderGassedTransactionGasLimit)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
                .TestObject;
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);
            _txPool = CreatePool(specProvider: provider);
            Assert.That(_txPool.SubmitTx(transaction, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));

            EnsureSenderBalance(TestItem.AddressA, UInt256.Zero);
            await AddEmptyBlock();
            Assert.That(() => _txPool.IsRevalidatedFor(_blockTree.BestSuggestedHeader), Is.True.After(Timeout, 10));
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);
            int pendingTransactionsCount = _txPool.GetPendingTransactionsCount();
            AcceptTxResult resubmissionResult = _txPool.SubmitTx(transaction, TxHandlingOptions.None);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pendingTransactionsCount, Is.Zero);
                Assert.That(resubmissionResult, Is.EqualTo(AcceptTxResult.AlreadyKnown));
            }
        }

        [Test]
        public async Task should_evict_transactions_that_exceed_the_gas_limit_cap_after_fork()
        {
            Block head = _blockTree.Head;
            _blockTree.BestSuggestedHeader = head.Header;

            TestSpecProvider provider = new(Prague.Instance)
            {
                NextForkSpec = Osaka.Instance,
                ForkOnBlockNumber = head.Number + 1
            };

            _txPool = CreatePool(new TxPoolConfig { GasLimit = long.MaxValue }, provider);
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            Transaction transaction = Build.A.Transaction
                .WithGasLimit(Eip7825Constants.DefaultTxGasLimitCap + 1)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
                .TestObject;

            Assert.That(_txPool.SubmitTx(transaction, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(1));

            await AddEmptyBlock();
            Assert.That(() => _txPool.IsRevalidatedFor(_blockTree.BestSuggestedHeader), Is.True.After(Timeout, 10));

            Assert.That(_txPool.GetPendingTransactionsCount(), Is.Zero);
        }

        [Test]
        public async Task should_evict_transaction_type_disabled_by_reorg()
        {
            Block preForkHead = _blockTree.Head;
            Block forkHead = Build.A.Block.WithNumber(preForkHead.Number + 1).TestObject;
            TestSpecProvider provider = new(Cancun.Instance)
            {
                NextForkSpec = Prague.Instance,
                ForkOnBlockNumber = forkHead.Number
            };
            _blockTree.BestSuggestedHeader = forkHead.Header;

            _txPool = CreatePool(specProvider: provider);
            EnsureSenderBalance(TestItem.AddressB, UInt256.MaxValue);
            Transaction transaction = Build.A.Transaction
                .WithType(TxType.SetCode)
                .WithMaxFeePerGas(9.GWei)
                .WithMaxPriorityFeePerGas(9.GWei)
                .WithGasLimit(100_000)
                .WithAuthorizationCode(_ethereumEcdsa.Sign(TestItem.PrivateKeyA, provider.ChainId, TestItem.AddressC, 0))
                .WithTo(TestItem.AddressA)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyB)
                .TestObject;

            Assert.That(_txPool.SubmitTx(transaction, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));

            _blockTree.BestSuggestedHeader = preForkHead.Header;
            await RaiseBlockAddedToMainAndWaitForNewHead(preForkHead, forkHead);
            Assert.That(() => _txPool.IsRevalidatedFor(preForkHead.Header), Is.True.After(Timeout, 10));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.Zero);
            }
        }

        [Test]
        public async Task should_evict_transaction_above_init_code_limit_after_fork()
        {
            Block head = _blockTree.Head;
            _blockTree.BestSuggestedHeader = head.Header;
            OverridableReleaseSpec preForkSpec = new(Shanghai.Instance) { IsEip3860Enabled = false };
            OverridableReleaseSpec postForkSpec = new(Shanghai.Instance) { IsEip3860Enabled = true };
            TestSpecProvider provider = new(preForkSpec)
            {
                NextForkSpec = postForkSpec,
                ForkOnBlockNumber = head.Number + 1
            };
            Transaction transaction = Build.A.Transaction
                .WithTo(null)
                .WithValue(0)
                .WithData(new byte[checked((int)postForkSpec.MaxInitCodeSize + 1)])
                .WithGasLimit(TxGasLimit)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
                .TestObject;
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);
            _txPool = CreatePool(specProvider: provider);

            Assert.That(_txPool.SubmitTx(transaction, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));

            await AddEmptyBlock();
            Assert.That(() => _txPool.IsRevalidatedFor(_blockTree.BestSuggestedHeader), Is.True.After(Timeout, 10));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.Zero);
                Assert.That(_txPool.IsRevalidatedFor(_blockTree.BestSuggestedHeader), Is.True);
            }
        }

        [Test]
        public async Task should_evict_high_s_signature_after_eip2_fork()
        {
            Block head = _blockTree.Head;
            _blockTree.BestSuggestedHeader = head.Header;
            OverridableReleaseSpec preForkSpec = new(Homestead.Instance) { IsEip2Enabled = false };
            OverridableReleaseSpec postForkSpec = new(Homestead.Instance) { IsEip2Enabled = true };
            TestSpecProvider provider = new(preForkSpec)
            {
                NextForkSpec = postForkSpec,
                ForkOnBlockNumber = head.Number + 1
            };
            Transaction transaction = Build.A.Transaction
                .WithTo(TestItem.AddressB)
                .WithValue(0)
                .Signed(_ethereumEcdsa, TestItem.PrivateKeyA, isEip155Enabled: false)
                .With(tx => tx.Signature = FlipSignature(tx.Signature!))
                .WithSenderAddress(TestItem.AddressA)
                .TestObject;
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);
            _txPool = CreatePool(specProvider: provider);

            Assert.That(_txPool.SubmitTx(transaction, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));

            await AddEmptyBlock();
            Assert.That(() => _txPool.IsRevalidatedFor(_blockTree.BestSuggestedHeader), Is.True.After(Timeout, 10));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.Zero);
                Assert.That(_txPool.IsRevalidatedFor(_blockTree.BestSuggestedHeader), Is.True);
            }
        }

        [Test]
        public async Task should_evict_wrong_chain_signature_after_eip155_fork()
        {
            Block head = _blockTree.Head;
            _blockTree.BestSuggestedHeader = head.Header;
            OverridableReleaseSpec preForkSpec = new(SpuriousDragon.Instance)
            {
                IsEip155Enabled = false,
                ValidateChainId = false
            };
            OverridableReleaseSpec postForkSpec = new(SpuriousDragon.Instance)
            {
                IsEip155Enabled = true,
                ValidateChainId = true
            };
            TestSpecProvider provider = new(preForkSpec)
            {
                NextForkSpec = postForkSpec,
                ForkOnBlockNumber = head.Number + 1
            };
            EthereumEcdsa wrongChainEcdsa = new(_specProvider.ChainId + 1);
            Transaction transaction = Build.A.Transaction
                .WithTo(TestItem.AddressB)
                .WithValue(0)
                .Signed(wrongChainEcdsa, TestItem.PrivateKeyA)
                .WithSenderAddress(TestItem.AddressA)
                .TestObject;
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);
            _txPool = CreatePool(specProvider: provider);

            Assert.That(_txPool.SubmitTx(transaction, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));

            await AddEmptyBlock();
            Assert.That(() => _txPool.IsRevalidatedFor(_blockTree.BestSuggestedHeader), Is.True.After(Timeout, 10));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.Zero);
                Assert.That(_txPool.IsRevalidatedFor(_blockTree.BestSuggestedHeader), Is.True);
            }
        }

        [Test]
        public async Task should_run_spec_change_validation_only_at_fork_boundary()
        {
            Block head = _blockTree.Head;
            _blockTree.BestSuggestedHeader = head.Header;

            TestSpecProvider provider = new(Prague.Instance)
            {
                NextForkSpec = Osaka.Instance,
                ForkOnBlockNumber = head.Number + 2
            };
            ISpecChangeTxValidator specChangeTxValidator = Substitute.For<ISpecChangeTxValidator>();
            specChangeTxValidator.IsWellFormed(Arg.Any<Transaction>(), Arg.Any<IReleaseSpec>()).Returns(ValidationResult.Success);
            specChangeTxValidator.IsWellFormedAfterFullValidation(
                    Arg.Any<Transaction>(),
                    Arg.Any<IReleaseSpec>())
                .Returns(ValidationResult.Success);

            _txPool = CreatePool(specProvider: provider, specChangeTxValidator: specChangeTxValidator);
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            Transaction transaction = Build.A.Transaction
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
                .TestObject;

            Assert.That(_txPool.SubmitTx(transaction, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
            specChangeTxValidator.Received(1).IsWellFormedAfterFullValidation(
                transaction,
                Prague.Instance);
            specChangeTxValidator.ClearReceivedCalls();
            Assert.That(_txPool.IsRevalidatedFor(Build.A.BlockHeader.WithNumber(head.Number + 1).TestObject), Is.True);

            await AddEmptyBlock();

            specChangeTxValidator.DidNotReceiveWithAnyArgs().IsWellFormed(default, default);

            Block nextBlock = Build.A.Block.WithNumber(head.Number + 2).TestObject;
            _blockTree.BestSuggestedHeader = nextBlock.Header;
            Assert.That(_txPool.IsRevalidatedFor(nextBlock.Header), Is.False);
            await RaiseBlockAddedToMainAndWaitForNewHead(nextBlock);
            Assert.That(() => _txPool.IsRevalidatedFor(nextBlock.Header), Is.True.After(Timeout, 10));

            specChangeTxValidator.Received(1).IsWellFormed(transaction, Osaka.Instance);
            Assert.That(_txPool.IsRevalidatedFor(nextBlock.Header), Is.True);
        }

        [Test]
        public async Task should_report_fork_state_safe_after_empty_pool_crosses_fork()
        {
            Block head = _blockTree.Head;
            _blockTree.BestSuggestedHeader = head.Header;

            TestSpecProvider provider = new(Prague.Instance)
            {
                NextForkSpec = Osaka.Instance,
                ForkOnBlockNumber = head.Number + 1
            };
            _txPool = CreatePool(specProvider: provider);

            await AddEmptyBlock();

            Assert.That(() => _txPool.IsRevalidatedFor(_blockTree.BestSuggestedHeader), Is.True.After(Timeout, 10));
        }

        [Test]
        public async Task should_drop_revalidated_state_when_transaction_is_accepted_under_another_spec()
        {
            Block head = _blockTree.Head;
            _blockTree.BestSuggestedHeader = head.Header;

            TestSpecProvider provider = new(Prague.Instance)
            {
                NextForkSpec = Osaka.Instance,
                ForkOnBlockNumber = head.Number + 1
            };
            _txPool = CreatePool(specProvider: provider);
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.AddressB, UInt256.MaxValue);

            Assert.That(_txPool.SubmitTx(Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject,
                TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));

            await AddEmptyBlock();
            BlockHeader forkHeader = _blockTree.BestSuggestedHeader;
            Assert.That(() => _txPool.IsRevalidatedFor(forkHeader), Is.True.After(Timeout, 10));

            // A reorg back below the fork makes the pool accept transactions under the previous rules again,
            // which stops the mark left by the walk for the fork spec from covering the pool.
            _blockTree.BestSuggestedHeader = head.Header;
            Assert.That(_txPool.SubmitTx(Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyB).TestObject,
                TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));

            _blockTree.BestSuggestedHeader = forkHeader;
            Assert.That(_txPool.IsRevalidatedFor(forkHeader), Is.False);
        }

        [Test]
        public void should_keep_latest_revalidation_request_when_an_older_request_arrives_last()
        {
            LatestRevalidationRequest request = new();

            request.Update(2);
            request.Update(1);

            Assert.That(request.Generation, Is.EqualTo(2));
        }

        [Test]
        public async Task should_retry_spec_change_revalidation_after_failure()
        {
            Block head = _blockTree.Head;
            _blockTree.BestSuggestedHeader = head.Header;

            TestSpecProvider provider = new(Prague.Instance)
            {
                NextForkSpec = Osaka.Instance,
                ForkOnBlockNumber = head.Number + 1
            };
            ITxValidator specChangeTxValidator = Substitute.For<ITxValidator>();
            TaskCompletionSource revalidationFailureTriggered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using ManualResetEventSlim allowRevalidation = new(false);
            int validationAttempts = 0;
            specChangeTxValidator.IsWellFormed(Arg.Any<Transaction>(), Arg.Any<IReleaseSpec>()).Returns(callInfo =>
            {
                if (!ReferenceEquals(callInfo.Arg<IReleaseSpec>(), Osaka.Instance))
                {
                    return ValidationResult.Success;
                }

                Interlocked.Increment(ref validationAttempts);
                if (!allowRevalidation.IsSet)
                {
                    revalidationFailureTriggered.TrySetResult();
                    throw new InvalidOperationException();
                }

                return ValidationResult.Success;
            });

            _txPool = CreatePool(specProvider: provider, specChangeTxValidator: specChangeTxValidator);
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.AddressB, UInt256.MaxValue);

            Transaction transaction = Build.A.Transaction
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
                .TestObject;
            Transaction unaffordableTransaction = Build.A.Transaction
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyB)
                .TestObject;

            Assert.That(_txPool.SubmitTx(transaction, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(_txPool.SubmitTx(unaffordableTransaction, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
            EnsureSenderBalance(TestItem.AddressB, UInt256.Zero);

            Block forkBlock = Build.A.Block.WithNumber(head.Number + 1).TestObject;
            _blockTree.BestSuggestedHeader = forkBlock.Header;
            Task forkHeadProcessed = Wait.ForEventCondition<Block>(
                CancellationToken.None,
                handler => _txPool.TxPoolHeadChanged += handler,
                handler => _txPool.TxPoolHeadChanged -= handler,
                block => block.Hash == forkBlock.Hash);
            _blockTree.RaiseBlockAddedToMain(new BlockReplacementEventArgs(forkBlock));
            await revalidationFailureTriggered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await forkHeadProcessed.WaitAsync(TimeSpan.FromSeconds(10));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.IsRevalidatedFor(forkBlock.Header), Is.False);
                Assert.That(() => _txPool.GetPendingTransactionsCount(), Is.EqualTo(1).After(Timeout, 10));
                Assert.That(_txPool.ContainsTx(transaction.Hash!, transaction.Type), Is.True);
            }

            allowRevalidation.Set();

            Block nextBlock = Build.A.Block.WithNumber(forkBlock.Number + 1).TestObject;
            _blockTree.BestSuggestedHeader = nextBlock.Header;
            await RaiseBlockAddedToMainAndWaitForNewHead(nextBlock);
            Assert.That(() => _txPool.IsRevalidatedFor(nextBlock.Header), Is.True.After(Timeout, 10));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.IsRevalidatedFor(nextBlock.Header), Is.True);
                Assert.That(validationAttempts, Is.GreaterThanOrEqualTo(2));
            }
        }

        [Test]
        public async Task should_reconcile_buckets_when_spec_change_revalidation_is_abandoned()
        {
            Block head = _blockTree.Head;
            _blockTree.BestSuggestedHeader = head.Header;
            TestSpecProvider provider = new(Prague.Instance)
            {
                NextForkSpec = Osaka.Instance,
                ForkOnBlockNumber = head.Number + 1
            };
            Transaction retainedTransaction = Build.A.Transaction
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
                .TestObject;
            Transaction unaffordableTransaction = Build.A.Transaction
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyB)
                .TestObject;
            TaskCompletionSource firstPassStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource secondPassStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> firstPassReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> secondPassReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using ManualResetEventSlim releaseFirstPass = new(false);
            using ManualResetEventSlim releaseSecondPass = new(false);
            int retainedTransactionValidations = 0;
            ISpecChangeTxValidator specChangeTxValidator = Substitute.For<ISpecChangeTxValidator>();
            specChangeTxValidator.IsWellFormedAfterFullValidation(
                    Arg.Any<Transaction>(),
                    Arg.Any<IReleaseSpec>())
                .Returns(ValidationResult.Success);
            specChangeTxValidator.IsWellFormed(Arg.Any<Transaction>(), Arg.Any<IReleaseSpec>()).Returns(callInfo =>
            {
                if (ReferenceEquals(callInfo.Arg<Transaction>(), retainedTransaction))
                {
                    int validation = Interlocked.Increment(ref retainedTransactionValidations);
                    if (validation == 1)
                    {
                        firstPassStarted.TrySetResult();
                        firstPassReleased.TrySetResult(releaseFirstPass.Wait(TimeSpan.FromSeconds(10)));
                    }
                    else if (validation == 2)
                    {
                        secondPassStarted.TrySetResult();
                        secondPassReleased.TrySetResult(releaseSecondPass.Wait(TimeSpan.FromSeconds(10)));
                    }
                }

                return ValidationResult.Success;
            });

            _txPool = CreatePool(specProvider: provider, specChangeTxValidator: specChangeTxValidator);
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.AddressB, UInt256.MaxValue);
            Assert.That(_txPool.SubmitTx(retainedTransaction, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(_txPool.SubmitTx(unaffordableTransaction, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
            EnsureSenderBalance(TestItem.AddressB, UInt256.Zero);

            Block forkBlock = Build.A.Block.WithNumber(head.Number + 1).TestObject;
            _blockTree.BestSuggestedHeader = forkBlock.Header;
            _blockTree.RaiseBlockAddedToMain(new BlockReplacementEventArgs(forkBlock));
            await firstPassStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Block rollbackBlock = Build.A.Block.WithNumber(head.Number).TestObject;
            _blockTree.BestSuggestedHeader = rollbackBlock.Header;
            Task rollbackProcessed = Wait.ForEventCondition<Block>(
                CancellationToken.None,
                handler => _txPool.TxPoolHeadChanged += handler,
                handler => _txPool.TxPoolHeadChanged -= handler,
                block => block.Hash == rollbackBlock.Hash);
            _blockTree.RaiseBlockAddedToMain(new BlockReplacementEventArgs(rollbackBlock));

            try
            {
                await rollbackProcessed.WaitAsync(TimeSpan.FromSeconds(10));
                releaseFirstPass.Set();
                await secondPassStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.That(await firstPassReleased.Task.WaitAsync(TimeSpan.FromSeconds(10)), Is.True);

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(_txPool.ContainsTx(retainedTransaction.Hash!, retainedTransaction.Type), Is.True);
                    Assert.That(_txPool.ContainsTx(unaffordableTransaction.Hash!, unaffordableTransaction.Type), Is.False);
                    Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(1));
                }
            }
            finally
            {
                releaseFirstPass.Set();
                releaseSecondPass.Set();
            }

            Assert.That(await secondPassReleased.Task.WaitAsync(TimeSpan.FromSeconds(10)), Is.True);
            Assert.That(() => _txPool.IsRevalidatedFor(rollbackBlock.Header), Is.True.After(Timeout, 10));
        }

        [Test]
        public async Task should_apply_revalidation_evictions_when_head_advances_during_pass()
        {
            Block head = _blockTree.Head;
            _blockTree.BestSuggestedHeader = head.Header;
            TestSpecProvider provider = new(Prague.Instance)
            {
                NextForkSpec = Osaka.Instance,
                ForkOnBlockNumber = head.Number + 1
            };
            TaskCompletionSource revalidationStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using ManualResetEventSlim releaseRevalidation = new(false);
            int validationAttempts = 0;
            Transaction invalidTransaction = Build.A.Transaction
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
                .TestObject;
            Transaction validTransaction = Build.A.Transaction
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyB)
                .TestObject;
            ITxValidator specChangeTxValidator = Substitute.For<ITxValidator>();
            specChangeTxValidator.IsWellFormed(Arg.Any<Transaction>(), Arg.Any<IReleaseSpec>()).Returns(callInfo =>
            {
                if (!ReferenceEquals(callInfo.Arg<IReleaseSpec>(), Osaka.Instance))
                {
                    return ValidationResult.Success;
                }

                Interlocked.Increment(ref validationAttempts);
                revalidationStarted.TrySetResult();
                Assert.That(
                    releaseRevalidation.Wait(TimeSpan.FromSeconds(10)),
                    Is.True,
                    "Timed out waiting to release fork revalidation.");
                return ReferenceEquals(callInfo.Arg<Transaction>(), invalidTransaction)
                    ? new ValidationResult("fork rejection")
                    : ValidationResult.Success;
            });

            _txPool = CreatePool(specProvider: provider, specChangeTxValidator: specChangeTxValidator);
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.AddressB, UInt256.MaxValue);
            Assert.That(_txPool.SubmitTx(invalidTransaction, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(_txPool.SubmitTx(validTransaction, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));

            Block forkBlock = Build.A.Block.WithNumber(head.Number + 1).TestObject;
            _blockTree.BestSuggestedHeader = forkBlock.Header;
            _blockTree.RaiseBlockAddedToMain(new BlockReplacementEventArgs(forkBlock));
            await revalidationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Block nextBlock = Build.A.Block.WithNumber(forkBlock.Number + 1).TestObject;
            _blockTree.BestSuggestedHeader = nextBlock.Header;
            Task nextHeadProcessed = Wait.ForEventCondition<Block>(
                CancellationToken.None,
                handler => _txPool.TxPoolHeadChanged += handler,
                handler => _txPool.TxPoolHeadChanged -= handler,
                block => block.Hash == nextBlock.Hash);
            _blockTree.RaiseBlockAddedToMain(new BlockReplacementEventArgs(nextBlock));
            releaseRevalidation.Set();

            await nextHeadProcessed.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(() => _txPool.IsRevalidatedFor(nextBlock.Header), Is.True.After(Timeout, 10));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(validationAttempts, Is.EqualTo(2));
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(1));
                Assert.That(_txPool.ContainsTx(invalidTransaction.Hash!, invalidTransaction.Type), Is.False);
                Assert.That(_txPool.ContainsTx(validTransaction.Hash!, validTransaction.Type), Is.True);
                Assert.That(_txPool.IsRevalidatedFor(nextBlock.Header), Is.True);
            }
        }

        [Test]
        public async Task should_restart_revalidation_after_head_spec_changes_and_returns_during_pass()
        {
            Block head = _blockTree.Head;
            _blockTree.BestSuggestedHeader = head.Header;
            TestSpecProvider provider = new(Prague.Instance)
            {
                NextForkSpec = Osaka.Instance,
                ForkOnBlockNumber = head.Number + 1
            };
            TaskCompletionSource revalidationStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using ManualResetEventSlim releaseRevalidation = new(false);
            Transaction existingTransaction = Build.A.Transaction
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
                .TestObject;
            Transaction invalidUnderOsaka = Build.A.Transaction
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyB)
                .TestObject;
            ITxValidator specChangeTxValidator = Substitute.For<ITxValidator>();
            specChangeTxValidator.IsWellFormed(Arg.Any<Transaction>(), Arg.Any<IReleaseSpec>()).Returns(callInfo =>
            {
                if (!ReferenceEquals(callInfo.Arg<IReleaseSpec>(), Osaka.Instance))
                {
                    return ValidationResult.Success;
                }

                revalidationStarted.TrySetResult();
                Assert.That(
                    releaseRevalidation.Wait(TimeSpan.FromSeconds(10)),
                    Is.True,
                    "Timed out waiting to release fork revalidation.");
                return ReferenceEquals(callInfo.Arg<Transaction>(), invalidUnderOsaka)
                    ? new ValidationResult("fork rejection")
                    : ValidationResult.Success;
            });

            _txPool = CreatePool(specProvider: provider, specChangeTxValidator: specChangeTxValidator);
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.AddressB, UInt256.MaxValue);
            Assert.That(_txPool.SubmitTx(existingTransaction, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));

            Block forkBlock = Build.A.Block.WithNumber(head.Number + 1).TestObject;
            _blockTree.BestSuggestedHeader = forkBlock.Header;
            _blockTree.RaiseBlockAddedToMain(new BlockReplacementEventArgs(forkBlock));
            await revalidationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Block rollbackBlock = Build.A.Block.WithNumber(head.Number).TestObject;
            _blockTree.BestSuggestedHeader = rollbackBlock.Header;
            _blockTree.RaiseBlockAddedToMain(new BlockReplacementEventArgs(rollbackBlock));
            Assert.That(_txPool.SubmitTx(invalidUnderOsaka, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));

            Block finalBlock = Build.A.Block.WithNumber(forkBlock.Number + 1).TestObject;
            _blockTree.BestSuggestedHeader = finalBlock.Header;
            Task finalHeadProcessed = Wait.ForEventCondition<Block>(
                CancellationToken.None,
                handler => _txPool.TxPoolHeadChanged += handler,
                handler => _txPool.TxPoolHeadChanged -= handler,
                block => block.Hash == finalBlock.Hash);
            _blockTree.RaiseBlockAddedToMain(new BlockReplacementEventArgs(finalBlock));
            releaseRevalidation.Set();

            await finalHeadProcessed.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(() => _txPool.IsRevalidatedFor(finalBlock.Header), Is.True.After(Timeout, 10));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.ContainsTx(invalidUnderOsaka.Hash!, invalidUnderOsaka.Type), Is.False);
                Assert.That(_txPool.IsRevalidatedFor(finalBlock.Header), Is.True);
            }
        }

        [Test]
        public async Task should_revalidate_after_queued_fork_reorg()
        {
            Block head = _blockTree.Head;
            _blockTree.BestSuggestedHeader = head.Header;

            TestSpecProvider provider = new(Prague.Instance)
            {
                NextForkSpec = Osaka.Instance,
                ForkOnBlockNumber = head.Number + 1
            };
            ITxValidator specChangeTxValidator = Substitute.For<ITxValidator>();
            specChangeTxValidator.IsWellFormed(Arg.Any<Transaction>(), Arg.Any<IReleaseSpec>()).Returns(ValidationResult.Success);

            _txPool = CreatePool(specProvider: provider, specChangeTxValidator: specChangeTxValidator);
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.AddressB, UInt256.MaxValue);

            Transaction transaction = Build.A.Transaction
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
                .TestObject;

            Assert.That(_txPool.SubmitTx(transaction, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));

            ReaderWriterLockSlim newHeadLock = (ReaderWriterLockSlim)typeof(TxPool)
                .GetField("_newHeadLock", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(_txPool)!;
            Block forkBlock = Build.A.Block.WithNumber(head.Number + 1).TestObject;
            Transaction forkSpecTransaction = Build.A.Transaction
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyB)
                .TestObject;
            Task finalHeadProcessed = Wait.ForEventCondition<Block>(
                CancellationToken.None,
                handler => _txPool.TxPoolHeadChanged += handler,
                handler => _txPool.TxPoolHeadChanged -= handler,
                block => block.Hash == head.Hash);

            // Holding the read lock keeps both head changes queued, so the pool accepts a transaction under the
            // fork rules and then falls back below the fork without ever being walked in between.
            newHeadLock.EnterReadLock();
            try
            {
                _blockTree.BestSuggestedHeader = forkBlock.Header;
                _blockTree.RaiseBlockAddedToMain(new BlockReplacementEventArgs(forkBlock));
                Assert.That(_txPool.SubmitTx(forkSpecTransaction, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
                specChangeTxValidator.ClearReceivedCalls();

                _blockTree.BestSuggestedHeader = head.Header;
                _blockTree.RaiseBlockAddedToMain(new BlockReplacementEventArgs(head, forkBlock));

                Assert.That(_txPool.IsRevalidatedFor(head.Header), Is.False);
            }
            finally
            {
                newHeadLock.ExitReadLock();
            }

            await finalHeadProcessed.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(() => _txPool.IsRevalidatedFor(head.Header), Is.True.After(Timeout, 10));

            using (Assert.EnterMultipleScope())
            {
                specChangeTxValidator.Received(1).IsWellFormed(transaction, Prague.Instance);
                specChangeTxValidator.Received(1).IsWellFormed(forkSpecTransaction, Prague.Instance);
                Assert.That(_txPool.IsRevalidatedFor(head.Header), Is.True);
            }
        }

        [Test]
        [NonParallelizable]
        public async Task should_keep_submission_production_and_head_processing_responsive_during_revalidation()
        {
            Block head = _blockTree.Head;
            _blockTree.BestSuggestedHeader = head.Header;
            TestSpecProvider provider = new(Prague.Instance)
            {
                NextForkSpec = Osaka.Instance,
                ForkOnBlockNumber = head.Number + 1
            };
            Transaction transaction = Build.A.Transaction
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
                .TestObject;
            Transaction concurrentTransaction = Build.A.Transaction
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyB)
                .TestObject;
            TaskCompletionSource revalidationStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using ManualResetEventSlim releaseRevalidation = new(false);
            ITxValidator specChangeTxValidator = Substitute.For<ITxValidator>();
            specChangeTxValidator.IsWellFormed(Arg.Any<Transaction>(), Arg.Any<IReleaseSpec>()).Returns(callInfo =>
            {
                if (ReferenceEquals(callInfo.Arg<Transaction>(), transaction)
                    && ReferenceEquals(callInfo.Arg<IReleaseSpec>(), Osaka.Instance))
                {
                    revalidationStarted.TrySetResult();
                    Assert.That(
                        releaseRevalidation.Wait(TimeSpan.FromSeconds(10)),
                        Is.True,
                        "Timed out waiting to release fork revalidation.");
                }

                return ValidationResult.Success;
            });

            _txPool = CreatePool(specProvider: provider, specChangeTxValidator: specChangeTxValidator);
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.AddressB, UInt256.MaxValue);
            Assert.That(_txPool.SubmitTx(transaction, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));

            Block forkBlock = Build.A.Block.WithNumber(head.Number + 1).TestObject;
            _blockTree.BestSuggestedHeader = forkBlock.Header;
            Task headProcessed = Wait.ForEventCondition<Block>(
                CancellationToken.None,
                handler => _txPool.TxPoolHeadChanged += handler,
                handler => _txPool.TxPoolHeadChanged -= handler,
                block => block.Hash == forkBlock.Hash);
            _blockTree.RaiseBlockAddedToMain(new BlockReplacementEventArgs(forkBlock));

            try
            {
                await headProcessed.WaitAsync(TimeSpan.FromSeconds(10));
                await revalidationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

                Task<(PendingTransactionsView View, AcceptTxResult Result)> concurrentAccess = RunOnDedicatedThread(() =>
                {
                    PendingTransactionsView view = _txPool.GetPendingForProduction(forkBlock.Header, filterToReadyTx: false, UInt256.Zero);
                    AcceptTxResult result = _txPool.SubmitTx(concurrentTransaction, TxHandlingOptions.None);
                    return (view, result);
                });

                (PendingTransactionsView view, AcceptTxResult result) = await concurrentAccess.WaitAsync(TimeSpan.FromSeconds(5));
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(view.IsRevalidated, Is.False);
                    Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
                }

                Block nextBlock = Build.A.Block
                    .WithNumber(forkBlock.Number + 1)
                    .WithTransactions(concurrentTransaction)
                    .TestObject;
                _blockTree.BestSuggestedHeader = nextBlock.Header;
                Task nextHeadProcessed = Wait.ForEventCondition<Block>(
                    CancellationToken.None,
                    handler => _txPool.TxPoolHeadChanged += handler,
                    handler => _txPool.TxPoolHeadChanged -= handler,
                    block => block.Hash == nextBlock.Hash);
                _blockTree.RaiseBlockAddedToMain(new BlockReplacementEventArgs(nextBlock));

                await nextHeadProcessed.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(_txPool.ContainsTx(concurrentTransaction.Hash!, concurrentTransaction.Type), Is.False);
            }
            finally
            {
                releaseRevalidation.Set();
            }

            Assert.That(
                () => _txPool.IsRevalidatedFor(_blockTree.BestSuggestedHeader),
                Is.True.After(Timeout, 10));
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
        public async Task Shedding_reads_the_pressure_left_after_the_head_s_own_bucket_cleanup()
        {
            // The slot UpdateBuckets is about to free is not pressure, so nothing should be shed for it.
            _txPool = CreatePool(new TxPoolConfig { Size = 2 }, new TestSpecProvider(Eip8141Prototype.Instance));
            EnsureSenderBalance(TestItem.PrivateKeyA.Address, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.PrivateKeyB.Address, UInt256.MaxValue);

            await RaiseBlockAddedToMainAndWaitForNewHead(Build.A.Block.WithNumber(1).WithTimestamp(1_000).TestObject);
            Transaction nearlyExpired = BuildFrameTx(nonce: 0, TestItem.PrivateKeyA.Address, deadline: 1_005);
            // No deadline, so it is never itself a shed candidate: it is only here to fill the pool.
            Transaction staleNonce = BuildFrameTx(nonce: 0, TestItem.PrivateKeyB.Address, deadline: null);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.SubmitTx(nearlyExpired, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
                Assert.That(_txPool.SubmitTx(staleNonce, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(2), "the pool must be full, or nothing would be shed either way");
            }

            // The new head consumes B's nonce, so UpdateBuckets drops that transaction and the pool is
            // no longer full by the time shedding runs.
            _stateProvider.IncrementNonce(TestItem.PrivateKeyB.Address);
            await RaiseBlockAddedToMainAndWaitForNewHead(Build.A.Block.WithNumber(2).WithTimestamp(1_000).TestObject);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.TryGetPendingTransaction(nearlyExpired.Hash!, out _), Is.True,
                    "nothing needed the slot, so the near-expiry frame transaction keeps it");
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(1), "the stale-nonce transaction still leaves");
            }
        }

        [Test]
        public async Task Shedding_breaks_an_equal_deadline_on_the_lower_priority_fee()
        {
            // The spec's second key: among equal deadlines the lowest effective priority fee yields first.
            _txPool = CreatePool(new TxPoolConfig { Size = 2 }, new TestSpecProvider(Eip8141Prototype.Instance));
            EnsureSenderBalance(TestItem.PrivateKeyA.Address, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.PrivateKeyB.Address, UInt256.MaxValue);

            await RaiseBlockAddedToMainAndWaitForNewHead(Build.A.Block.WithNumber(1).WithTimestamp(1_000).TestObject);
            _txPool.SubmitTx(BuildFrameTx(nonce: 0, TestItem.PrivateKeyA.Address, deadline: 1_005,
                maxPriorityFeePerGas: 1.GWei, maxFeePerGas: 1.GWei), TxHandlingOptions.None);
            Transaction richer = BuildFrameTx(nonce: 0, TestItem.PrivateKeyB.Address, deadline: 1_005,
                maxPriorityFeePerGas: 5.GWei, maxFeePerGas: 5.GWei);
            _txPool.SubmitTx(richer, TxHandlingOptions.None);

            await RaiseBlockAddedToMainAndWaitForNewHead(Build.A.Block.WithNumber(2).WithTimestamp(1_000).TestObject);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(1));
                Assert.That(_txPool.TryGetPendingTransaction(richer.Hash!, out _), Is.True, "the higher priority fee keeps its slot");
            }
        }

        [Test]
        public async Task Shedding_takes_the_nearest_deadline_first_and_stops_at_the_freed_slot()
        {
            // Only as many as the pressure needs, in the spec's order: the later deadline keeps its place.
            _txPool = CreatePool(new TxPoolConfig { Size = 2 }, new TestSpecProvider(Eip8141Prototype.Instance));
            EnsureSenderBalance(TestItem.PrivateKeyA.Address, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.PrivateKeyB.Address, UInt256.MaxValue);

            await RaiseBlockAddedToMainAndWaitForNewHead(Build.A.Block.WithNumber(1).WithTimestamp(1_000).TestObject);
            _txPool.SubmitTx(BuildFrameTx(nonce: 0, TestItem.PrivateKeyA.Address, deadline: 1_005), TxHandlingOptions.None);
            Transaction later = BuildFrameTx(nonce: 0, TestItem.PrivateKeyB.Address, deadline: 1_015);
            _txPool.SubmitTx(later, TxHandlingOptions.None);

            await RaiseBlockAddedToMainAndWaitForNewHead(Build.A.Block.WithNumber(2).WithTimestamp(1_000).TestObject);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(1));
                Assert.That(_txPool.TryGetPendingTransaction(later.Hash!, out _), Is.True, "the later deadline keeps its slot");
            }
        }

        [TestCase(1, true, TestName = "a full pool sheds the frame tx closest to expiry")]
        [TestCase(4, false, TestName = "a pool with room keeps it")]
        public async Task Nearly_expired_frame_transaction_is_shed_only_under_capacity_pressure(int poolSize, bool shed)
        {
            // The spec's second eviction tier: a frame tx with almost no life left yields its slot first.
            _txPool = CreatePool(new TxPoolConfig { Size = poolSize }, new TestSpecProvider(Eip8141Prototype.Instance));
            EnsureSenderBalance(TestItem.PrivateKeyA.Address, UInt256.MaxValue);

            await RaiseBlockAddedToMainAndWaitForNewHead(Build.A.Block.WithNumber(1).WithTimestamp(1_000).TestObject);
            Assert.That(_txPool.SubmitTx(BuildFrameTx(nonce: 0, TestItem.PrivateKeyA.Address, deadline: 1_010), TxHandlingOptions.None),
                Is.EqualTo(AcceptTxResult.Accepted));

            await RaiseBlockAddedToMainAndWaitForNewHead(Build.A.Block.WithNumber(2).WithTimestamp(1_000).TestObject);

            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(shed ? 0 : 1));
        }

        [Test]
        public async Task Shedding_leaves_the_transaction_resubmittable()
        {
            // Capacity pressure decided the shed, not expiry, so the transaction is still includable.
            _txPool = CreatePool(new TxPoolConfig { Size = 1 }, new TestSpecProvider(Eip8141Prototype.Instance));
            EnsureSenderBalance(TestItem.PrivateKeyA.Address, UInt256.MaxValue);

            await RaiseBlockAddedToMainAndWaitForNewHead(Build.A.Block.WithNumber(1).WithTimestamp(1_000).TestObject);
            _txPool.SubmitTx(BuildFrameTx(nonce: 0, TestItem.PrivateKeyA.Address, deadline: 1_010), TxHandlingOptions.None);

            await RaiseBlockAddedToMainAndWaitForNewHead(Build.A.Block.WithNumber(2).WithTimestamp(1_000).TestObject);
            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(0), "the transaction must have been shed");

            Assert.That(_txPool.SubmitTx(BuildFrameTx(nonce: 0, TestItem.PrivateKeyA.Address, deadline: 1_010), TxHandlingOptions.None),
                Is.EqualTo(AcceptTxResult.Accepted));
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
            simulator.Simulate(Arg.Any<Transaction>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>()).Returns(FrameTxSimulationResult.Accept(sponsor));
            // The verify-gas bound is out of scope here; disable it so the exposure gate is what binds.
            _txPool = CreatePool(new TxPoolConfig { FrameTxMaxVerifyGas = 0 }, new TestSpecProvider(Eip8141Prototype.Instance), frameTxPrefixSimulator: simulator);

            EnsureSenderBalance(TestItem.PrivateKeyA.Address, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.PrivateKeyB.Address, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.PrivateKeyC.Address, UInt256.MaxValue);

            Transaction first = SponsoredFrameTx(TestItem.PrivateKeyA, TestItem.PrivateKeyD);
            Transaction second = SponsoredFrameTx(TestItem.PrivateKeyB, TestItem.PrivateKeyD);
            Transaction third = SponsoredFrameTx(TestItem.PrivateKeyC, TestItem.PrivateKeyD);

            Assert.That(FrameTxValidation.TryCalculateMaxCost(first, Eip8141Prototype.Instance, out UInt256 maxCost), Is.True);
            EnsureSenderBalance(sponsor, maxCost + maxCost / 2); // fits one tx, not two

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
        public async Task Frame_transaction_is_evicted_when_its_prefix_stops_validating_against_the_new_head()
        {
            IFrameTxPrefixSimulator simulator = Substitute.For<IFrameTxPrefixSimulator>();
            simulator.Simulate(Arg.Any<Transaction>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>()).Returns(FrameTxSimulationResult.Accept(TestItem.AddressD));
            _txPool = CreatePool(new TxPoolConfig { FrameTxMaxVerifyGas = 0 }, new TestSpecProvider(Eip8141Prototype.Instance), frameTxPrefixSimulator: simulator);
            EnsureSenderBalance(TestItem.PrivateKeyA.Address, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.AddressD, UInt256.MaxValue);

            Assert.That(_txPool.SubmitTx(SponsoredFrameTx(TestItem.PrivateKeyA, TestItem.PrivateKeyD), TxHandlingOptions.None),
                Is.EqualTo(AcceptTxResult.Accepted));

            simulator.Simulate(Arg.Any<Transaction>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>()).Returns(FrameTxSimulationResult.Reject("prefix reverts"));
            await RaiseBlockAddedToMainAndWaitForNewHead(Build.A.Block.WithNumber(1).TestObject);

            Assert.That(_txPool.GetPendingTransactionsCount(), Is.Zero);
        }

        [Test]
        public async Task Revalidation_evicts_a_transaction_whose_payer_moved()
        {
            // The payer is never rewritten in place: RemoveTransaction runs without the head lock, so a removal
            // landing between the payer and exposure writes would release the wrong figure from the wrong payer.
            IFrameTxPrefixSimulator simulator = Substitute.For<IFrameTxPrefixSimulator>();
            simulator.Simulate(Arg.Any<Transaction>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>()).Returns(FrameTxSimulationResult.Accept(TestItem.AddressD));
            _txPool = CreatePool(new TxPoolConfig { FrameTxMaxVerifyGas = 0 }, new TestSpecProvider(Eip8141Prototype.Instance), frameTxPrefixSimulator: simulator);
            EnsureSenderBalance(TestItem.PrivateKeyA.Address, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.PrivateKeyB.Address, UInt256.MaxValue);
            // Solvent, so an in-place move would succeed and keep the transaction: eviction is the policy
            // under test, not a reservation that happened to fail.
            EnsureSenderBalance(TestItem.AddressF, UInt256.MaxValue);

            Transaction tx = SponsoredFrameTx(TestItem.PrivateKeyA, TestItem.PrivateKeyD);
            Transaction next = SponsoredFrameTx(TestItem.PrivateKeyB, TestItem.PrivateKeyD);
            // Exactly one transaction's worth, so the eviction must have released D for the next to be admitted.
            Assert.That(FrameTxValidation.TryCalculateMaxCost(next, Eip8141Prototype.Instance, out UInt256 oneTx), Is.True);
            EnsureSenderBalance(TestItem.AddressD, oneTx);

            Assert.That(_txPool.SubmitTx(tx, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));

            simulator.Simulate(Arg.Any<Transaction>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>()).Returns(FrameTxSimulationResult.Accept(TestItem.AddressF));
            await RaiseBlockAddedToMainAndWaitForNewHead(Build.A.Block.WithNumber(1).TestObject);

            Assert.That(_txPool.GetPendingTransactionsCount(), Is.Zero, "a moved payer evicts rather than rewrites");

            // Back to the original sponsor, so the follow-up measures D's ledger rather than F's empty balance.
            simulator.Simulate(Arg.Any<Transaction>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>()).Returns(FrameTxSimulationResult.Accept(TestItem.AddressD));
            Assert.That(_txPool.SubmitTx(next, TxHandlingOptions.None),
                Is.EqualTo(AcceptTxResult.Accepted), "the eviction must have released the original payer");
        }

        [Test]
        public async Task Revalidation_leaves_an_unresolved_payer_as_admitted()
        {
            // Admitted without a payer it holds no reservation, so there is nothing to move and nothing unsafe
            // about leaving it: evicting would drop a transaction that has become better attributed, not worse.
            IFrameTxPrefixSimulator simulator = Substitute.For<IFrameTxPrefixSimulator>();
            simulator.Simulate(Arg.Any<Transaction>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>()).Returns(FrameTxSimulationResult.Undecided("simulator unavailable"));
            _txPool = CreatePool(new TxPoolConfig { FrameTxMaxVerifyGas = 0 }, new TestSpecProvider(Eip8141Prototype.Instance), frameTxPrefixSimulator: simulator);
            EnsureSenderBalance(TestItem.PrivateKeyA.Address, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.AddressD, UInt256.MaxValue);

            Transaction tx = SponsoredFrameTx(TestItem.PrivateKeyA, TestItem.PrivateKeyD);
            Assert.That(_txPool.SubmitTx(tx, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));

            simulator.Simulate(Arg.Any<Transaction>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>()).Returns(FrameTxSimulationResult.Accept(TestItem.AddressF));
            Block first = Build.A.Block.WithNumber(1).TestObject;
            await RaiseBlockAddedToMainAndWaitForNewHead(first);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(1));
                Assert.That(tx.PayerAddress, Is.Null, "the record is left exactly as admission wrote it");
            }

            // The payer was indexed even though it was not recorded, so a head touching only it revalidates.
            simulator.ClearReceivedCalls();
            simulator.Simulate(Arg.Any<Transaction>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>()).Returns(FrameTxSimulationResult.Reject("the payer revoked its approval"));
            Block second = Build.A.Block.WithNumber(2).WithParent(first).TestObject;
            second.AccountChanges = new ArrayPoolList<AddressAsKey>(1) { TestItem.AddressF };
            await RaiseBlockAddedToMainAndWaitForNewHead(second);

            Assert.That(_txPool.GetPendingTransactionsCount(), Is.Zero, "the resolved payer must be a tracked dependency");
        }

        // Block.AccountChanges is a touched set, so any block running an expiry-bearing frame transaction names
        // the verifier; indexing it would make every such block collect the whole expiring population.
        [Test]
        public async Task Revalidation_ignores_a_block_that_only_touched_the_expiry_verifier()
        {
            IFrameTxPrefixSimulator simulator = Substitute.For<IFrameTxPrefixSimulator>();
            simulator.Simulate(Arg.Any<Transaction>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>()).Returns(FrameTxSimulationResult.Accept(TestItem.AddressD));
            _txPool = CreatePool(new TxPoolConfig { FrameTxMaxVerifyGas = 0 }, new TestSpecProvider(Eip8141Prototype.Instance), frameTxPrefixSimulator: simulator);
            EnsureSenderBalance(TestItem.PrivateKeyA.Address, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.AddressD, UInt256.MaxValue);

            Transaction tx = SponsoredFrameTx(TestItem.PrivateKeyA, TestItem.PrivateKeyD, deadline: 9_000_000);
            Assert.That(_txPool.SubmitTx(tx, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));

            // A sequential baseline first, so the following change list is trusted as complete.
            Block parent = Build.A.Block.WithNumber(1).TestObject;
            parent.AccountChanges = new ArrayPoolList<AddressAsKey>(1) { TestItem.AddressF };
            await RaiseBlockAddedToMainAndWaitForNewHead(parent);
            simulator.ClearReceivedCalls();

            simulator.Simulate(Arg.Any<Transaction>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>()).Returns(FrameTxSimulationResult.Reject("prefix reverts"));
            Block block = Build.A.Block.WithNumber(2).WithParent(parent).TestObject;
            block.AccountChanges = new ArrayPoolList<AddressAsKey>(1) { Eip8141Constants.ExpiryVerifierAddress };
            await RaiseBlockAddedToMainAndWaitForNewHead(block);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(1), "the verifier is not a tracked dependency");
                simulator.DidNotReceive().Simulate(Arg.Any<Transaction>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>());
            }
        }

        [Test]
        public async Task Revalidation_timed_out_by_the_prefix_is_not_requeued()
        {
            // A timeout is the prefix's own wall clock, not a bound this node spent, so re-queueing it would
            // have it reclaim the per-head budget on every head with nothing to break the loop.
            IFrameTxPrefixSimulator simulator = Substitute.For<IFrameTxPrefixSimulator>();
            simulator.Simulate(Arg.Any<Transaction>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>()).Returns(FrameTxSimulationResult.Accept(TestItem.AddressD));
            _txPool = CreatePool(new TxPoolConfig { FrameTxMaxVerifyGas = 0 }, new TestSpecProvider(Eip8141Prototype.Instance), frameTxPrefixSimulator: simulator);
            EnsureSenderBalance(TestItem.PrivateKeyA.Address, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.AddressD, UInt256.MaxValue);

            _txPool.SubmitTx(SponsoredFrameTx(TestItem.PrivateKeyA, TestItem.PrivateKeyD), TxHandlingOptions.None);

            simulator.Simulate(Arg.Any<Transaction>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>()).Returns(FrameTxSimulationResult.RejectTimedOut("timed out"));
            Block first = Build.A.Block.WithNumber(1).TestObject;
            await RaiseBlockAddedToMainAndWaitForNewHead(first);
            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(1), "a timeout must not evict");

            // A head touching nothing it depends on, so only a carried deferral could bring it back.
            simulator.ClearReceivedCalls();
            Block second = Build.A.Block.WithNumber(2).WithParent(first).TestObject;
            second.AccountChanges = new ArrayPoolList<AddressAsKey>(1) { TestItem.AddressF };
            await RaiseBlockAddedToMainAndWaitForNewHead(second);

            simulator.DidNotReceive().Simulate(Arg.Any<Transaction>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>());
        }

        [Test]
        public async Task Revalidation_deferred_by_an_admission_bound_is_retried_on_a_later_head()
        {
            // A bound this node spent judges nothing, so the transaction has to stay queued: a one-off
            // change leaves no later head whose change list would mention its dependencies again.
            IFrameTxPrefixSimulator simulator = Substitute.For<IFrameTxPrefixSimulator>();
            simulator.Simulate(Arg.Any<Transaction>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>()).Returns(FrameTxSimulationResult.Accept(TestItem.AddressD));
            _txPool = CreatePool(new TxPoolConfig { FrameTxMaxVerifyGas = 0 }, new TestSpecProvider(Eip8141Prototype.Instance), frameTxPrefixSimulator: simulator);
            EnsureSenderBalance(TestItem.PrivateKeyA.Address, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.AddressD, UInt256.MaxValue);

            _txPool.SubmitTx(SponsoredFrameTx(TestItem.PrivateKeyA, TestItem.PrivateKeyD), TxHandlingOptions.None);

            simulator.Simulate(Arg.Any<Transaction>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>()).Returns(FrameTxSimulationResult.RejectIndeterminate("budget exhausted"));
            Block first = Build.A.Block.WithNumber(1).TestObject;
            await RaiseBlockAddedToMainAndWaitForNewHead(first);
            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(1), "an admission bound must not evict");

            // The next head touches nothing this transaction depends on, so only the carried-forward
            // deferral can bring it back to the simulator.
            simulator.Simulate(Arg.Any<Transaction>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>()).Returns(FrameTxSimulationResult.Reject("prefix reverts"));
            Block second = Build.A.Block.WithNumber(2).WithParent(first).TestObject;
            second.AccountChanges = new ArrayPoolList<AddressAsKey>(1) { TestItem.AddressF };
            await RaiseBlockAddedToMainAndWaitForNewHead(second);

            Assert.That(_txPool.GetPendingTransactionsCount(), Is.Zero, "the deferred revalidation must be retried");
        }

        [Test]
        public async Task Frame_transaction_survives_a_simulation_that_failed_on_a_resource_bound()
        {
            // An exhausted budget says nothing about validity, so it must not turn into a mass eviction.
            IFrameTxPrefixSimulator simulator = Substitute.For<IFrameTxPrefixSimulator>();
            simulator.Simulate(Arg.Any<Transaction>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>()).Returns(FrameTxSimulationResult.Accept(TestItem.AddressD));
            _txPool = CreatePool(new TxPoolConfig { FrameTxMaxVerifyGas = 0 }, new TestSpecProvider(Eip8141Prototype.Instance), frameTxPrefixSimulator: simulator);
            EnsureSenderBalance(TestItem.PrivateKeyA.Address, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.AddressD, UInt256.MaxValue);

            _txPool.SubmitTx(SponsoredFrameTx(TestItem.PrivateKeyA, TestItem.PrivateKeyD), TxHandlingOptions.None);

            simulator.Simulate(Arg.Any<Transaction>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>()).Returns(FrameTxSimulationResult.RejectIndeterminate("budget exhausted"));
            await RaiseBlockAddedToMainAndWaitForNewHead(Build.A.Block.WithNumber(1).TestObject);

            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(1));
        }

        [Test]
        public async Task Revalidation_eviction_releases_the_payer_reservation()
        {
            // A leaked reservation would be permanent: the sponsor could never fund another frame tx.
            IFrameTxPrefixSimulator simulator = Substitute.For<IFrameTxPrefixSimulator>();
            simulator.Simulate(Arg.Any<Transaction>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>()).Returns(FrameTxSimulationResult.Accept(TestItem.AddressD));
            _txPool = CreatePool(new TxPoolConfig { FrameTxMaxVerifyGas = 0 }, new TestSpecProvider(Eip8141Prototype.Instance), frameTxPrefixSimulator: simulator);
            EnsureSenderBalance(TestItem.PrivateKeyA.Address, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.PrivateKeyB.Address, UInt256.MaxValue);

            Transaction evicted = SponsoredFrameTx(TestItem.PrivateKeyA, TestItem.PrivateKeyD);
            Transaction next = SponsoredFrameTx(TestItem.PrivateKeyB, TestItem.PrivateKeyD);
            // Exactly one transaction's worth, or a leaked reservation could not refuse the second one.
            Assert.That(FrameTxValidation.TryCalculateMaxCost(next, Eip8141Prototype.Instance, out UInt256 oneTx), Is.True);
            EnsureSenderBalance(TestItem.AddressD, oneTx);

            _txPool.SubmitTx(evicted, TxHandlingOptions.None);

            simulator.Simulate(Arg.Any<Transaction>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>()).Returns(FrameTxSimulationResult.Reject("prefix reverts"));
            await RaiseBlockAddedToMainAndWaitForNewHead(Build.A.Block.WithNumber(1).TestObject);

            simulator.Simulate(Arg.Any<Transaction>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>()).Returns(FrameTxSimulationResult.Accept(TestItem.AddressD));
            Assert.That(_txPool.SubmitTx(next, TxHandlingOptions.None),
                Is.EqualTo(AcceptTxResult.Accepted), "the evicted transaction must have released its sponsor reservation");
        }

        [Test]
        public async Task Revalidation_eviction_leaves_the_transaction_resubmittable()
        {
            // Unlike expiry, invalidity against a head reverses, so the hash must not stay in the cache.
            IFrameTxPrefixSimulator simulator = Substitute.For<IFrameTxPrefixSimulator>();
            simulator.Simulate(Arg.Any<Transaction>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>()).Returns(FrameTxSimulationResult.Accept(TestItem.AddressD));
            _txPool = CreatePool(new TxPoolConfig { FrameTxMaxVerifyGas = 0 }, new TestSpecProvider(Eip8141Prototype.Instance), frameTxPrefixSimulator: simulator);
            EnsureSenderBalance(TestItem.PrivateKeyA.Address, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.AddressD, UInt256.MaxValue);

            _txPool.SubmitTx(SponsoredFrameTx(TestItem.PrivateKeyA, TestItem.PrivateKeyD), TxHandlingOptions.None);

            simulator.Simulate(Arg.Any<Transaction>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>()).Returns(FrameTxSimulationResult.Reject("payer over its exposure"));
            await RaiseBlockAddedToMainAndWaitForNewHead(Build.A.Block.WithNumber(1).TestObject);

            simulator.Simulate(Arg.Any<Transaction>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>()).Returns(FrameTxSimulationResult.Accept(TestItem.AddressD));
            Assert.That(_txPool.SubmitTx(SponsoredFrameTx(TestItem.PrivateKeyA, TestItem.PrivateKeyD), TxHandlingOptions.None),
                Is.EqualTo(AcceptTxResult.Accepted), "the same transaction must be admissible once its payer is solvent again");
        }

        [Test]
        public async Task Revalidation_tracks_a_delegation_installed_after_admission()
        {
            // The delegate is a head-state snapshot, so a sender that delegates after admission must be
            // re-indexed or the account whose code its prefix runs stops being watched.
            IFrameTxPrefixSimulator simulator = Substitute.For<IFrameTxPrefixSimulator>();
            simulator.Simulate(Arg.Any<Transaction>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>()).Returns(FrameTxSimulationResult.Accept(TestItem.AddressD));
            _txPool = CreatePool(new TxPoolConfig { FrameTxMaxVerifyGas = 0 }, new TestSpecProvider(Eip8141Prototype.Instance), frameTxPrefixSimulator: simulator);
            EnsureSenderBalance(TestItem.PrivateKeyA.Address, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.AddressD, UInt256.MaxValue);

            _txPool.SubmitTx(SponsoredFrameTx(TestItem.PrivateKeyA, TestItem.PrivateKeyD), TxHandlingOptions.None);

            byte[] delegation = [.. Eip7702Constants.DelegationHeader, .. TestItem.AddressC.Bytes];
            _stateProvider.InsertCode(TestItem.PrivateKeyA.Address, delegation, Eip8141Prototype.Instance);
            Block delegating = Build.A.Block.WithNumber(1).TestObject;
            delegating.AccountChanges = new ArrayPoolList<AddressAsKey>(1) { TestItem.PrivateKeyA.Address };
            await RaiseBlockAddedToMainAndWaitForNewHead(delegating);
            simulator.ClearReceivedCalls();

            // Only the delegate moves now: without the re-index the transaction would not be revalidated.
            simulator.Simulate(Arg.Any<Transaction>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>()).Returns(FrameTxSimulationResult.Reject("delegate code changed"));
            Block delegateChanged = Build.A.Block.WithNumber(2).WithParent(delegating).TestObject;
            delegateChanged.AccountChanges = new ArrayPoolList<AddressAsKey>(1) { TestItem.AddressC };
            await RaiseBlockAddedToMainAndWaitForNewHead(delegateChanged);

            Assert.That(_txPool.GetPendingTransactionsCount(), Is.Zero);
        }

        [Test]
        public async Task Reorg_revalidates_frame_transactions_its_change_list_does_not_mention()
        {
            // A reorg reports the new branch's changes but not what the abandoned one reverted.
            IFrameTxPrefixSimulator simulator = Substitute.For<IFrameTxPrefixSimulator>();
            simulator.Simulate(Arg.Any<Transaction>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>()).Returns(FrameTxSimulationResult.Accept(TestItem.AddressD));
            _txPool = CreatePool(new TxPoolConfig { FrameTxMaxVerifyGas = 0 }, new TestSpecProvider(Eip8141Prototype.Instance), frameTxPrefixSimulator: simulator);
            EnsureSenderBalance(TestItem.PrivateKeyA.Address, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.AddressD, UInt256.MaxValue);

            _txPool.SubmitTx(SponsoredFrameTx(TestItem.PrivateKeyA, TestItem.PrivateKeyD), TxHandlingOptions.None);

            simulator.Simulate(Arg.Any<Transaction>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>()).Returns(FrameTxSimulationResult.Reject("prefix reverts"));
            Block block = Build.A.Block.WithNumber(1).TestObject;
            block.AccountChanges = new ArrayPoolList<AddressAsKey>(1) { TestItem.AddressF };
            await RaiseBlockAddedToMainAndWaitForNewHead(block, Build.A.Block.WithNumber(1).TestObject);

            Assert.That(_txPool.GetPendingTransactionsCount(), Is.Zero);
        }

        [Test]
        public async Task Frame_transaction_is_not_revalidated_when_the_block_touched_no_tracked_dependency()
        {
            IFrameTxPrefixSimulator simulator = Substitute.For<IFrameTxPrefixSimulator>();
            simulator.Simulate(Arg.Any<Transaction>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>()).Returns(FrameTxSimulationResult.Accept(TestItem.AddressD));
            _txPool = CreatePool(new TxPoolConfig { FrameTxMaxVerifyGas = 0 }, new TestSpecProvider(Eip8141Prototype.Instance), frameTxPrefixSimulator: simulator);
            EnsureSenderBalance(TestItem.PrivateKeyA.Address, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.AddressD, UInt256.MaxValue);

            _txPool.SubmitTx(SponsoredFrameTx(TestItem.PrivateKeyA, TestItem.PrivateKeyD), TxHandlingOptions.None);

            // A change list is trusted to describe everything that moved only after a sequential baseline.
            Block parent = Build.A.Block.WithNumber(1).TestObject;
            parent.AccountChanges = new ArrayPoolList<AddressAsKey>(1) { TestItem.AddressF };
            await RaiseBlockAddedToMainAndWaitForNewHead(parent);
            simulator.ClearReceivedCalls();

            Block block = Build.A.Block.WithNumber(2).WithParent(parent).TestObject;
            block.AccountChanges = new ArrayPoolList<AddressAsKey>(1) { TestItem.AddressF };
            await RaiseBlockAddedToMainAndWaitForNewHead(block);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(1));
                simulator.DidNotReceive().Simulate(Arg.Any<Transaction>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>());
            }
        }

        [Test]
        public void SubmitTx_FrameTransactions_SharingNonCanonicalPaymaster_BoundByPendingCap_ReleasedOnRemoval()
        {
            // Distinct senders share one code-carrying pay target, so the non-canonical paymaster cap
            // bounds how many of its sponsored transactions may be pending at once.
            IFrameTxPrefixSimulator simulator = Substitute.For<IFrameTxPrefixSimulator>();
            simulator.Simulate(Arg.Any<Transaction>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>()).Returns(FrameTxSimulationResult.Accept(TestItem.AddressD));
            _txPool = CreatePool(new TxPoolConfig { FrameTxMaxVerifyGas = 0 }, new TestSpecProvider(Eip8141Prototype.Instance), frameTxPrefixSimulator: simulator);

            EnsureSenderBalance(TestItem.PrivateKeyA.Address, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.PrivateKeyB.Address, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.PrivateKeyC.Address, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.AddressD, UInt256.MaxValue);
            _stateProvider.InsertCode([0x60, 0x00], TestItem.AddressD);

            Transaction first = SponsoredFrameTx(TestItem.PrivateKeyA, TestItem.PrivateKeyD);
            Transaction second = SponsoredFrameTx(TestItem.PrivateKeyB, TestItem.PrivateKeyD);
            Transaction third = SponsoredFrameTx(TestItem.PrivateKeyC, TestItem.PrivateKeyD);

            AcceptTxResult firstResult = _txPool.SubmitTx(first, TxHandlingOptions.PersistentBroadcast);
            AcceptTxResult secondResult = _txPool.SubmitTx(second, TxHandlingOptions.PersistentBroadcast);

            _txPool.RemoveTransaction(first.Hash);
            AcceptTxResult thirdResult = _txPool.SubmitTx(third, TxHandlingOptions.PersistentBroadcast);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(firstResult, Is.EqualTo(AcceptTxResult.Accepted));
                Assert.That(secondResult, Is.EqualTo(AcceptTxResult.NonCanonicalPaymasterLimitReached));
                Assert.That(thirdResult, Is.EqualTo(AcceptTxResult.Accepted), "removing the first tx freed the paymaster's slot");
            }
        }

        [Test]
        public void SubmitTx_ConcurrentFrameTransactions_SharingNonCanonicalPaymaster_AdmitsOnlyTheCap()
        {
            // Reading the count and then inserting would let every submission observe the same free slot,
            // leaving the sponsor over its cap for as long as the transactions stay pending.
            IFrameTxPrefixSimulator simulator = Substitute.For<IFrameTxPrefixSimulator>();
            simulator.Simulate(Arg.Any<Transaction>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>()).Returns(FrameTxSimulationResult.Accept(TestItem.AddressD));
            _txPool = CreatePool(new TxPoolConfig { FrameTxMaxVerifyGas = 0 }, new TestSpecProvider(Eip8141Prototype.Instance), frameTxPrefixSimulator: simulator);

            PrivateKey[] senders = [TestItem.PrivateKeyA, TestItem.PrivateKeyB, TestItem.PrivateKeyC, TestItem.PrivateKeyE, TestItem.PrivateKeyF];
            foreach (PrivateKey sender in senders)
            {
                EnsureSenderBalance(sender.Address, UInt256.MaxValue);
            }

            EnsureSenderBalance(TestItem.AddressD, UInt256.MaxValue);
            _stateProvider.InsertCode([0x60, 0x00], TestItem.AddressD);

            Transaction[] sponsored = [.. senders.Select(sender => SponsoredFrameTx(sender, TestItem.PrivateKeyD))];
            AcceptTxResult[] results = new AcceptTxResult[sponsored.Length];

            Parallel.For(0, sponsored.Length, i => results[i] = _txPool.SubmitTx(sponsored[i], TxHandlingOptions.PersistentBroadcast));

            Assert.That(results.Count(static result => result == AcceptTxResult.Accepted),
                Is.EqualTo(Eip8141Constants.MaxPendingTxsUsingNonCanonicalPaymaster),
                "concurrent submissions must not admit more than the cap");
        }

        [Test]
        public void SubmitTx_FrameTransaction_RejectedAfterTheCapIsCounted_ReleasesThePaymasterSlot()
        {
            // The cap counts ahead of the filters that resolve the payer, so a rejection there must hand the
            // slot back or the sponsor is locked out for the life of the pool.
            IFrameTxPrefixSimulator simulator = Substitute.For<IFrameTxPrefixSimulator>();
            simulator.Simulate(Arg.Any<Transaction>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>()).Returns(FrameTxSimulationResult.Reject("declined"));
            _txPool = CreatePool(new TxPoolConfig { FrameTxMaxVerifyGas = 0 }, new TestSpecProvider(Eip8141Prototype.Instance), frameTxPrefixSimulator: simulator);

            EnsureSenderBalance(TestItem.PrivateKeyA.Address, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.PrivateKeyB.Address, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.AddressD, UInt256.MaxValue);
            _stateProvider.InsertCode([0x60, 0x00], TestItem.AddressD);

            AcceptTxResult rejected = _txPool.SubmitTx(SponsoredFrameTx(TestItem.PrivateKeyA, TestItem.PrivateKeyD), TxHandlingOptions.PersistentBroadcast);

            simulator.Simulate(Arg.Any<Transaction>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>()).Returns(FrameTxSimulationResult.Accept(TestItem.AddressD));
            AcceptTxResult afterRelease = _txPool.SubmitTx(SponsoredFrameTx(TestItem.PrivateKeyB, TestItem.PrivateKeyD), TxHandlingOptions.PersistentBroadcast);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(rejected, Is.Not.EqualTo(AcceptTxResult.Accepted));
                Assert.That(afterRelease, Is.EqualTo(AcceptTxResult.Accepted), "the refused transaction must have released the slot it counted");
            }
        }

        [Test]
        public async Task Frame_transaction_rejected_after_the_cap_gate_does_not_hold_the_sponsor_slot()
        {
            // The slot is a reservation over pending transactions, so it must not be taken by a submission
            // that is still going to be rejected: at a cap of one, that would let unpooled traffic naming a
            // sponsor deny the sponsor's real transaction for as long as the remaining filters run.
            Address sponsor = TestItem.PrivateKeyD.Address;
            using ManualResetEventSlim reachedFilter = new(false);
            using ManualResetEventSlim releaseFilter = new(false);

            Transaction doomed = SponsoredFrameTx(TestItem.PrivateKeyA, TestItem.PrivateKeyD);
            BlockingRejectFilter blocker = new(() => doomed.Hash, reachedFilter, releaseFilter);

            _txPool = CreatePool(new TxPoolConfig { FrameTxMaxVerifyGas = 0 }, new TestSpecProvider(Eip8141Prototype.Instance), incomingTxFilter: blocker);
            EnsureSenderBalance(TestItem.PrivateKeyA.Address, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.PrivateKeyB.Address, UInt256.MaxValue);
            EnsureSenderBalance(sponsor, UInt256.MaxValue);
            _stateProvider.InsertCode([0x60, 0x00], sponsor);

            Task<AcceptTxResult> doomedResult = Task.Run(() => _txPool.SubmitTx(doomed, TxHandlingOptions.None));
            Assert.That(reachedFilter.Wait(TimeSpan.FromSeconds(10)), Is.True, "the doomed submission never reached the injected filter");

            // Submitted while the doomed one is parked past the cap gate and has not been rejected yet.
            AcceptTxResult sponsored = _txPool.SubmitTx(SponsoredFrameTx(TestItem.PrivateKeyB, TestItem.PrivateKeyD), TxHandlingOptions.None);

            releaseFilter.Set();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(sponsored, Is.EqualTo(AcceptTxResult.Accepted), "a submission that never pools must not occupy the sponsor's slot");
                Assert.That(await doomedResult, Is.EqualTo(AcceptTxResult.Invalid));
            }
        }

        /// <summary>Parks one transaction inside the filter chain, then rejects it.</summary>
        private sealed class BlockingRejectFilter(
            Func<Hash256> target,
            ManualResetEventSlim reached,
            ManualResetEventSlim release) : IIncomingTxFilter
        {
            public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
            {
                if (tx.Hash != target()) return AcceptTxResult.Accepted;

                reached.Set();
                release.Wait(TimeSpan.FromSeconds(10));
                return AcceptTxResult.Invalid;
            }
        }

        [Test]
        public void Frame_transaction_prefix_simulation_is_told_the_signatures_are_already_verified()
        {
            // Pins the guarantee, not the registration order: whatever the chain looks like, the prefix
            // may only be told "pre-validated" when the signature filter has actually accepted this tx.
            IFrameTxPrefixSimulator simulator = Substitute.For<IFrameTxPrefixSimulator>();
            simulator.Simulate(Arg.Any<Transaction>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>())
                .Returns(FrameTxSimulationResult.Accept(TestItem.AddressD));
            // The verify-gas bound is out of scope here; disable it so the tx reaches the simulation filter.
            _txPool = CreatePool(new TxPoolConfig { FrameTxMaxVerifyGas = 0 }, new TestSpecProvider(Eip8141Prototype.Instance), frameTxPrefixSimulator: simulator);

            EnsureSenderBalance(TestItem.PrivateKeyA.Address, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.AddressD, UInt256.MaxValue);

            Transaction tx = SponsoredFrameTx(TestItem.PrivateKeyA, TestItem.PrivateKeyD);
            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
                simulator.Received(1).Simulate(tx, signaturesPreValidated: true, token: Arg.Any<CancellationToken>(), local: Arg.Any<bool>());
            }
        }

        [Test]
        public void Frame_transaction_payer_reservation_is_taken_through_the_pool_and_released_on_removal()
        {
            // BalanceTooLowFilter sums only nonces below tx.Nonce, so a same-nonce replacement is the one
            // shape reaching the exposure gate here.
            _txPool = CreatePool(null, new TestSpecProvider(Eip8141Prototype.Instance));

            Transaction first = SelfPayingFrameTx(nonce: 0, feePerGas: 6);
            Transaction bumped = SelfPayingFrameTx(nonce: 0, feePerGas: 7);
            Transaction afterRelease = SelfPayingFrameTx(nonce: 0, feePerGas: 8);

            // Priced with the gate's own helper: enough for either transaction alone, never for both.
            Assert.That(FrameTxValidation.TryCalculateMaxCost(first, Eip8141Prototype.Instance, out UInt256 firstCost), Is.True);
            Assert.That(FrameTxValidation.TryCalculateMaxCost(bumped, Eip8141Prototype.Instance, out UInt256 bumpedCost), Is.True);
            EnsureSenderBalance(TestItem.PrivateKeyA.Address, firstCost + bumpedCost - 1);

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
            Assert.That(FrameTxValidation.TryCalculateMaxCost(SelfPayingFrameTx(nonce: 0, feePerGas: 1), Eip8141Prototype.Instance, out UInt256 unitCost), Is.True);
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

        [Test]
        public void Frame_transaction_replacement_releases_only_the_reservation_it_displaced()
        {
            // Admission adds the bump on top of the incumbent's reservation and leaves the displacement to
            // the pool, so the ledger settles only once the replaced transaction's removal releases it.
            _txPool = CreatePool(null, new TestSpecProvider(Eip8141Prototype.Instance));

            // Priced from the transactions themselves rather than scaled off one of them: the reservation
            // is not linear in the fee, and a balance guessed from a unit cost would not sit on the bound.
            UInt256 balance = MaxCostOf(SelfPayingFrameTx(nonce: 0, feePerGas: 3)) + MaxCostOf(SelfPayingFrameTx(nonce: 1, feePerGas: 2));
            EnsureSenderBalance(TestItem.PrivateKeyA.Address, balance);

            Assert.That(_txPool.SubmitTx(SelfPayingFrameTx(nonce: 0, feePerGas: 2), TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
            AcceptTxResult bump = _txPool.SubmitTx(SelfPayingFrameTx(nonce: 0, feePerGas: 3), TxHandlingOptions.None);

            // The balance leaves room for the bump and one more nonce, and nothing beyond it. Holding the
            // displaced reservation as well would refuse the second nonce.
            AcceptTxResult withinBalance = _txPool.SubmitTx(SelfPayingFrameTx(nonce: 1, feePerGas: 2), TxHandlingOptions.None);
            AcceptTxResult overBalance = _txPool.SubmitTx(SelfPayingFrameTx(nonce: 2, feePerGas: 1), TxHandlingOptions.None);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(bump, Is.EqualTo(AcceptTxResult.Accepted));
                Assert.That(withinBalance, Is.EqualTo(AcceptTxResult.Accepted), "the displaced reservation must have been released");
                Assert.That(overBalance, Is.EqualTo(AcceptTxResult.FrameTxPayerExposureExceeded), "the bound must still bind, or the case above proves nothing");
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(2), "the bump must have displaced the incumbent rather than joined it");
            }
        }

        [Test]
        public void Frame_transaction_replacement_leaves_its_paymaster_holding_exactly_one_slot()
        {
            // The cap counts the bump before the pool displaces the incumbent, so the sponsor is briefly at
            // two. Settling at anything but one locks it out the moment the survivor leaves.
            IFrameTxPrefixSimulator simulator = Substitute.For<IFrameTxPrefixSimulator>();
            simulator.Simulate(Arg.Any<Transaction>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>()).Returns(FrameTxSimulationResult.Accept(TestItem.AddressD));
            _txPool = CreatePool(new TxPoolConfig { FrameTxMaxVerifyGas = 0 }, new TestSpecProvider(Eip8141Prototype.Instance), frameTxPrefixSimulator: simulator);

            EnsureSenderBalance(TestItem.PrivateKeyA.Address, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.PrivateKeyB.Address, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.AddressD, UInt256.MaxValue);
            _stateProvider.InsertCode([0x60, 0x00], TestItem.AddressD);

            Transaction bump = SponsoredFrameTx(TestItem.PrivateKeyA, TestItem.PrivateKeyD, feePerGas: 2.GWei);

            AcceptTxResult admitted = _txPool.SubmitTx(SponsoredFrameTx(TestItem.PrivateKeyA, TestItem.PrivateKeyD), TxHandlingOptions.None);
            AcceptTxResult replaced = _txPool.SubmitTx(bump, TxHandlingOptions.None);
            // One pending, so the sponsor is still at the cap: another sender must be turned away.
            AcceptTxResult whileHeld = _txPool.SubmitTx(SponsoredFrameTx(TestItem.PrivateKeyB, TestItem.PrivateKeyD), TxHandlingOptions.None);

            _txPool.RemoveTransaction(bump.Hash);
            // Repriced so it is a new hash: the one turned away above is remembered as already known.
            AcceptTxResult afterRemoval = _txPool.SubmitTx(SponsoredFrameTx(TestItem.PrivateKeyB, TestItem.PrivateKeyD, feePerGas: 3.GWei), TxHandlingOptions.None);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(admitted, Is.EqualTo(AcceptTxResult.Accepted));
                Assert.That(replaced, Is.EqualTo(AcceptTxResult.Accepted), "a fee bump discounts the slot the incumbent holds");
                Assert.That(whileHeld, Is.EqualTo(AcceptTxResult.NonCanonicalPaymasterLimitReached), "the survivor still owes the sponsor a slot");
                Assert.That(afterRemoval, Is.EqualTo(AcceptTxResult.Accepted), "the displaced transaction must not have kept a slot");
            }
        }

        [Test]
        public async Task Frame_transactions_surviving_a_head_leave_both_ledgers_empty_when_drained()
        {
            // Drives the pool's own bookkeeping check, which walks both ledgers per head and is compiled
            // into debug builds only; the release-observable half is that a drained pool re-admits.
            IFrameTxPrefixSimulator simulator = Substitute.For<IFrameTxPrefixSimulator>();
            simulator.Simulate(Arg.Any<Transaction>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>()).Returns(FrameTxSimulationResult.Accept(TestItem.AddressD));
            _txPool = CreatePool(new TxPoolConfig { FrameTxMaxVerifyGas = 0 }, new TestSpecProvider(Eip8141Prototype.Instance), frameTxPrefixSimulator: simulator);

            EnsureSenderBalance(TestItem.PrivateKeyB.Address, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.AddressD, UInt256.MaxValue);
            _stateProvider.InsertCode([0x60, 0x00], TestItem.AddressD);

            // Exactly one self-paying transaction's worth, so a reservation left behind refuses the next one.
            // The retargeted shape is the dearer of the two, so the plain resubmission below fits it.
            Transaction selfPaying = SelfPayingFrameTx(nonce: 0, feePerGas: 2, distinctHash: true);
            EnsureSenderBalance(TestItem.PrivateKeyA.Address, MaxCostOf(selfPaying));

            Assert.That(_txPool.SubmitTx(selfPaying, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(_txPool.SubmitTx(SponsoredFrameTx(TestItem.PrivateKeyB, TestItem.PrivateKeyD), TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));

            await RaiseBlockAddedToMainAndWaitForNewHead(Build.A.Block.WithNumber(1).TestObject);
            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(2), "both must still be pending, or the check walked an empty pool");

            foreach (Transaction pending in _txPool.GetPendingTransactions())
            {
                _txPool.RemoveTransaction(pending.Hash);
            }

            // Both re-priced or re-shaped, since the pool remembers what it has already seen.
            AcceptTxResult resubmitted = _txPool.SubmitTx(SelfPayingFrameTx(nonce: 0, feePerGas: 2), TxHandlingOptions.None);
            AcceptTxResult responsored = _txPool.SubmitTx(SponsoredFrameTx(TestItem.PrivateKeyB, TestItem.PrivateKeyD, feePerGas: 3.GWei), TxHandlingOptions.None);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(resubmitted, Is.EqualTo(AcceptTxResult.Accepted), "the payer's whole balance is free again");
                Assert.That(responsored, Is.EqualTo(AcceptTxResult.Accepted), "the sponsor's cap slot is free again");
            }
        }

        private static UInt256 MaxCostOf(Transaction tx)
        {
            Assert.That(FrameTxValidation.TryCalculateMaxCost(tx, Eip8141Prototype.Instance, out UInt256 maxCost), Is.True);
            return maxCost;
        }

        /// <summary>A self_verify frame tx the payer resolver settles natively, so the exposure gate sees a payer.</summary>
        private Transaction SelfPayingFrameTx(ulong nonce, uint feePerGas, bool distinctHash = false, UInt256[] nonceKeys = null)
        {
            Transaction tx = BuildFrameTx(nonce, TestItem.PrivateKeyA.Address, deadline: null,
                maxPriorityFeePerGas: feePerGas, maxFeePerGas: feePerGas, nonceKeys: nonceKeys);
            // Naming the sender explicitly is still a self_verify frame, so this varies the hash; the
            // target costs 12 more intrinsic gas, so the retargeted shape reserves slightly more.
            if (distinctHash)
            {
                int i = Array.FindIndex(tx.Frames!, f => f.Flags == TxFrame.ApproveExecutionAndPayment);
                Assert.That(i, Is.GreaterThanOrEqualTo(0), "the helper must still build a self_verify frame to retarget");
                TxFrame frame = tx.Frames![i];
                tx.Frames[i] = new TxFrame(frame.Mode, frame.Flags, TestItem.PrivateKeyA.Address, frame.GasLimit, frame.Value, frame.Data);
            }
            tx.FrameSignatures = [FrameSignature(tx, FrameSignatureDefect.None)];
            // As FrameTxDecoder sets it: the frame-gas sum, so the sender-balance filters price below the
            // payer gate and the exposure bound is what binds.
            ulong frameGas = 0;
            foreach (TxFrame frame in tx.Frames!) frameGas += frame.GasLimit;
            tx.GasLimit = frameGas;
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
        public void Frame_transaction_payer_exposure_prices_the_calldata_its_nonce_keys_occupy()
        {
            // eth_sendTransaction builds the transaction field by field, so it never reaches the decoder that
            // measures the EIP-8250 nonce-key calldata the bound is priced on. Left unmeasured the reservation
            // is systematically below what the transaction costs, which is the bound this gate exists to hold.
            IReleaseSpec spec = new OverridableReleaseSpec(Eip8141Prototype.Instance) { IsEip8250Enabled = true };
            _txPool = CreatePool(null, new TestSpecProvider(spec));
            EnsureSenderBalance(TestItem.PrivateKeyA.Address, UInt256.MaxValue);

            Transaction tx = SelfPayingFrameTx(nonce: 0, feePerGas: 3, nonceKeys: [(UInt256)0xbeef]);
            Assert.That(tx.FrameCalldataStats, Is.EqualTo(default((int ZeroBytes, int NonZeroBytes))), "nothing on this path measured it");
            Assert.That(FrameTxValidation.TryCalculateMaxCost(tx, spec, out UInt256 unmeasuredCost), Is.True);

            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.None);

            Assert.That(FrameTxValidation.TryCalculateMaxCost(tx, spec, out UInt256 measuredCost), Is.True);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
                Assert.That(measuredCost, Is.GreaterThan(unmeasuredCost), "the nonce-key calldata is not free");
                Assert.That(tx.PayerExposure, Is.EqualTo(measuredCost), "the payer is held to the measured cost, not the unmeasured one");
            }
        }

        [Test]
        public void Frame_transaction_payer_exposure_does_not_discount_a_different_keyed_nonce_domain()
        {
            // EIP-8250: a same-nonce transaction in another nonce-key domain does not compete, so both stay
            // pending and the payer owes both. Discounting it would admit exposure beyond the balance.
            _txPool = CreatePool(null, KeyedNonceSpecProvider());

            // The probe carries no nonce keys, so its max cost does not depend on the EIP-8250 surcharge.
            Assert.That(FrameTxValidation.TryCalculateMaxCost(SelfPayingFrameTx(nonce: 0, feePerGas: 1), Eip8141Prototype.Instance, out UInt256 unitCost), Is.True);
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

        [Test]
        public void SubmitTx_FrameTransactions_SharingAPaymasterAcrossKeyedNonceDomains_BothCountAgainstTheCap()
        {
            // EIP-8250: two nonce-key domains at one nonce do not compete, so both stay pending and both
            // owe the paymaster a slot. Discounting one against the other would double the cap per sender.
            IFrameTxPrefixSimulator simulator = Substitute.For<IFrameTxPrefixSimulator>();
            simulator.Simulate(Arg.Any<Transaction>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>()).Returns(FrameTxSimulationResult.Accept(TestItem.AddressD));
            _txPool = CreatePool(new TxPoolConfig { FrameTxMaxVerifyGas = 0 }, KeyedNonceSpecProvider(), frameTxPrefixSimulator: simulator);

            EnsureSenderBalance(TestItem.PrivateKeyA.Address, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.AddressD, UInt256.MaxValue);
            _stateProvider.InsertCode([0x60, 0x00], TestItem.AddressD);

            Transaction first = SponsoredFrameTx(TestItem.PrivateKeyA, TestItem.PrivateKeyD, nonceKeys: [(UInt256)1]);
            Transaction second = SponsoredFrameTx(TestItem.PrivateKeyA, TestItem.PrivateKeyD, nonceKeys: [(UInt256)2]);

            AcceptTxResult firstResult = _txPool.SubmitTx(first, TxHandlingOptions.PersistentBroadcast);
            AcceptTxResult secondResult = _txPool.SubmitTx(second, TxHandlingOptions.PersistentBroadcast);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(firstResult, Is.EqualTo(AcceptTxResult.Accepted));
                Assert.That(secondResult, Is.EqualTo(AcceptTxResult.NonCanonicalPaymasterLimitReached),
                    "the second domain joins the pending set rather than displacing the first");
            }
        }

        // An only_verify|pay prefix naming the sponsor: opaque to native resolution, so it is simulated.
        private Transaction SponsoredFrameTx(PrivateKey senderKey, PrivateKey sponsorKey, UInt256[] nonceKeys = null, ulong? deadline = null, UInt256? feePerGas = null)
        {
            // An expiry verifier frame may appear only as the first frame (EIP-8141 "Expiry Verifier Frame").
            TxFrame[] frames = deadline is null
                ?
                [
                    new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecution, target: null, gasLimit: 100_000, UInt256.Zero, Array.Empty<byte>()),
                    new TxFrame(TxFrame.ModeVerify, TxFrame.ApprovePayment, target: sponsorKey.Address, gasLimit: 0, UInt256.Zero, Array.Empty<byte>()),
                ]
                :
                [
                    FrameTxTestFrames.ExpiryAt(deadline.Value, gasLimit: 50_000),
                    new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecution, target: null, gasLimit: 100_000, UInt256.Zero, Array.Empty<byte>()),
                    new TxFrame(TxFrame.ModeVerify, TxFrame.ApprovePayment, target: sponsorKey.Address, gasLimit: 0, UInt256.Zero, Array.Empty<byte>()),
                ];
            Transaction tx = new()
            {
                Type = TxType.FrameTx,
                ChainId = _specProvider.ChainId,
                Nonce = 0,
                SenderAddress = senderKey.Address,
                NonceKeys = nonceKeys,
                Frames = frames,
                FrameSignatures =
                [
                    new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, signer: null, default, default),
                    new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, signer: sponsorKey.Address, default, default),
                ],
                GasLimit = 1_000_000,
                GasPrice = feePerGas ?? 1.GWei,
                DecodedMaxFeePerGas = feePerGas ?? 1.GWei,
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

        [Test]
        public void SubmitTx_KeyedNonce_DoesNotPipelineTheNextSequence()
        {
            _txPool = CreatePool(null, KeyedNonceSpecProvider());
            Address sender = TestItem.PrivateKeyA.Address;
            EnsureSenderBalance(sender, 100.Ether);

            Transaction current = BuildKeyedFrameTx(sender, nonceKey: 0xbeef, seq: 0, value: UInt256.Zero, maxFee: 1.GWei);
            Assert.That(_txPool.SubmitTx(current, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Accepted));

            Transaction next = BuildKeyedFrameTx(sender, nonceKey: 0xbeef, seq: 1, value: UInt256.Zero, maxFee: 1.GWei);
            AcceptTxResult result = _txPool.SubmitTx(next, TxHandlingOptions.PersistentBroadcast);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.ToString(), Does.Contain(TxPoolErrorMessages.KeyedNonceUnmet),
                    "a keyed sequence past the current one is rejected outright, not queued the way the account nonce lane queues a future successor");
                Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(1),
                    "only the current keyed sequence pends, so the keyed lane admits at most one transaction per key per block");
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

        // The marker decides whether a restart may skip revalidation, so any spec flag that can change a
        // validator verdict must change it too. Swept rather than listed, so a new validator cannot slip past.
        [Test]
        public async Task Spec_change_marker_moves_with_every_spec_flag_the_validator_can_act_on()
        {
            SpecChangeTxValidator validator = new(TestBlockchainIds.ChainId);
            Transaction[] corpus = SpecChangeMarkerCorpus();

            // Every type gate on, or a rule behind one is never reached and its flag looks unguarded-but-harmless.
            ReleaseSpec baseline = SpecChangeMarkerBaseline();
            string baselineMarker = await MarkerFor(baseline);
            string baselineVerdicts = Verdicts(validator, baseline, corpus);

            List<string> unguarded = [];
            foreach (PropertyInfo flag in typeof(ReleaseSpec).GetProperties()
                         .Where(p => p.PropertyType == typeof(bool) && p.CanRead && p.CanWrite))
            {
                ReleaseSpec flipped = SpecChangeMarkerBaseline();
                flag.SetValue(flipped, !(bool)flag.GetValue(baseline)!);

                if (Verdicts(validator, flipped, corpus) != baselineVerdicts
                    && await MarkerFor(flipped) == baselineMarker)
                {
                    unguarded.Add(flag.Name);
                }
            }

            Assert.That(unguarded, Is.Empty,
                $"these flags change a validation verdict without moving the marker, so a restart across them skips revalidation: {string.Join(", ", unguarded)}");
        }

        private static ReleaseSpec SpecChangeMarkerBaseline() => new()
        {
            IsEip1559Enabled = true,
            IsEip2930Enabled = true,
            IsEip4844Enabled = true,
            IsEip7702Enabled = true,
            IsEip8141Enabled = true,
            IsEip8250Enabled = true,
        };

        /// <summary>Transactions spanning the shapes the spec-change validator judges differently.</summary>
        private static Transaction[] SpecChangeMarkerCorpus() =>
        [
            Build.A.Transaction.WithChainId(TestBlockchainIds.ChainId).SignedAndResolved().TestObject,
            Build.A.Transaction.WithType(TxType.EIP1559).WithChainId(TestBlockchainIds.ChainId).SignedAndResolved().TestObject,
            Build.A.Transaction.WithShardBlobTxTypeAndFields().WithChainId(TestBlockchainIds.ChainId).SignedAndResolved().TestObject,
            Build.A.Transaction.WithType(TxType.SetCode).WithChainId(TestBlockchainIds.ChainId).SignedAndResolved().TestObject,
            new Transaction
            {
                Type = TxType.FrameTx,
                ChainId = TestBlockchainIds.ChainId,
                SenderAddress = TestItem.AddressA,
                Frames = [FrameTxTestFrames.SelfVerify(FrameTxTestFrames.PrefixFrameGas)],
                FrameSignatures = [],
                NonceKeys = [UInt256.One],
            },
        ];

        /// <summary>A stable rendering of how <paramref name="validator"/> judges <paramref name="corpus"/>.</summary>
        private static string Verdicts(ITxValidator validator, IReleaseSpec spec, Transaction[] corpus)
        {
            StringBuilder verdicts = new();
            foreach (Transaction tx in corpus)
            {
                try
                {
                    ValidationResult result = validator.IsWellFormed(tx, spec);
                    verdicts.Append(result.AsBool()).Append(':').Append(result.Error).Append('|');
                }
                catch (Exception e)
                {
                    verdicts.Append(e.GetType().Name).Append('|');
                }
            }

            return verdicts.ToString();
        }

        /// <summary>The marker a pool publishes at construction for <paramref name="spec"/>.</summary>
        private async Task<string> MarkerFor(IReleaseSpec spec)
        {
            BlobTxStorage storage = new();
            await using TxPool pool = CreatePool(
                new TxPoolConfig { BlobsSupport = BlobsSupportMode.Storage, PersistentBlobStorageSize = 1 },
                new TestSingleReleaseSpecProvider(spec),
                txStorage: storage);
            return ((ISpecChangeValidationStorage)storage).GetSpecChangeValidationMarker();
        }

        private TxPool CreatePool(
            ITxPoolConfig config = null,
            ISpecProvider specProvider = null,
            ChainHeadInfoProvider chainHeadInfoProvider = null,
            IIncomingTxFilter incomingTxFilter = null,
            IBlobTxStorage txStorage = null,
            bool thereIsPriorityContract = false,
            IEthereumEcdsa ethereumEcdsa = null,
            ITxValidator specChangeTxValidator = null,
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
                specChangeTxValidator ?? new SpecChangeTxValidator(_specProvider.ChainId),
                _logManager,
                transactionComparerProvider.GetDefaultComparer(),
                ShouldGossip.Instance,
                incomingTxFilter is null ? null : [incomingTxFilter],
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
