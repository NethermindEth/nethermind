// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using CkzgLib;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Consensus.Comparers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing.GethStyle.Custom.JavaScript;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.ChainSpecStyle.Json;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.TxPool.Collections;
using NSubstitute;
using NUnit.Framework;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Nethermind.TxPool.Test
{
    [TestFixture]
    public partial class TxPoolTests
    {
        [Test]
        public void should_reject_blob_tx_if_blobs_not_supported([Values(true, false)] bool isBlobSupportEnabled)
        {
            TxPoolConfig txPoolConfig = new() { BlobsSupport = isBlobSupportEnabled ? BlobsSupportMode.InMemory : BlobsSupportMode.Disabled };
            _txPool = CreatePool(txPoolConfig, GetCancunSpecProvider());

            Transaction tx = Build.A.Transaction
                .WithNonce(UInt256.Zero)
                .WithShardBlobTxTypeAndFields()
                .WithMaxFeePerGas(1.GWei())
                .WithMaxPriorityFeePerGas(1.GWei())
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast).Should().Be(isBlobSupportEnabled
                ? AcceptTxResult.Accepted
                : AcceptTxResult.NotSupportedTxType);
        }

        [Test]
        public void should_reject_blob_tx_if_max_size_is_exceeded([Values(true, false)] bool sizeExceeded, [Values(1, 2, 3, 4, 5, 6)] int numberOfBlobs)
        {
            Transaction tx = Build.A.Transaction
                .WithShardBlobTxTypeAndFields(numberOfBlobs)
                .WithMaxPriorityFeePerGas(1.GWei())
                .WithMaxFeePerGas(1.GWei())
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            var txPoolConfig = new TxPoolConfig() { MaxBlobTxSize = tx.GetLength(shouldCountBlobs: false) - (sizeExceeded ? 1 : 0) };
            _txPool = CreatePool(txPoolConfig, GetCancunSpecProvider());

            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            result.Should().Be(sizeExceeded ? AcceptTxResult.MaxTxSizeExceeded : AcceptTxResult.Accepted);
            _txPool.GetPendingBlobTransactionsCount().Should().Be(sizeExceeded ? 0 : 1);
        }

        [Test]
        public void should_calculate_blob_tx_size_properly([Values(1, 2, 3, 4, 5, 6)] int numberOfBlobs)
        {
            Transaction tx = Build.A.Transaction
                .WithShardBlobTxTypeAndFields(numberOfBlobs)
                .WithMaxPriorityFeePerGas(1.GWei())
                .WithMaxFeePerGas(1.GWei())
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            var txPoolConfig = new TxPoolConfig() { MaxBlobTxSize = tx.GetLength(shouldCountBlobs: false) };
            _txPool = CreatePool(txPoolConfig, GetCancunSpecProvider());

            _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast).Should().Be(AcceptTxResult.Accepted);
            _txPool.TryGetPendingBlobTransaction(tx.Hash!, out Transaction blobTx);
            blobTx!.GetLength().Should().BeGreaterThan((int)txPoolConfig.MaxBlobTxSize);
        }

        [Test]
        public void blob_pool_size_should_be_correct([Values(true, false)] bool persistentStorageEnabled)
        {
            const int poolSize = 10;
            TxPoolConfig txPoolConfig = new()
            {
                BlobsSupport = persistentStorageEnabled ? BlobsSupportMode.Storage : BlobsSupportMode.InMemory,
                PersistentBlobStorageSize = persistentStorageEnabled ? poolSize : 0,
                InMemoryBlobPoolSize = persistentStorageEnabled ? 0 : poolSize
            };
            _txPool = CreatePool(txPoolConfig, GetCancunSpecProvider());

            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);
            for (int i = 0; i < poolSize; i++)
            {
                Transaction tx = Build.A.Transaction
                    .WithNonce((UInt256)i)
                    .WithShardBlobTxTypeAndFields()
                    .WithMaxFeePerGas(1.GWei() + (UInt256)(100 - i))
                    .WithMaxPriorityFeePerGas(1.GWei() + (UInt256)(100 - i))
                    .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
                _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast).Should().Be(AcceptTxResult.Accepted);
            }

            _txPool.GetPendingTransactionsCount().Should().Be(0);
            _txPool.GetPendingBlobTransactionsCount().Should().Be(poolSize);
        }

        [TestCase(TxType.EIP1559, 0, 5, 100)]
        [TestCase(TxType.Blob, 5, 0, 100)]
        [TestCase(TxType.EIP1559, 10, 0, 10)]
        [TestCase(TxType.Blob, 0, 15, 15)]
        [TestCase(TxType.EIP1559, 20, 25, 20)]
        [TestCase(TxType.Blob, 30, 35, 35)]
        public void should_reject_txs_with_nonce_too_far_in_future(TxType txType, int maxPendingTxs, int maxPendingBlobTxs, int expectedNumberOfAcceptedTxs)
        {
            TxPoolConfig txPoolConfig = new()
            {
                BlobsSupport = BlobsSupportMode.InMemory,
                Size = 100,
                MaxPendingTxsPerSender = maxPendingTxs,
                MaxPendingBlobTxsPerSender = maxPendingBlobTxs
            };
            _txPool = CreatePool(txPoolConfig, GetCancunSpecProvider());
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);
            for (int nonce = 0; nonce < txPoolConfig.Size; nonce++)
            {
                Transaction tx = Build.A.Transaction
                    .WithNonce((UInt256)nonce)
                    .WithType(txType)
                    .WithShardBlobTxTypeAndFieldsIfBlobTx()
                    .WithMaxFeePerGas(1.GWei())
                    .WithMaxPriorityFeePerGas(1.GWei())
                    .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

                _txPool.SubmitTx(tx, TxHandlingOptions.None).Should().Be(nonce > expectedNumberOfAcceptedTxs
                    ? AcceptTxResult.NonceTooFarInFuture
                    : AcceptTxResult.Accepted);
            }
        }

        [Test]
        public void should_reject_tx_with_FeeTooLow_even_if_is_blob_type([Values(true, false)] bool isBlob, [Values(true, false)] bool persistentStorageEnabled)
        {
            const int poolSize = 10;
            TxPoolConfig txPoolConfig = new()
            {
                BlobsSupport = persistentStorageEnabled ? BlobsSupportMode.Storage : BlobsSupportMode.InMemory,
                Size = isBlob ? 0 : poolSize,
                PersistentBlobStorageSize = persistentStorageEnabled ? poolSize : 0,
                InMemoryBlobPoolSize = persistentStorageEnabled ? 0 : poolSize
            };

            _txPool = CreatePool(txPoolConfig, GetCancunSpecProvider());
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.AddressB, UInt256.MaxValue);

            for (int i = 0; i < poolSize; i++)
            {
                Transaction tx = Build.A.Transaction
                    .WithNonce((UInt256)i)
                    .WithType(isBlob ? TxType.Blob : TxType.EIP1559)
                    .WithShardBlobTxTypeAndFieldsIfBlobTx()
                    .WithMaxFeePerGas(1.GWei() + (UInt256)(100 - i))
                    .WithMaxPriorityFeePerGas(1.GWei() + (UInt256)(100 - i))
                    .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
                _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast).Should().Be(AcceptTxResult.Accepted);
            }

            _txPool.GetPendingTransactionsCount().Should().Be(isBlob ? 0 : poolSize);
            _txPool.GetPendingBlobTransactionsCount().Should().Be(isBlob ? poolSize : 0);

            Transaction feeTooLowTx = Build.A.Transaction
                .WithNonce(UInt256.Zero)
                .WithType(isBlob ? TxType.Blob : TxType.EIP1559)
                .WithShardBlobTxTypeAndFieldsIfBlobTx()
                .WithMaxFeePerGas(1.GWei() + UInt256.One)
                .WithMaxPriorityFeePerGas(1.GWei() + UInt256.One)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyB).TestObject;

            _txPool.SubmitTx(feeTooLowTx, TxHandlingOptions.None).Should().Be(AcceptTxResult.FeeTooLow);
        }

        [Test]
        public void should_add_blob_tx_and_return_when_requested([Values(true, false)] bool isPersistentStorage)
        {
            TxPoolConfig txPoolConfig = new()
            {
                BlobsSupport = isPersistentStorage ? BlobsSupportMode.Storage : BlobsSupportMode.InMemory,
                Size = 10
            };
            BlobTxStorage blobTxStorage = new();
            _txPool = CreatePool(txPoolConfig, GetCancunSpecProvider(), txStorage: blobTxStorage);
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            Transaction blobTxAdded = Build.A.Transaction
                .WithShardBlobTxTypeAndFields()
                .WithMaxFeePerGas(1.GWei())
                .WithMaxPriorityFeePerGas(1.GWei())
                .WithNonce(UInt256.Zero)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

            _txPool.SubmitTx(blobTxAdded, TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);
            _txPool.TryGetPendingTransaction(blobTxAdded.Hash!, out Transaction blobTxReturned);

            blobTxReturned.Should().BeEquivalentTo(blobTxAdded);

            blobTxStorage.TryGet(blobTxAdded.Hash, blobTxAdded.SenderAddress!, blobTxAdded.Timestamp, out Transaction blobTxFromDb).Should().Be(isPersistentStorage); // additional check for persistent db
            if (isPersistentStorage)
            {
                blobTxFromDb.Should().BeEquivalentTo(blobTxAdded, static options => options
                    .Excluding(static t => t.GasBottleneck) // GasBottleneck is not encoded/decoded...
                    .Excluding(static t => t.PoolIndex));   // ...as well as PoolIndex
            }
        }

        [Test]
        public void should_not_throw_when_asking_for_non_existing_tx()
        {
            TxPoolConfig txPoolConfig = new() { Size = 10 };
            BlobTxStorage blobTxStorage = new();
            _txPool = CreatePool(txPoolConfig, GetCancunSpecProvider(), txStorage: blobTxStorage);

            _txPool.TryGetPendingTransaction(TestItem.KeccakA, out Transaction blobTxReturned).Should().BeFalse();
            blobTxReturned.Should().BeNull();

            blobTxStorage.TryGet(TestItem.KeccakA, TestItem.AddressA, UInt256.One, out Transaction blobTxFromDb).Should().BeFalse();
            blobTxFromDb.Should().BeNull();
        }

        [TestCase(999_999_999, false)]
        [TestCase(1_000_000_000, true)]
        public void should_not_allow_to_add_blob_tx_with_MaxPriorityFeePerGas_lower_than_1GWei(int maxPriorityFeePerGas, bool expectedResult)
        {
            TxPoolConfig txPoolConfig = new() { BlobsSupport = BlobsSupportMode.InMemory, Size = 10 };
            _txPool = CreatePool(txPoolConfig, GetCancunSpecProvider());
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            Transaction tx = Build.A.Transaction
                .WithShardBlobTxTypeAndFields()
                .WithMaxFeePerGas((UInt256)maxPriorityFeePerGas)
                .WithMaxPriorityFeePerGas((UInt256)maxPriorityFeePerGas)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

            _txPool.SubmitTx(tx, TxHandlingOptions.None).Should().Be(expectedResult
                ? AcceptTxResult.Accepted
                : AcceptTxResult.FeeTooLow);
        }

        [Test]
        public void should_not_add_nonce_gap_blob_tx_even_to_not_full_TxPool([Values(true, false)] bool isBlob)
        {
            _txPool = CreatePool(new TxPoolConfig() { BlobsSupport = BlobsSupportMode.InMemory, Size = 128 }, GetCancunSpecProvider());
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            Transaction firstTx = Build.A.Transaction
                .WithType(isBlob ? TxType.Blob : TxType.EIP1559)
                .WithShardBlobTxTypeAndFieldsIfBlobTx()
                .WithMaxFeePerGas(1.GWei())
                .WithMaxPriorityFeePerGas(1.GWei())
                .WithNonce(UInt256.Zero)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

            Transaction nonceGapTx = Build.A.Transaction
                .WithType(isBlob ? TxType.Blob : TxType.EIP1559)
                .WithShardBlobTxTypeAndFieldsIfBlobTx()
                .WithMaxFeePerGas(1.GWei())
                .WithMaxPriorityFeePerGas(1.GWei())
                .WithNonce((UInt256)2)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

            _txPool.SubmitTx(firstTx, TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);
            _txPool.SubmitTx(nonceGapTx, TxHandlingOptions.None).Should().Be(isBlob ? AcceptTxResult.NonceGap : AcceptTxResult.Accepted);
        }

        [Test]
        public void should_not_allow_to_have_pending_transactions_of_both_blob_type_and_other([Values(true, false)] bool firstIsBlob, [Values(true, false)] bool secondIsBlob)
        {
            Transaction GetTx(bool isBlob, UInt256 nonce)
            {
                return Build.A.Transaction
                    .WithType(isBlob ? TxType.Blob : TxType.EIP1559)
                    .WithShardBlobTxTypeAndFieldsIfBlobTx()
                    .WithMaxFeePerGas(1.GWei())
                    .WithMaxPriorityFeePerGas(1.GWei())
                    .WithNonce(nonce)
                    .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            }

            _txPool = CreatePool(new TxPoolConfig() { BlobsSupport = BlobsSupportMode.InMemory, Size = 128 }, GetCancunSpecProvider());
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            Transaction firstTx = GetTx(firstIsBlob, UInt256.Zero);
            Transaction secondTx = GetTx(secondIsBlob, UInt256.One);

            _txPool.SubmitTx(firstTx, TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);
            _txPool.SubmitTx(secondTx, TxHandlingOptions.None).Should().Be(firstIsBlob ^ secondIsBlob ? AcceptTxResult.PendingTxsOfConflictingType : AcceptTxResult.Accepted);
        }

        [Test]
        public void should_remove_replaced_blob_tx_from_persistent_storage_and_cache()
        {
            TxPoolConfig txPoolConfig = new()
            {
                BlobsSupport = BlobsSupportMode.Storage,
                Size = 10
            };
            BlobTxStorage blobTxStorage = new();
            _txPool = CreatePool(txPoolConfig, GetCancunSpecProvider(), txStorage: blobTxStorage);
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            Transaction oldTx = Build.A.Transaction
                .WithShardBlobTxTypeAndFields()
                .WithNonce(UInt256.Zero)
                .WithMaxFeePerGas(1.GWei())
                .WithMaxPriorityFeePerGas(1.GWei())
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

            Transaction newTx = Build.A.Transaction
                .WithShardBlobTxTypeAndFields()
                .WithNonce(UInt256.Zero)
                .WithMaxFeePerGas(oldTx.MaxFeePerGas * 2)
                .WithMaxPriorityFeePerGas(oldTx.MaxPriorityFeePerGas * 2)
                .WithMaxFeePerBlobGas(oldTx.MaxFeePerBlobGas * 2)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;


            _txPool.SubmitTx(oldTx, TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);
            _txPool.GetPendingBlobTransactionsCount().Should().Be(1);
            _txPool.TryGetPendingTransaction(oldTx.Hash!, out Transaction blobTxReturned).Should().BeTrue();
            blobTxReturned.Should().BeEquivalentTo(oldTx);
            blobTxStorage.TryGet(oldTx.Hash, oldTx.SenderAddress!, oldTx.Timestamp, out Transaction blobTxFromDb).Should().BeTrue();
            blobTxFromDb.Should().BeEquivalentTo(oldTx, static options => options
                .Excluding(static t => t.GasBottleneck) // GasBottleneck is not encoded/decoded...
                .Excluding(static t => t.PoolIndex));   // ...as well as PoolIndex

            _txPool.SubmitTx(newTx, TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);
            _txPool.GetPendingBlobTransactionsCount().Should().Be(1);
            _txPool.TryGetPendingTransaction(newTx.Hash!, out blobTxReturned).Should().BeTrue();
            blobTxReturned.Should().BeEquivalentTo(newTx);
            blobTxStorage.TryGet(oldTx.Hash, oldTx.SenderAddress, oldTx.Timestamp, out blobTxFromDb).Should().BeFalse();
            blobTxStorage.TryGet(newTx.Hash, newTx.SenderAddress!, newTx.Timestamp, out blobTxFromDb).Should().BeTrue();
            blobTxFromDb.Should().BeEquivalentTo(newTx, static options => options
                .Excluding(static t => t.GasBottleneck) // GasBottleneck is not encoded/decoded...
                .Excluding(static t => t.PoolIndex));   // ...as well as PoolIndex
        }

        [Test]
        public void should_keep_in_memory_only_light_blob_tx_equivalent_if_persistent_storage_enabled([Values(true, false)] bool isPersistentStorage)
        {
            TxPoolConfig txPoolConfig = new()
            {
                BlobsSupport = isPersistentStorage ? BlobsSupportMode.Storage : BlobsSupportMode.InMemory,
                Size = 10
            };
            _txPool = CreatePool(txPoolConfig, GetCancunSpecProvider());
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            Transaction tx = Build.A.Transaction
                .WithShardBlobTxTypeAndFields()
                .WithNonce(UInt256.Zero)
                .WithMaxFeePerGas(1.GWei())
                .WithMaxPriorityFeePerGas(1.GWei())
                .WithMaxFeePerBlobGas(UInt256.One)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

            _txPool.SubmitTx(tx, TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);
            _txPool.GetPendingBlobTransactionsCount().Should().Be(1);
            _txPool.GetPendingTransactionsCount().Should().Be(0);

            _txPool.TryGetBlobTxSortingEquivalent(tx.Hash!, out Transaction returned);
            returned.Should().BeEquivalentTo(isPersistentStorage ? new LightTransaction(tx) : tx);
        }

        [Test]
        public void should_dump_GasBottleneck_of_blob_tx_to_zero_if_MaxFeePerBlobGas_is_lower_than_current([Values(true, false)] bool isBlob, [Values(true, false)] bool isPersistentStorage)
        {
            TxPoolConfig txPoolConfig = new()
            {
                BlobsSupport = isPersistentStorage ? BlobsSupportMode.Storage : BlobsSupportMode.InMemory,
                Size = 10
            };
            _txPool = CreatePool(txPoolConfig, GetCancunSpecProvider());
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            _headInfo.CurrentFeePerBlobGas = UInt256.MaxValue;

            Transaction tx = Build.A.Transaction
                .WithType(isBlob ? TxType.Blob : TxType.EIP1559)
                .WithShardBlobTxTypeAndFieldsIfBlobTx()
                .WithNonce(UInt256.Zero)
                .WithMaxFeePerGas(1.GWei())
                .WithMaxPriorityFeePerGas(1.GWei())
                .WithMaxFeePerBlobGas(isBlob ? UInt256.One : null)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

            _txPool.SubmitTx(tx, TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);
            _txPool.GetPendingBlobTransactionsCount().Should().Be(isBlob ? 1 : 0);
            _txPool.GetPendingTransactionsCount().Should().Be(isBlob ? 0 : 1);
            if (isBlob)
            {
                _txPool.TryGetBlobTxSortingEquivalent(tx.Hash!, out Transaction returned);
                returned.GasBottleneck.Should().Be(UInt256.Zero);
                returned.Should().BeEquivalentTo(isPersistentStorage ? new LightTransaction(tx) : tx,
                    static options => options.Excluding(static t => t.GasBottleneck));
                returned.Should().NotBeEquivalentTo(isPersistentStorage ? tx : new LightTransaction(tx));
            }
            else
            {
                _txPool.TryGetPendingTransaction(tx.Hash!, out Transaction eip1559Tx);
                eip1559Tx.Should().BeEquivalentTo(tx);
                eip1559Tx.GasBottleneck.Should().Be(1.GWei());
            }
        }

        [Test]
        public void should_not_allow_to_replace_blob_tx_by_tx_with_less_blobs([Values(1, 2, 3, 4, 5, 6)] int blobsInFirstTx, [Values(1, 2, 3, 4, 5, 6)] int blobsInSecondTx)
        {
            bool shouldReplace = blobsInFirstTx <= blobsInSecondTx;

            _txPool = CreatePool(new TxPoolConfig() { BlobsSupport = BlobsSupportMode.InMemory, Size = 128 }, GetCancunSpecProvider());
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            Transaction firstTx = Build.A.Transaction
                .WithShardBlobTxTypeAndFields(blobsInFirstTx)
                .WithNonce(UInt256.Zero)
                .WithMaxFeePerGas(1.GWei())
                .WithMaxPriorityFeePerGas(1.GWei())
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

            Transaction secondTx = Build.A.Transaction
                .WithShardBlobTxTypeAndFields(blobsInSecondTx)
                .WithNonce(UInt256.Zero)
                .WithMaxFeePerGas(firstTx.MaxFeePerGas * 2)
                .WithMaxPriorityFeePerGas(firstTx.MaxPriorityFeePerGas * 2)
                .WithMaxFeePerBlobGas(firstTx.MaxFeePerBlobGas * 2)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

            _txPool.SubmitTx(firstTx, TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);

            _txPool.GetPendingBlobTransactionsCount().Should().Be(1);

            _txPool.SubmitTx(secondTx, TxHandlingOptions.None).Should().Be(shouldReplace ? AcceptTxResult.Accepted : AcceptTxResult.ReplacementNotAllowed);
            _txPool.GetPendingBlobTransactionsCount().Should().Be(1);
            _txPool.TryGetPendingTransaction(firstTx.Hash!, out Transaction returnedFirstTx).Should().Be(!shouldReplace);
            _txPool.TryGetPendingTransaction(secondTx.Hash!, out Transaction returnedSecondTx).Should().Be(shouldReplace);
            returnedFirstTx.Should().BeEquivalentTo(shouldReplace ? null : firstTx);
            returnedSecondTx.Should().BeEquivalentTo(shouldReplace ? secondTx : null);
        }

        [Test]
        public void should_allow_to_replace_blob_tx_by_the_one_with_network_wrapper_in_higher_version()
        {
            // start with Cancun
            OverridableReleaseSpec releaseSpec = new OverridableReleaseSpec(Cancun.Instance);
            IChainHeadSpecProvider specProvider = Substitute.For<IChainHeadSpecProvider>();
            specProvider.GetCurrentHeadSpec().Returns(releaseSpec);

            ChainHeadInfoProvider chainHeadInfoProvider = new(specProvider, _blockTree, _stateProvider, new CodeInfoRepository());
            _txPool = CreatePool(new TxPoolConfig() { BlobsSupport = BlobsSupportMode.InMemory, Size = 128 },
                specProvider: specProvider, chainHeadInfoProvider: chainHeadInfoProvider);

            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            Transaction firstTx = Build.A.Transaction
                .WithShardBlobTxTypeAndFields(spec: new ReleaseSpec() { IsEip7594Enabled = false })
                .WithNonce(UInt256.Zero)
                .WithMaxFeePerGas(1.GWei())
                .WithMaxPriorityFeePerGas(1.GWei())
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

            Transaction secondTx = Build.A.Transaction
                .WithShardBlobTxTypeAndFields(spec: new ReleaseSpec() { IsEip7594Enabled = true })
                .WithNonce(UInt256.Zero)
                .WithMaxFeePerGas(2.GWei())
                .WithMaxPriorityFeePerGas(1.GWei())
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

            _txPool.SubmitTx(firstTx, TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);
            _txPool.GetPendingBlobTransactionsCount().Should().Be(1);

            // switch to Osaka
            releaseSpec = new OverridableReleaseSpec(Osaka.Instance);
            specProvider.GetCurrentHeadSpec().Returns(releaseSpec);

            _txPool.SubmitTx(secondTx, TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);
            _txPool.GetPendingBlobTransactionsCount().Should().Be(1);
            _txPool.TryGetPendingTransaction(firstTx.Hash!, out Transaction returnedFirstTx).Should().BeFalse();
            _txPool.TryGetPendingTransaction(secondTx.Hash!, out Transaction returnedSecondTx).Should().BeTrue();
            returnedFirstTx.Should().BeNull();
            returnedSecondTx.Should().BeEquivalentTo(secondTx);
        }

        [Test]
        public void should_discard_tx_when_data_gas_cost_cause_overflow([Values(false, true)] bool supportsBlobs)
        {
            _txPool = CreatePool(new TxPoolConfig() { BlobsSupport = BlobsSupportMode.InMemory }, GetCancunSpecProvider());

            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            UInt256.MaxValue.Divide(GasCostOf.Transaction * 2, out UInt256 halfOfMaxGasPriceWithoutOverflow);

            Transaction firstTransaction = Build.A.Transaction
                .WithShardBlobTxTypeAndFields()
                .WithMaxFeePerBlobGas(UInt256.Zero)
                .WithNonce(UInt256.Zero)
                .WithMaxFeePerGas(halfOfMaxGasPriceWithoutOverflow)
                .WithMaxPriorityFeePerGas(halfOfMaxGasPriceWithoutOverflow)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            _txPool.SubmitTx(firstTransaction, TxHandlingOptions.PersistentBroadcast).Should().Be(AcceptTxResult.Accepted);

            Transaction transactionWithPotentialOverflow = Build.A.Transaction
                .WithShardBlobTxTypeAndFields()
                .WithMaxFeePerBlobGas(supportsBlobs
                    ? UInt256.One
                    : UInt256.Zero)
                .WithNonce(UInt256.One)
                .WithMaxFeePerGas(halfOfMaxGasPriceWithoutOverflow)
                .WithMaxPriorityFeePerGas(halfOfMaxGasPriceWithoutOverflow)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

            _txPool.SubmitTx(transactionWithPotentialOverflow, TxHandlingOptions.PersistentBroadcast).Should().Be(supportsBlobs ? AcceptTxResult.Int256Overflow : AcceptTxResult.Accepted);
        }

        [Test]
        public async Task should_allow_to_have_pending_transaction_of_other_type_if_conflicting_one_was_included([Values(true, false)] bool firstIsBlob, [Values(true, false)] bool secondIsBlob)
        {
            Transaction GetTx(bool isBlob, UInt256 nonce)
            {
                return Build.A.Transaction
                    .WithType(isBlob ? TxType.Blob : TxType.EIP1559)
                    .WithShardBlobTxTypeAndFieldsIfBlobTx()
                    .WithMaxFeePerGas(1.GWei())
                    .WithMaxPriorityFeePerGas(1.GWei())
                    .WithNonce(nonce)
                    .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            }

            _txPool = CreatePool(new TxPoolConfig() { BlobsSupport = BlobsSupportMode.InMemory, Size = 128 }, GetCancunSpecProvider());
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            Transaction firstTx = GetTx(firstIsBlob, UInt256.Zero);
            Transaction secondTx = GetTx(secondIsBlob, UInt256.One);

            _txPool.SubmitTx(firstTx, TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);

            _txPool.GetPendingTransactionsCount().Should().Be(firstIsBlob ? 0 : 1);
            _txPool.GetPendingBlobTransactionsCount().Should().Be(firstIsBlob ? 1 : 0);
            _stateProvider.IncrementNonce(TestItem.AddressA);
            await RaiseBlockAddedToMainAndWaitForTransactions(1);

            _txPool.GetPendingTransactionsCount().Should().Be(0);
            _txPool.GetPendingBlobTransactionsCount().Should().Be(0);
            _txPool.SubmitTx(secondTx, TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);
            _txPool.GetPendingTransactionsCount().Should().Be(secondIsBlob ? 0 : 1);
            _txPool.GetPendingBlobTransactionsCount().Should().Be(secondIsBlob ? 1 : 0);
        }

        [TestCase(0, 97)]
        [TestCase(1, 131320)]
        [TestCase(2, 262530)]
        [TestCase(3, 393737)]
        [TestCase(4, 524944)]
        [TestCase(5, 656152)]
        [TestCase(6, 787361)]
        public void should_calculate_size_of_blob_tx_correctly(int numberOfBlobs, int expectedLength)
        {
            Transaction blobTx = Build.A.Transaction
                .WithShardBlobTxTypeAndFields(numberOfBlobs)
                .SignedAndResolved()
                .TestObject;
            blobTx.GetLength().Should().Be(expectedLength);
        }

        [Test]
        public void RecoverAddress_should_work_correctly()
        {
            Transaction tx = CreateBlobTx(TestItem.PrivateKeyA);
            _ethereumEcdsa.RecoverAddress(tx).Should().Be(tx.SenderAddress);
        }

        [Test]
        public async Task should_add_processed_txs_to_db()
        {
            const long blockNumber = 358;

            BlobTxStorage blobTxStorage = new();
            ITxPoolConfig txPoolConfig = new TxPoolConfig()
            {
                Size = 128,
                BlobsSupport = BlobsSupportMode.StorageWithReorgs
            };
            _txPool = CreatePool(txPoolConfig, GetCancunSpecProvider(), txStorage: blobTxStorage);

            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.AddressB, UInt256.MaxValue);

            Transaction[] txs = { CreateBlobTx(TestItem.PrivateKeyA), CreateBlobTx(TestItem.PrivateKeyB) };

            _txPool.SubmitTx(txs[0], TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);
            _txPool.SubmitTx(txs[1], TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);

            _txPool.GetPendingTransactionsCount().Should().Be(0);
            _txPool.GetPendingBlobTransactionsCount().Should().Be(txs.Length);
            _stateProvider.IncrementNonce(TestItem.AddressA);
            _stateProvider.IncrementNonce(TestItem.AddressB);

            Block block = Build.A.Block.WithNumber(blockNumber).WithTransactions(txs).TestObject;

            await RaiseBlockAddedToMainAndWaitForTransactions(txs.Length, block);

            _txPool.GetPendingTransactionsCount().Should().Be(0);
            _txPool.GetPendingBlobTransactionsCount().Should().Be(0);

            blobTxStorage.TryGetBlobTransactionsFromBlock(blockNumber, out Transaction[] returnedTxs).Should().BeTrue();
            returnedTxs.Length.Should().Be(txs.Length);
            returnedTxs.Should().BeEquivalentTo(txs, static options => options
                .Excluding(static t => t.GasBottleneck)    // GasBottleneck is not encoded/decoded...
                .Excluding(static t => t.PoolIndex)        // ...as well as PoolIndex
                .Excluding(static t => t.SenderAddress));  // sender is recovered later, it is not returned from db

            blobTxStorage.DeleteBlobTransactionsFromBlock(blockNumber);
            blobTxStorage.TryGetBlobTransactionsFromBlock(blockNumber, out returnedTxs).Should().BeFalse();
        }

        [Test]
        public async Task should_bring_back_reorganized_blob_txs()
        {
            const long blockNumber = 358;

            BlobTxStorage blobTxStorage = new();
            ITxPoolConfig txPoolConfig = new TxPoolConfig()
            {
                Size = 128,
                BlobsSupport = BlobsSupportMode.StorageWithReorgs
            };
            _txPool = CreatePool(txPoolConfig, GetCancunSpecProvider(), txStorage: blobTxStorage);

            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.AddressB, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.AddressC, UInt256.MaxValue);

            Transaction[] txsA = { CreateBlobTx(TestItem.PrivateKeyA), CreateBlobTx(TestItem.PrivateKeyB) };
            Transaction[] txsB = { CreateBlobTx(TestItem.PrivateKeyC) };

            _txPool.SubmitTx(txsA[0], TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);
            _txPool.SubmitTx(txsA[1], TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);
            _txPool.SubmitTx(txsB[0], TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);

            _txPool.GetPendingTransactionsCount().Should().Be(0);
            _txPool.GetPendingBlobTransactionsCount().Should().Be(txsA.Length + txsB.Length);

            // adding block A
            Block blockA = Build.A.Block.WithNumber(blockNumber).WithTransactions(txsA).TestObject;
            await RaiseBlockAddedToMainAndWaitForNewHead(blockA);

            _txPool.GetPendingBlobTransactionsCount().Should().Be(txsB.Length);
            _txPool.TryGetPendingBlobTransaction(txsA[0].Hash!, out _).Should().BeFalse();
            _txPool.TryGetPendingBlobTransaction(txsA[1].Hash!, out _).Should().BeFalse();
            _txPool.TryGetPendingBlobTransaction(txsB[0].Hash!, out _).Should().BeTrue();

            // reorganized from block A to block B
            Block blockB = Build.A.Block.WithNumber(blockNumber).WithTransactions(txsB).TestObject;
            await RaiseBlockAddedToMainAndWaitForNewHead(blockB, blockA);

            // tx from block B should be removed from blob pool, but present in processed txs db
            _txPool.TryGetPendingBlobTransaction(txsB[0].Hash!, out _).Should().BeFalse();
            blobTxStorage.TryGetBlobTransactionsFromBlock(blockNumber, out Transaction[] blockBTxs).Should().BeTrue();
            txsB.Should().BeEquivalentTo(blockBTxs, static options => options
                .Excluding(static t => t.GasBottleneck)    // GasBottleneck is not encoded/decoded...
                .Excluding(static t => t.PoolIndex)        // ...as well as PoolIndex
                .Excluding(static t => t.SenderAddress));  // sender is recovered later, it is not returned from db

            // blob txs from reorganized blockA should be readded to blob pool
            _txPool.GetPendingBlobTransactionsCount().Should().Be(txsA.Length);
            _txPool.TryGetPendingBlobTransaction(txsA[0].Hash!, out Transaction tx1).Should().BeTrue();
            _txPool.TryGetPendingBlobTransaction(txsA[1].Hash!, out Transaction tx2).Should().BeTrue();

            tx1.Should().BeEquivalentTo(txsA[0], static options => options
                .Excluding(static t => t.GasBottleneck)    // GasBottleneck is not encoded/decoded...
                .Excluding(static t => t.PoolIndex));      // ...as well as PoolIndex

            tx2.Should().BeEquivalentTo(txsA[1], static options => options
                .Excluding(static t => t.GasBottleneck)    // GasBottleneck is not encoded/decoded...
                .Excluding(static t => t.PoolIndex));      // ...as well as PoolIndex
        }

        [Test]
        public void should_index_blobs_when_adding_txs([Values(true, false)] bool isPersistentStorage, [Values(true, false)] bool uniqueBlobs)
        {
            const int poolSize = 10;
            TxPoolConfig txPoolConfig = new()
            {
                BlobsSupport = isPersistentStorage ? BlobsSupportMode.Storage : BlobsSupportMode.InMemory,
                PersistentBlobStorageSize = isPersistentStorage ? poolSize : 0,
                InMemoryBlobPoolSize = isPersistentStorage ? 0 : poolSize
            };

            IComparer<Transaction> comparer = new TransactionComparerProvider(_specProvider, _blockTree).GetDefaultComparer();

            BlobTxDistinctSortedPool blobPool = isPersistentStorage
                ? new PersistentBlobTxDistinctSortedPool(new BlobTxStorage(), txPoolConfig, comparer, LimboLogs.Instance)
                : new BlobTxDistinctSortedPool(txPoolConfig.InMemoryBlobPoolSize, comparer, LimboLogs.Instance);

            Transaction[] blobTxs = new Transaction[poolSize * 2];

            IBlobProofsBuilder blobProofsBuilder = IBlobProofsManager.For(ProofVersion.V1);

            // adding 2x more txs than pool capacity. First half will be evicted
            for (int i = 0; i < poolSize * 2; i++)
            {
                EnsureSenderBalance(TestItem.Addresses[i], UInt256.MaxValue);

                blobTxs[i] = Build.A.Transaction
                    .WithShardBlobTxTypeAndFields(isMempoolTx: !uniqueBlobs)
                    .WithMaxFeePerGas(1.GWei() + (UInt256)i)
                    .WithMaxPriorityFeePerGas(1.GWei() + (UInt256)i)
                    .WithMaxFeePerBlobGas(1000.Wei() + (UInt256)i)
                    .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeys[i]).TestObject;

                // making blobs unique. Otherwise, all txs have the same blob
                if (uniqueBlobs)
                {
                    byte[] blobs = new byte[Ckzg.BytesPerBlob];
                    blobs[0] = (byte)(i % 256);

                    var networkWrapper = blobProofsBuilder.AllocateWrapper(blobs);
                    blobProofsBuilder.ComputeProofsAndCommitments(networkWrapper);
                    byte[][] hashes = blobProofsBuilder.ComputeHashes(networkWrapper);

                    blobTxs[i].NetworkWrapper = networkWrapper;
                    blobTxs[i].BlobVersionedHashes = hashes;
                }

                blobPool.TryInsert(blobTxs[i].Hash, blobTxs[i], out _).Should().BeTrue();
            }

            blobPool.BlobIndex.Count.Should().Be(uniqueBlobs ? poolSize : 1);

            // first half of txs (0, poolSize - 1) was evicted and should be removed from index
            // second half (poolSize, 2x poolSize - 1) should be indexed
            for (int i = 0; i < poolSize * 2; i++)
            {
                // if blobs are unique, we expect index to have 10 keys (poolSize, 2x poolSize - 1) with 1 value each
                if (uniqueBlobs)
                {
                    blobPool.BlobIndex.TryGetValue(blobTxs[i].BlobVersionedHashes[0]!, out List<Hash256> txHashes).Should().Be(i >= poolSize);
                    if (i >= poolSize)
                    {
                        txHashes.Count.Should().Be(1);
                        txHashes[0].Should().Be(blobTxs[i].Hash);
                    }
                }
                // if blobs are not unique, we expect index to have 1 key with 10 values (poolSize, 2x poolSize - 1)
                else
                {
                    blobPool.BlobIndex.TryGetValue(blobTxs[i].BlobVersionedHashes[0]!, out List<Hash256> values).Should().BeTrue();
                    values.Count.Should().Be(poolSize);
                    values.Contains(blobTxs[i].Hash).Should().Be(i >= poolSize);
                }
            }
        }

        [Test]
        [Repeat(3)]
        public void should_handle_indexing_blobs_when_adding_txs_in_parallel([Values(true, false)] bool isPersistentStorage)
        {
            const int txsPerSender = 10;
            int poolSize = TestItem.PrivateKeys.Length * txsPerSender;
            TxPoolConfig txPoolConfig = new()
            {
                BlobsSupport = isPersistentStorage ? BlobsSupportMode.Storage : BlobsSupportMode.InMemory,
                PersistentBlobStorageSize = isPersistentStorage ? poolSize : 0,
                InMemoryBlobPoolSize = isPersistentStorage ? 0 : poolSize
            };

            IComparer<Transaction> comparer = new TransactionComparerProvider(_specProvider, _blockTree).GetDefaultComparer();

            BlobTxDistinctSortedPool blobPool = isPersistentStorage
                ? new PersistentBlobTxDistinctSortedPool(new BlobTxStorage(), txPoolConfig, comparer, LimboLogs.Instance)
                : new BlobTxDistinctSortedPool(txPoolConfig.InMemoryBlobPoolSize, comparer, LimboLogs.Instance);

            byte[] expectedBlobVersionedHash = null;

            foreach (PrivateKey privateKey in TestItem.PrivateKeys)
            {
                EnsureSenderBalance(privateKey.Address, UInt256.MaxValue);
            }

            // adding, getting and removing txs in parallel
            Parallel.ForEach(TestItem.PrivateKeys, privateKey =>
            {
                for (int i = 0; i < txsPerSender; i++)
                {
                    Transaction tx = Build.A.Transaction
                        .WithNonce((UInt256)i)
                        .WithShardBlobTxTypeAndFields()
                        .WithMaxFeePerGas(1.GWei())
                        .WithMaxPriorityFeePerGas(1.GWei())
                        .WithMaxFeePerBlobGas(1000.Wei())
                        .SignedAndResolved(_ethereumEcdsa, privateKey).TestObject;

                    expectedBlobVersionedHash ??= tx.BlobVersionedHashes[0]!;

                    blobPool.TryInsert(tx.Hash, tx, out _).Should().BeTrue();

                    for (int j = 0; j < 100; j++)
                    {
                        blobPool.TryGetBlobAndProofV0(expectedBlobVersionedHash.ToBytes(), out _, out _).Should().BeTrue();
                    }

                    // removing 50% of txs
                    if (i % 2 == 0) blobPool.TryRemove(tx.Hash, out _).Should().BeTrue();
                }
            });

            // we expect index to have 1 key with poolSize/2 values (50% of txs were removed)
            blobPool.BlobIndex.Count.Should().Be(1);
            blobPool.BlobIndex.TryGetValue(expectedBlobVersionedHash, out List<Hash256> values).Should().BeTrue();
            values.Count.Should().Be(poolSize / 2);
        }

        [Test]
        public void should_add_blob_tx_in_eip7594_form_and_return_when_requested([Values(true, false)] bool isPersistentStorage, [Values(true, false)] bool hasTxCellProofs, [Values(true, false)] bool isOsakaActivated)
        {
            // tx is valid if:
            // - osaka is activated and there are cell proofs
            // - osaka is not activated and there is old proof
            bool isTxValid = !(isOsakaActivated ^ hasTxCellProofs);

            TxPoolConfig txPoolConfig = new()
            {
                BlobsSupport = isPersistentStorage ? BlobsSupportMode.Storage : BlobsSupportMode.InMemory,
                Size = 10
            };
            BlobTxStorage blobTxStorage = new();

            _txPool = CreatePool(txPoolConfig,
                isOsakaActivated ? GetOsakaSpecProvider() : GetCancunSpecProvider(),
                txStorage: blobTxStorage);

            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            Transaction blobTxAdded = Build.A.Transaction
                .WithShardBlobTxTypeAndFields(spec: new ReleaseSpec() { IsEip7594Enabled = hasTxCellProofs })
                .WithMaxFeePerGas(1.GWei())
                .WithMaxPriorityFeePerGas(1.GWei())
                .WithNonce(UInt256.Zero)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

            AcceptTxResult result = _txPool.SubmitTx(blobTxAdded, TxHandlingOptions.None);
            result.Should().Be(isTxValid ? AcceptTxResult.Accepted : AcceptTxResult.Invalid);
            _txPool.TryGetPendingTransaction(blobTxAdded.Hash!, out Transaction blobTxReturned);
            blobTxReturned.Should().BeEquivalentTo(isTxValid ? blobTxAdded : null);

            if (isTxValid)
            {
                ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)blobTxReturned.NetworkWrapper;
                wrapper.Proofs.Length.Should().Be(isOsakaActivated ? Ckzg.CellsPerExtBlob : 1);
                wrapper.Version.Should().Be(hasTxCellProofs ? ProofVersion.V1 : ProofVersion.V0);

                blobTxStorage.TryGet(blobTxAdded.Hash, blobTxAdded.SenderAddress!, blobTxAdded.Timestamp, out Transaction blobTxFromDb).Should().Be(isPersistentStorage); // additional check for persistent db
                if (isPersistentStorage)
                {
                    blobTxFromDb.Should().BeEquivalentTo(blobTxAdded, static options => options
                        .Excluding(static t => t.GasBottleneck) // GasBottleneck is not encoded/decoded...
                        .Excluding(static t => t.PoolIndex));   // ...as well as PoolIndex
                }
            }
        }

        [Test]
        public void should_convert_blob_proofs_to_cell_proofs_if_enabled([Values(true, false)] bool isPersistentStorage, [Values(true, false)] bool isOsakaActivated, [Values(true, false)] bool isConversionEnabled)
        {
            // tx has old version of proofs, so is valid if osaka is not activated or conversion is enabled
            bool isTxValid = !isOsakaActivated || isConversionEnabled;

            TxPoolConfig txPoolConfig = new()
            {
                BlobsSupport = isPersistentStorage ? BlobsSupportMode.Storage : BlobsSupportMode.InMemory,
                Size = 10,
                ProofsTranslationEnabled = isConversionEnabled
            };
            BlobTxStorage blobTxStorage = new();

            _txPool = CreatePool(txPoolConfig,
                isOsakaActivated ? GetOsakaSpecProvider() : GetCancunSpecProvider(),
                txStorage: blobTxStorage);

            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            // update head and set correct current proof version
            _blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(Build.A.Block.TestObject));

            Transaction blobTxAdded = Build.A.Transaction
                .WithShardBlobTxTypeAndFields()
                .WithMaxFeePerGas(1.GWei())
                .WithMaxPriorityFeePerGas(1.GWei())
                .WithNonce(UInt256.Zero)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

            AcceptTxResult result = _txPool.SubmitTx(blobTxAdded, TxHandlingOptions.None);
            result.Should().Be(isTxValid ? AcceptTxResult.Accepted : AcceptTxResult.Invalid);
            _txPool.TryGetPendingTransaction(blobTxAdded.Hash!, out Transaction blobTxReturned);
            blobTxReturned.Should().BeEquivalentTo(isTxValid ? blobTxAdded : null);

            if (isTxValid)
            {
                ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)blobTxReturned.NetworkWrapper;
                wrapper.Proofs.Length.Should().Be(isOsakaActivated ? Ckzg.CellsPerExtBlob : 1);
                wrapper.Version.Should().Be(isOsakaActivated ? ProofVersion.V1 : ProofVersion.V0);

                blobTxStorage.TryGet(blobTxAdded.Hash, blobTxAdded.SenderAddress!, blobTxAdded.Timestamp, out Transaction blobTxFromDb).Should().Be(isPersistentStorage); // additional check for persistent db
                if (isPersistentStorage)
                {
                    blobTxFromDb.Should().BeEquivalentTo(blobTxAdded, static options => options
                        .Excluding(static t => t.GasBottleneck) // GasBottleneck is not encoded/decoded...
                        .Excluding(static t => t.PoolIndex));   // ...as well as PoolIndex
                }
            }
        }

        [TestCaseSource(nameof(BlobScheduleActivationsTestCaseSource))]
        public async Task<int> should_evict_based_on_proof_version_and_fork(BlobsSupportMode poolMode, TestAction[] testActions)
        {
            (ChainSpecBasedSpecProvider provider, _) = TestSpecHelper.LoadChainSpec(new ChainSpecJson
            {
                Params = new ChainSpecParamsJson
                {
                    Eip4844TransitionTimestamp = _blockTree.Head.Timestamp,
                    Eip7002TransitionTimestamp = _blockTree.Head.Timestamp,
                    Eip7594TransitionTimestamp = _blockTree.Head.Timestamp + 1,
                }
            });

            Block head = _blockTree.Head;
            _blockTree.FindBestSuggestedHeader().Returns(head.Header);

            UInt256 nonce = 0;

            TxPoolConfig txPoolConfig = new() { BlobsSupport = poolMode, Size = 10 };
            _txPool = CreatePool(txPoolConfig, provider);
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            IReleaseSpec v1Spec = provider.GetSpec(new ForkActivation(0, _blockTree.Head.Timestamp + 1));

            foreach (TestAction action in testActions)
            {
                switch (action)
                {
                    case TestAction.AddV0:
                        _txPool.SubmitTx(CreateBlobTx(TestItem.PrivateKeyA, nonce++), TxHandlingOptions.None);
                        break;
                    case TestAction.AddV1:
                        _txPool.SubmitTx(CreateBlobTx(TestItem.PrivateKeyA, nonce++, releaseSpec: v1Spec), TxHandlingOptions.None);
                        break;
                    case TestAction.Fork:
                        await AddBlock();
                        break;
                    case TestAction.ResetNonce:
                        nonce = 0;
                        break;
                }
            }

            return _txPool.GetPendingBlobTransactionsCount();
        }

        public enum TestAction
        {
            AddV0,
            AddV1,
            Fork,
            ResetNonce,
        }

        public static IEnumerable BlobScheduleActivationsTestCaseSource
        {
            get
            {
                TestCaseData MakeTestCase(string testName, int finalCount, BlobsSupportMode mode, params TestAction[] testActions)
                    => new(mode, testActions) { TestName = $"EvictProofVersion({mode}): {testName}", ExpectedResult = finalCount };

                foreach (BlobsSupportMode mode in new[] { BlobsSupportMode.InMemory, BlobsSupportMode.Storage })
                {
                    yield return MakeTestCase("V0 should be evicted in Osaka", 0, mode, TestAction.AddV0, TestAction.Fork);
                    yield return MakeTestCase("Take only V0 ones before Osaka", 2, mode, TestAction.AddV0, TestAction.AddV0, TestAction.AddV1);
                    yield return MakeTestCase("Evict old proof and all the next txs", 0, mode, TestAction.AddV0, TestAction.AddV0, TestAction.Fork, TestAction.AddV1);
                    yield return MakeTestCase("Replace with new proof", 1, mode, TestAction.AddV0, TestAction.Fork, TestAction.ResetNonce, TestAction.AddV1);
                    yield return MakeTestCase("Ignore V1 before Osaka, no gaps", 0, mode, TestAction.AddV1);
                }
            }
        }

        private Task AddBlock()
        {
            BlockHeader bh = new(_blockTree.Head.Hash, Keccak.EmptyTreeHash, TestItem.AddressA, 0, _blockTree.Head.Number + 1, _blockTree.Head.GasLimit, _blockTree.Head.Timestamp + 1, []);
            _blockTree.FindBestSuggestedHeader().Returns(bh);
            _blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(new Block(bh, new BlockBody([], [])), _blockTree.Head));
            return Task.Delay(300);
        }

        private Transaction CreateBlobTx(PrivateKey sender, UInt256 nonce = default, int blobCount = 1, IReleaseSpec releaseSpec = default)
        {
            return Build.A.Transaction
                .WithShardBlobTxTypeAndFields(blobCount: blobCount, spec: releaseSpec)
                .WithMaxFeePerGas(1.GWei())
                .WithMaxPriorityFeePerGas(1.GWei())
                .WithNonce(nonce)
                .SignedAndResolved(_ethereumEcdsa, sender).TestObject;
        }

        [Test]
        public async Task should_evict_with_too_many_blobs()
        {
            ChainSpecBasedSpecProvider provider = new(new ChainSpec
            {
                Parameters = new ChainParameters
                {
                    Eip4844TransitionTimestamp = 0,
                    BlobSchedule = {
                        { new BlobScheduleSettings { Max = 5, Timestamp = _blockTree.Head.Timestamp  } },
                        { new BlobScheduleSettings { Max = 3, Timestamp = _blockTree.Head.Timestamp + 1  } },
                    },
                },
                EngineChainSpecParametersProvider = Substitute.For<IChainSpecParametersProvider>()
            });

            Block head = _blockTree.Head;
            _blockTree.FindBestSuggestedHeader().Returns(head.Header);

            TxPoolConfig txPoolConfig = new() { BlobsSupport = BlobsSupportMode.InMemory, Size = 10 };
            _txPool = CreatePool(txPoolConfig, provider);
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            UInt256 nonce = 0;

            _txPool.SubmitTx(CreateBlobTx(TestItem.PrivateKeyA, nonce++, 3), TxHandlingOptions.None);
            _txPool.SubmitTx(CreateBlobTx(TestItem.PrivateKeyA, nonce++, 5), TxHandlingOptions.None);
            _txPool.SubmitTx(CreateBlobTx(TestItem.PrivateKeyA, nonce++, 3), TxHandlingOptions.None);

            Assert.That(_txPool.GetPendingBlobTransactionsCount(), Is.EqualTo(3));

            await AddBlock();

            Assert.That(_txPool.GetPendingBlobTransactionsCount(), Is.EqualTo(1));
        }
    }
}
