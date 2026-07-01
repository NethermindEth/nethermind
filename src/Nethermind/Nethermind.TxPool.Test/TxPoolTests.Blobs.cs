// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using CkzgLib;
using Nethermind.Blockchain;
using Nethermind.Consensus.Comparers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.JavaScript;
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
using Nethermind.Blockchain.Spec;

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
                .WithNonce(0)
                .WithShardBlobTxTypeAndFields()
                .WithMaxFeePerGas(1.GWei)
                .WithMaxPriorityFeePerGas(1.GWei)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            Assert.That(_txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(isBlobSupportEnabled
                ? AcceptTxResult.Accepted
                : AcceptTxResult.NotSupportedTxType));
        }

        [Test]
        public void should_reject_blob_tx_if_max_size_is_exceeded([Values(true, false)] bool sizeExceeded, [Values(1, 2, 3, 4, 5, 6)] int numberOfBlobs)
        {
            Transaction tx = Build.A.Transaction
                .WithShardBlobTxTypeAndFields(numberOfBlobs)
                .WithMaxPriorityFeePerGas(1.GWei)
                .WithMaxFeePerGas(1.GWei)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            TxPoolConfig txPoolConfig = new() { MaxBlobTxSize = tx.GetLength(shouldCountBlobs: false) - (sizeExceeded ? 1 : 0) };
            _txPool = CreatePool(txPoolConfig, GetCancunSpecProvider());

            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            Assert.That(result, Is.EqualTo(sizeExceeded ? AcceptTxResult.MaxTxSizeExceeded : AcceptTxResult.Accepted));
            Assert.That(_txPool.GetPendingBlobTransactionsCount(), Is.EqualTo(sizeExceeded ? 0 : 1));
        }

        [Test]
        public void should_calculate_blob_tx_size_properly([Values(1, 2, 3, 4, 5, 6)] int numberOfBlobs)
        {
            Transaction tx = Build.A.Transaction
                .WithShardBlobTxTypeAndFields(numberOfBlobs)
                .WithMaxPriorityFeePerGas(1.GWei)
                .WithMaxFeePerGas(1.GWei)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            TxPoolConfig txPoolConfig = new() { MaxBlobTxSize = tx.GetLength(shouldCountBlobs: false) };
            _txPool = CreatePool(txPoolConfig, GetCancunSpecProvider());

            Assert.That(_txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Accepted));
            _txPool.TryGetPendingBlobTransaction(tx.Hash!, out Transaction blobTx);
            Assert.That(blobTx!.GetLength(), Is.GreaterThan((int)txPoolConfig.MaxBlobTxSize));
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
                    .WithNonce(i)
                    .WithShardBlobTxTypeAndFields()
                    .WithMaxFeePerGas(1.GWei + (UInt256)(100 - i))
                    .WithMaxPriorityFeePerGas(1.GWei + (UInt256)(100 - i))
                    .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
                Assert.That(_txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Accepted));
            }

            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(0));
            Assert.That(_txPool.GetPendingBlobTransactionsCount(), Is.EqualTo(poolSize));
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

            Transaction[] txs = new Transaction[txPoolConfig.Size];
            Parallel.For(0, txPoolConfig.Size, (nonce) =>
            {
                txs[nonce] = Build.A.Transaction
                    .WithNonce(nonce)
                    .WithType(txType)
                    .WithShardBlobTxTypeAndFieldsIfBlobTx()
                    .WithMaxFeePerGas(1.GWei)
                    .WithMaxPriorityFeePerGas(1.GWei)
                    .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            });

            for (int nonce = 0; nonce < txPoolConfig.Size; nonce++)
            {
                Assert.That(_txPool.SubmitTx(txs[nonce], TxHandlingOptions.None), Is.EqualTo(nonce > expectedNumberOfAcceptedTxs
                    ? AcceptTxResult.NonceTooFarInFuture
                    : AcceptTxResult.Accepted));
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
                    .WithNonce(i)
                    .WithType(isBlob ? TxType.Blob : TxType.EIP1559)
                    .WithShardBlobTxTypeAndFieldsIfBlobTx()
                    .WithMaxFeePerGas(1.GWei + (UInt256)(100 - i))
                    .WithMaxPriorityFeePerGas(1.GWei + (UInt256)(100 - i))
                    .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
                Assert.That(_txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Accepted));
            }

            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(isBlob ? 0 : poolSize));
            Assert.That(_txPool.GetPendingBlobTransactionsCount(), Is.EqualTo(isBlob ? poolSize : 0));

            Transaction feeTooLowTx = Build.A.Transaction
                .WithNonce(0)
                .WithType(isBlob ? TxType.Blob : TxType.EIP1559)
                .WithShardBlobTxTypeAndFieldsIfBlobTx()
                .WithMaxFeePerGas(1.GWei + UInt256.One)
                .WithMaxPriorityFeePerGas(1.GWei + UInt256.One)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyB).TestObject;

            Assert.That(_txPool.SubmitTx(feeTooLowTx, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.FeeTooLow));
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
                .WithMaxFeePerGas(1.GWei)
                .WithMaxPriorityFeePerGas(1.GWei)
                .WithNonce(0)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

            Assert.That(_txPool.SubmitTx(blobTxAdded, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(_txPool.TryGetPendingTransaction(blobTxAdded.Hash!, out Transaction blobTxReturned), Is.True);
            Assert.That(blobTxReturned, Is.EqualTo(blobTxAdded).UsingTransactionComparer());

            Assert.That(blobTxStorage.TryGet(blobTxAdded.Hash, blobTxAdded.SenderAddress!, blobTxAdded.Timestamp, out Transaction blobTxFromDb), Is.EqualTo(isPersistentStorage)); // additional check for persistent db
            if (isPersistentStorage)
            {
                Assert.That(blobTxFromDb, Is.EqualTo(blobTxAdded).UsingTransactionComparer(nameof(Transaction.GasBottleneck), nameof(Transaction.PoolIndex)));
            }
        }

        [Test]
        public void should_not_throw_when_asking_for_non_existing_tx()
        {
            TxPoolConfig txPoolConfig = new() { Size = 10 };
            BlobTxStorage blobTxStorage = new();
            _txPool = CreatePool(txPoolConfig, GetCancunSpecProvider(), txStorage: blobTxStorage);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_txPool.TryGetPendingTransaction(TestItem.KeccakA, out Transaction blobTxReturned), Is.False);
                Assert.That(blobTxReturned, Is.Null);

                Assert.That(blobTxStorage.TryGet(TestItem.KeccakA, TestItem.AddressA, UInt256.One, out Transaction blobTxFromDb), Is.False);
                Assert.That(blobTxFromDb, Is.Null);
            }
        }

        [TestCase(1, null, true)]
        [TestCase(999_999_999, null, true)]
        [TestCase(1_000_000_000, null, true)]
        [TestCase(1_000_000_001, null, true)]
        [TestCase(1, 0ul, true)]
        [TestCase(1_000_000_000, 0ul, true)]
        [TestCase(1, 10ul, false)]
        public void should_allow_to_add_blob_tx_with_MaxPriorityFeePerGas_lower_than_1GWei(int maxPriorityFeePerGas, ulong? configuredMinPriorityFee, bool expectedResult)
        {
            TxPoolConfig txPoolConfig = new() { BlobsSupport = BlobsSupportMode.InMemory, Size = 10 };

            if (configuredMinPriorityFee is not null)
            {
                txPoolConfig.MinBlobTxPriorityFee = configuredMinPriorityFee.Value;
            }

            _txPool = CreatePool(txPoolConfig, GetCancunSpecProvider());
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            Transaction tx = Build.A.Transaction
                .WithShardBlobTxTypeAndFields()
                .WithMaxFeePerGas((UInt256)maxPriorityFeePerGas)
                .WithMaxPriorityFeePerGas((UInt256)maxPriorityFeePerGas)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

            Assert.That(_txPool.SubmitTx(tx, TxHandlingOptions.None), Is.EqualTo(expectedResult
                ? AcceptTxResult.Accepted
                : AcceptTxResult.FeeTooLow));
        }

        [Test]
        public void should_not_allow_to_add_blob_tx_with_MaxFeePerBlobGas_lower_than_CurrentFeePerBlobGas([Values(true, false)] bool isMaxFeePerBlobGasHighEnough, [Values(true, false)] bool isRequirementEnabled)
        {
            ISpecProvider specProvider = GetCancunSpecProvider();
            ChainHeadInfoProvider chainHeadInfoProvider = new(new ChainHeadSpecProvider(specProvider, _blockTree), _blockTree, _stateProvider);

            TxPoolConfig txPoolConfig = new()
            {
                BlobsSupport = BlobsSupportMode.InMemory,
                Size = 10,
                CurrentBlobBaseFeeRequired = isRequirementEnabled
            };

            UInt256 currentFeePerBlobGas = 100;
            chainHeadInfoProvider.CurrentFeePerBlobGas = currentFeePerBlobGas;

            _txPool = CreatePool(config: txPoolConfig,
                specProvider: specProvider,
                chainHeadInfoProvider: chainHeadInfoProvider);
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            Transaction tx = Build.A.Transaction
                .WithShardBlobTxTypeAndFields()
                .WithMaxFeePerBlobGas(isMaxFeePerBlobGasHighEnough ? currentFeePerBlobGas : currentFeePerBlobGas - 1)
                .WithMaxFeePerGas(1.GWei)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

            Assert.That(_txPool.SubmitTx(tx, TxHandlingOptions.None),
                Is.EqualTo(isRequirementEnabled && !isMaxFeePerBlobGasHighEnough
                    ? AcceptTxResult.FeeTooLow
                    : AcceptTxResult.Accepted));
        }

        [Test]
        public void should_allow_nonce_gap_blob_tx_when_blob_pool_has_capacity([Values(true, false)] bool isBlob)
        {
            _txPool = CreatePool(new TxPoolConfig() { BlobsSupport = BlobsSupportMode.InMemory, Size = 128 }, GetCancunSpecProvider());
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            Transaction firstTx = Build.A.Transaction
                .WithType(isBlob ? TxType.Blob : TxType.EIP1559)
                .WithShardBlobTxTypeAndFieldsIfBlobTx()
                .WithMaxFeePerGas(1.GWei)
                .WithMaxPriorityFeePerGas(1.GWei)
                .WithNonce(0)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

            Transaction nonceGapTx = Build.A.Transaction
                .WithType(isBlob ? TxType.Blob : TxType.EIP1559)
                .WithShardBlobTxTypeAndFieldsIfBlobTx()
                .WithMaxFeePerGas(1.GWei)
                .WithMaxPriorityFeePerGas(1.GWei)
                .WithNonce(2)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

            Assert.That(_txPool.SubmitTx(firstTx, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(_txPool.SubmitTx(nonceGapTx, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
        }

        [Test]
        public void should_not_allow_to_have_pending_transactions_of_both_blob_type_and_other([Values(true, false)] bool firstIsBlob, [Values(true, false)] bool secondIsBlob)
        {
            Transaction GetTx(bool isBlob, ulong nonce) => Build.A.Transaction
                    .WithType(isBlob ? TxType.Blob : TxType.EIP1559)
                    .WithShardBlobTxTypeAndFieldsIfBlobTx()
                    .WithMaxFeePerGas(1.GWei)
                    .WithMaxPriorityFeePerGas(1.GWei)
                    .WithNonce(nonce)
                    .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

            _txPool = CreatePool(new TxPoolConfig() { BlobsSupport = BlobsSupportMode.InMemory, Size = 128 }, GetCancunSpecProvider());
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            Transaction firstTx = GetTx(firstIsBlob, 0UL);
            Transaction secondTx = GetTx(secondIsBlob, 1UL);

            Assert.That(_txPool.SubmitTx(firstTx, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(_txPool.SubmitTx(secondTx, TxHandlingOptions.None), Is.EqualTo(firstIsBlob ^ secondIsBlob ? AcceptTxResult.PendingTxsOfConflictingType : AcceptTxResult.Accepted));
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
                .WithNonce(0)
                .WithMaxFeePerGas(1.GWei)
                .WithMaxPriorityFeePerGas(1.GWei)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

            Transaction newTx = Build.A.Transaction
                .WithShardBlobTxTypeAndFields()
                .WithNonce(0)
                .WithMaxFeePerGas(oldTx.MaxFeePerGas * 2)
                .WithMaxPriorityFeePerGas(oldTx.MaxPriorityFeePerGas * 2)
                .WithMaxFeePerBlobGas(oldTx.MaxFeePerBlobGas * 2)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;


            Assert.That(_txPool.SubmitTx(oldTx, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(_txPool.GetPendingBlobTransactionsCount(), Is.EqualTo(1));
            Assert.That(_txPool.TryGetPendingTransaction(oldTx.Hash!, out Transaction blobTxReturned), Is.True);
            Assert.That(blobTxReturned, Is.EqualTo(oldTx).UsingTransactionComparer());
            Assert.That(blobTxStorage.TryGet(oldTx.Hash, oldTx.SenderAddress!, oldTx.Timestamp, out Transaction blobTxFromDb), Is.True);
            Assert.That(blobTxFromDb, Is.EqualTo(oldTx).UsingTransactionComparer(nameof(Transaction.GasBottleneck), nameof(Transaction.PoolIndex)));

            Assert.That(_txPool.SubmitTx(newTx, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(_txPool.GetPendingBlobTransactionsCount(), Is.EqualTo(1));
            Assert.That(_txPool.TryGetPendingTransaction(newTx.Hash!, out blobTxReturned), Is.True);
            Assert.That(blobTxReturned, Is.EqualTo(newTx).UsingTransactionComparer());
            Assert.That(blobTxStorage.TryGet(oldTx.Hash, oldTx.SenderAddress, oldTx.Timestamp, out blobTxFromDb), Is.False);
            Assert.That(blobTxStorage.TryGet(newTx.Hash, newTx.SenderAddress!, newTx.Timestamp, out blobTxFromDb), Is.True);
            Assert.That(blobTxFromDb, Is.EqualTo(newTx).UsingTransactionComparer(nameof(Transaction.GasBottleneck), nameof(Transaction.PoolIndex)));
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
                .WithNonce(0)
                .WithMaxFeePerGas(1.GWei)
                .WithMaxPriorityFeePerGas(1.GWei)
                .WithMaxFeePerBlobGas(UInt256.One)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

            Assert.That(_txPool.SubmitTx(tx, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(_txPool.GetPendingBlobTransactionsCount(), Is.EqualTo(1));
            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(0));

            _txPool.TryGetBlobTxSortingEquivalent(tx.Hash!, out Transaction returned);
            Assert.That(returned, Is.EqualTo(isPersistentStorage ? new LightTransaction(tx) : tx).UsingTransactionComparer());
        }

        [Test]
        public void should_dump_GasBottleneck_of_blob_tx_to_zero_if_MaxFeePerBlobGas_is_lower_than_current([Values(true, false)] bool isBlob, [Values(true, false)] bool isPersistentStorage)
        {
            TxPoolConfig txPoolConfig = new()
            {
                BlobsSupport = isPersistentStorage ? BlobsSupportMode.Storage : BlobsSupportMode.InMemory,
                Size = 10,
                CurrentBlobBaseFeeRequired = false
            };
            _txPool = CreatePool(txPoolConfig, GetCancunSpecProvider());
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            _headInfo.CurrentFeePerBlobGas = UInt256.MaxValue;

            Transaction tx = Build.A.Transaction
                .WithType(isBlob ? TxType.Blob : TxType.EIP1559)
                .WithShardBlobTxTypeAndFieldsIfBlobTx()
                .WithNonce(0)
                .WithMaxFeePerGas(1.GWei)
                .WithMaxPriorityFeePerGas(1.GWei)
                .WithMaxFeePerBlobGas(isBlob ? UInt256.One : null)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

            Assert.That(_txPool.SubmitTx(tx, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(_txPool.GetPendingBlobTransactionsCount(), Is.EqualTo(isBlob ? 1 : 0));
            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(isBlob ? 0 : 1));
            if (isBlob)
            {
                _txPool.TryGetBlobTxSortingEquivalent(tx.Hash!, out Transaction returned);
                Assert.That(returned.GasBottleneck, Is.EqualTo(UInt256.Zero));
                Assert.That(returned, Is.EqualTo(isPersistentStorage ? new LightTransaction(tx) : tx).UsingTransactionComparer(nameof(Transaction.GasBottleneck)));
                Assert.That(returned, Is.Not.EqualTo(isPersistentStorage ? tx : new LightTransaction(tx)));
            }
            else
            {
                Assert.That(_txPool.TryGetPendingTransaction(tx.Hash!, out Transaction eip1559Tx), Is.True);
                Assert.That(eip1559Tx, Is.EqualTo(tx).UsingTransactionComparer());
                Assert.That(eip1559Tx.GasBottleneck, Is.EqualTo(1.GWei));
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
                .WithNonce(0)
                .WithMaxFeePerGas(1.GWei)
                .WithMaxPriorityFeePerGas(1.GWei)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

            Transaction secondTx = Build.A.Transaction
                .WithShardBlobTxTypeAndFields(blobsInSecondTx)
                .WithNonce(0)
                .WithMaxFeePerGas(firstTx.MaxFeePerGas * 2)
                .WithMaxPriorityFeePerGas(firstTx.MaxPriorityFeePerGas * 2)
                .WithMaxFeePerBlobGas(firstTx.MaxFeePerBlobGas * 2)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

            Assert.That(_txPool.SubmitTx(firstTx, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));

            Assert.That(_txPool.GetPendingBlobTransactionsCount(), Is.EqualTo(1));

            Assert.That(_txPool.SubmitTx(secondTx, TxHandlingOptions.None), Is.EqualTo(shouldReplace ? AcceptTxResult.Accepted : AcceptTxResult.ReplacementNotAllowed));
            Assert.That(_txPool.GetPendingBlobTransactionsCount(), Is.EqualTo(1));
            Assert.That(_txPool.TryGetPendingTransaction(firstTx.Hash!, out Transaction returnedFirstTx), Is.EqualTo(!shouldReplace));
            Assert.That(_txPool.TryGetPendingTransaction(secondTx.Hash!, out Transaction returnedSecondTx), Is.EqualTo(shouldReplace));
            if (shouldReplace)
            {
                Assert.That(returnedFirstTx, Is.Null);
                Assert.That(returnedSecondTx, Is.EqualTo(secondTx).UsingTransactionComparer());
            }
            else
            {
                Assert.That(returnedFirstTx, Is.EqualTo(firstTx).UsingTransactionComparer());
                Assert.That(returnedSecondTx, Is.Null);
            }
        }

        [Test]
        public void should_allow_to_replace_blob_tx_by_the_one_with_network_wrapper_in_higher_version()
        {
            // start with Cancun
            OverridableReleaseSpec releaseSpec = new(Cancun.Instance);
            IChainHeadSpecProvider specProvider = Substitute.For<IChainHeadSpecProvider>();
            specProvider.GetCurrentHeadSpec().Returns(releaseSpec);

            ChainHeadInfoProvider chainHeadInfoProvider = new(specProvider, _blockTree, _stateProvider);
            _txPool = CreatePool(new TxPoolConfig() { BlobsSupport = BlobsSupportMode.InMemory, Size = 128 },
                specProvider: specProvider, chainHeadInfoProvider: chainHeadInfoProvider);

            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            Transaction firstTx = Build.A.Transaction
                .WithShardBlobTxTypeAndFields(spec: new ReleaseSpec() { IsEip7594Enabled = false })
                .WithNonce(0)
                .WithMaxFeePerGas(1.GWei)
                .WithMaxPriorityFeePerGas(1.GWei)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

            Transaction secondTx = Build.A.Transaction
                .WithShardBlobTxTypeAndFields(spec: new ReleaseSpec() { IsEip7594Enabled = true })
                .WithNonce(0)
                .WithMaxFeePerGas(2.GWei)
                .WithMaxPriorityFeePerGas(1.GWei)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

            Assert.That(_txPool.SubmitTx(firstTx, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(_txPool.GetPendingBlobTransactionsCount(), Is.EqualTo(1));

            // switch to Osaka
            releaseSpec = new OverridableReleaseSpec(Osaka.Instance);
            specProvider.GetCurrentHeadSpec().Returns(releaseSpec);

            Assert.That(_txPool.SubmitTx(secondTx, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(_txPool.GetPendingBlobTransactionsCount(), Is.EqualTo(1));
            Assert.That(_txPool.TryGetPendingTransaction(firstTx.Hash!, out Transaction returnedFirstTx), Is.False);
            Assert.That(_txPool.TryGetPendingTransaction(secondTx.Hash!, out Transaction returnedSecondTx), Is.True);
            Assert.That(returnedFirstTx, Is.Null);
            Assert.That(returnedSecondTx, Is.EqualTo(secondTx).UsingTransactionComparer());
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
                .WithNonce(0)
                .WithMaxFeePerGas(halfOfMaxGasPriceWithoutOverflow)
                .WithMaxPriorityFeePerGas(halfOfMaxGasPriceWithoutOverflow)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            Assert.That(_txPool.SubmitTx(firstTransaction, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Accepted));

            Transaction transactionWithPotentialOverflow = Build.A.Transaction
                .WithShardBlobTxTypeAndFields()
                .WithMaxFeePerBlobGas(supportsBlobs
                    ? UInt256.One
                    : UInt256.Zero)
                .WithNonce(1)
                .WithMaxFeePerGas(halfOfMaxGasPriceWithoutOverflow)
                .WithMaxPriorityFeePerGas(halfOfMaxGasPriceWithoutOverflow)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

            Assert.That(_txPool.SubmitTx(transactionWithPotentialOverflow, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(supportsBlobs ? AcceptTxResult.Int256Overflow : AcceptTxResult.Accepted));
        }

        [Test]
        public async Task should_allow_to_have_pending_transaction_of_other_type_if_conflicting_one_was_included([Values(true, false)] bool firstIsBlob, [Values(true, false)] bool secondIsBlob)
        {
            Transaction GetTx(bool isBlob, ulong nonce) => Build.A.Transaction
                    .WithType(isBlob ? TxType.Blob : TxType.EIP1559)
                    .WithShardBlobTxTypeAndFieldsIfBlobTx()
                    .WithMaxFeePerGas(1.GWei)
                    .WithMaxPriorityFeePerGas(1.GWei)
                    .WithNonce(nonce)
                    .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

            _txPool = CreatePool(new TxPoolConfig() { BlobsSupport = BlobsSupportMode.InMemory, Size = 128 }, GetCancunSpecProvider());
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            Transaction firstTx = GetTx(firstIsBlob, 0UL);
            Transaction secondTx = GetTx(secondIsBlob, 1UL);

            Assert.That(_txPool.SubmitTx(firstTx, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));

            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(firstIsBlob ? 0 : 1));
            Assert.That(_txPool.GetPendingBlobTransactionsCount(), Is.EqualTo(firstIsBlob ? 1 : 0));
            _stateProvider.IncrementNonce(TestItem.AddressA);
            Block block = Build.A.Block.WithNumber(1).TestObject;
            await RaiseBlockAddedToMainAndWaitForNewHead(block);

            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(0));
            Assert.That(_txPool.GetPendingBlobTransactionsCount(), Is.EqualTo(0));
            Assert.That(_txPool.SubmitTx(secondTx, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(secondIsBlob ? 0 : 1));
            Assert.That(_txPool.GetPendingBlobTransactionsCount(), Is.EqualTo(secondIsBlob ? 1 : 0));
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
            Assert.That(blobTx.GetLength(), Is.EqualTo(expectedLength));
        }

        [Test]
        public void RecoverAddress_should_work_correctly()
        {
            Transaction tx = CreateBlobTx(TestItem.PrivateKeyA);
            Assert.That(_ethereumEcdsa.RecoverAddress(tx), Is.EqualTo(tx.SenderAddress));
        }

        [Test]
        public async Task should_add_processed_txs_to_db()
        {
            const ulong blockNumber = 358;

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

            Assert.That(_txPool.SubmitTx(txs[0], TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(_txPool.SubmitTx(txs[1], TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));

            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(0));
            Assert.That(_txPool.GetPendingBlobTransactionsCount(), Is.EqualTo(txs.Length));
            _stateProvider.IncrementNonce(TestItem.AddressA);
            _stateProvider.IncrementNonce(TestItem.AddressB);

            Block block = Build.A.Block.WithNumber(blockNumber).WithTransactions(txs).TestObject;

            await RaiseBlockAddedToMainAndWaitForTransactions(txs.Length, block);

            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(0));
            Assert.That(_txPool.GetPendingBlobTransactionsCount(), Is.EqualTo(0));

            Assert.That(blobTxStorage.TryGetBlobTransactionsFromBlock(blockNumber, out Transaction[] returnedTxs), Is.True);
            Assert.That(returnedTxs.Length, Is.EqualTo(txs.Length));
            Assert.That(returnedTxs, Is.EquivalentTo(txs).UsingTransactionComparer(nameof(Transaction.GasBottleneck), nameof(Transaction.PoolIndex), nameof(Transaction.SenderAddress)));

            blobTxStorage.DeleteBlobTransactionsFromBlock(blockNumber);
            Assert.That(blobTxStorage.TryGetBlobTransactionsFromBlock(blockNumber, out returnedTxs), Is.False);
        }

        [Test]
        public async Task should_bring_back_reorganized_blob_txs()
        {
            const ulong blockNumber = 358;

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

            Assert.That(_txPool.SubmitTx(txsA[0], TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(_txPool.SubmitTx(txsA[1], TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(_txPool.SubmitTx(txsB[0], TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));

            Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(0));
            Assert.That(_txPool.GetPendingBlobTransactionsCount(), Is.EqualTo(txsA.Length + txsB.Length));

            // adding block A
            Block blockA = Build.A.Block.WithNumber(blockNumber).WithTransactions(txsA).TestObject;
            await RaiseBlockAddedToMainAndWaitForNewHead(blockA);

            Assert.That(_txPool.GetPendingBlobTransactionsCount(), Is.EqualTo(txsB.Length));
            Assert.That(_txPool.TryGetPendingBlobTransaction(txsA[0].Hash!, out _), Is.False);
            Assert.That(_txPool.TryGetPendingBlobTransaction(txsA[1].Hash!, out _), Is.False);
            Assert.That(_txPool.TryGetPendingBlobTransaction(txsB[0].Hash!, out _), Is.True);

            // reorganized from block A to block B
            Block blockB = Build.A.Block.WithNumber(blockNumber).WithTransactions(txsB).TestObject;
            await RaiseBlockAddedToMainAndWaitForNewHead(blockB, blockA);

            // tx from block B should be removed from blob pool, but present in processed txs db
            Assert.That(_txPool.TryGetPendingBlobTransaction(txsB[0].Hash!, out _), Is.False);
            Assert.That(blobTxStorage.TryGetBlobTransactionsFromBlock(blockNumber, out Transaction[] blockBTxs), Is.True);
            Assert.That(txsB, Is.EquivalentTo(blockBTxs).UsingTransactionComparer(nameof(Transaction.GasBottleneck), nameof(Transaction.PoolIndex), nameof(Transaction.SenderAddress)));

            // blob txs from reorganized blockA should be readded to blob pool
            Assert.That(_txPool.GetPendingBlobTransactionsCount(), Is.EqualTo(txsA.Length));
            Assert.That(_txPool.TryGetPendingBlobTransaction(txsA[0].Hash!, out Transaction tx1), Is.True);
            Assert.That(_txPool.TryGetPendingBlobTransaction(txsA[1].Hash!, out Transaction tx2), Is.True);

            Assert.That(tx1, Is.EqualTo(txsA[0]).UsingTransactionComparer(nameof(Transaction.GasBottleneck), nameof(Transaction.PoolIndex)));

            Assert.That(tx2, Is.EqualTo(txsA[1]).UsingTransactionComparer(nameof(Transaction.GasBottleneck), nameof(Transaction.PoolIndex)));
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
                    .WithMaxFeePerGas(1.GWei + (UInt256)i)
                    .WithMaxPriorityFeePerGas(1.GWei + (UInt256)i)
                    .WithMaxFeePerBlobGas(1000.Wei + (UInt256)i)
                    .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeys[i]).TestObject;

                // making blobs unique. Otherwise, all txs have the same blob
                if (uniqueBlobs)
                {
                    byte[] blobs = new byte[Ckzg.BytesPerBlob];
                    blobs[0] = (byte)(i % 256);

                    ShardBlobNetworkWrapper networkWrapper = blobProofsBuilder.AllocateWrapper(blobs);
                    blobProofsBuilder.ComputeProofsAndCommitments(networkWrapper);
                    byte[][] hashes = blobProofsBuilder.ComputeHashes(networkWrapper);

                    blobTxs[i].NetworkWrapper = networkWrapper;
                    blobTxs[i].BlobVersionedHashes = hashes;
                }

                Assert.That(blobPool.TryInsert(blobTxs[i].Hash, blobTxs[i], out _), Is.True);
            }

            Assert.That(blobPool.BlobIndex.Count, Is.EqualTo(uniqueBlobs ? poolSize : 1));

            // first half of txs (0, poolSize - 1) was evicted and should be removed from index
            // second half (poolSize, 2x poolSize - 1) should be indexed
            for (int i = 0; i < poolSize * 2; i++)
            {
                // if blobs are unique, we expect index to have 10 keys (poolSize, 2x poolSize - 1) with 1 value each
                if (uniqueBlobs)
                {
                    Assert.That(blobPool.BlobIndex.TryGetValue(blobTxs[i].BlobVersionedHashes[0]!, out List<Hash256> txHashes), Is.EqualTo(i >= poolSize));
                    if (i >= poolSize)
                    {
                        Assert.That(txHashes.Count, Is.EqualTo(1));
                        Assert.That(txHashes[0], Is.EqualTo(blobTxs[i].Hash));
                    }
                }
                // if blobs are not unique, we expect index to have 1 key with 10 values (poolSize, 2x poolSize - 1)
                else
                {
                    Assert.That(blobPool.BlobIndex.TryGetValue(blobTxs[i].BlobVersionedHashes[0]!, out List<Hash256> values), Is.True);
                    Assert.That(values.Count, Is.EqualTo(poolSize));
                    Assert.That(values.Contains(blobTxs[i].Hash), Is.EqualTo(i >= poolSize));
                }
            }
        }

        [Test]
        [Repeat(3)]
        public void should_handle_indexing_blobs_when_adding_txs_in_parallel([Values(true, false)] bool isPersistentStorage)
        {
            const int txsPerSender = 10;
            PrivateKey[] testPrivateKeys = TestItem.PrivateKeys[..64];
            int poolSize = testPrivateKeys.Length * txsPerSender;
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

            foreach (PrivateKey privateKey in testPrivateKeys)
            {
                EnsureSenderBalance(privateKey.Address, UInt256.MaxValue);
            }

            // adding, getting and removing txs in parallel
            Parallel.ForEach(testPrivateKeys, privateKey =>
            {
                for (int i = 0; i < txsPerSender; i++)
                {
                    Transaction tx = Build.A.Transaction
                        .WithNonce(i)
                        .WithShardBlobTxTypeAndFields()
                        .WithMaxFeePerGas(1.GWei)
                        .WithMaxPriorityFeePerGas(1.GWei)
                        .WithMaxFeePerBlobGas(1000.Wei)
                        .SignedAndResolved(_ethereumEcdsa, privateKey).TestObject;

                    expectedBlobVersionedHash ??= tx.BlobVersionedHashes[0]!;

                    Assert.That(blobPool.TryInsert(tx.Hash, tx, out _), Is.True);

                    for (int j = 0; j < 100; j++)
                    {
                        Assert.That(blobPool.TryGetBlobAndProofV0(expectedBlobVersionedHash.ToBytes(), out _, out _), Is.True);
                    }

                    // removing 50% of txs
                    if (i % 2 == 0) Assert.That(blobPool.TryRemove(tx.Hash, out _), Is.True);
                }
            });

            // we expect index to have 1 key with poolSize/2 values (50% of txs were removed)
            Assert.That(blobPool.BlobIndex.Count, Is.EqualTo(1));
            Assert.That(blobPool.BlobIndex.TryGetValue(expectedBlobVersionedHash, out List<Hash256> values), Is.True);
            Assert.That(values.Count, Is.EqualTo(poolSize / 2));
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
                .WithMaxFeePerGas(1.GWei)
                .WithMaxPriorityFeePerGas(1.GWei)
                .WithNonce(0)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

            AcceptTxResult result = _txPool.SubmitTx(blobTxAdded, TxHandlingOptions.None);
            Assert.That(result, Is.EqualTo(isTxValid ? AcceptTxResult.Accepted : AcceptTxResult.Invalid));
            Assert.That(_txPool.TryGetPendingTransaction(blobTxAdded.Hash!, out Transaction blobTxReturned), Is.EqualTo(isTxValid));

            if (isTxValid)
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(blobTxReturned, Is.EqualTo(blobTxAdded).UsingTransactionComparer());
                    ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)blobTxReturned.NetworkWrapper;
                    Assert.That(wrapper.Proofs.Length, Is.EqualTo(isOsakaActivated ? Ckzg.CellsPerExtBlob : 1));
                    Assert.That(wrapper.Version, Is.EqualTo(hasTxCellProofs ? ProofVersion.V1 : ProofVersion.V0));

                    Assert.That(blobTxStorage.TryGet(blobTxAdded.Hash, blobTxAdded.SenderAddress!, blobTxAdded.Timestamp, out Transaction blobTxFromDb), Is.EqualTo(isPersistentStorage)); // additional check for persistent db
                    if (isPersistentStorage)
                    {
                        Assert.That(blobTxFromDb, Is.EqualTo(blobTxAdded).UsingTransactionComparer(nameof(Transaction.GasBottleneck), nameof(Transaction.PoolIndex)));
                    }
                }
            }
            else
            {
                Assert.That(blobTxReturned, Is.Null);
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
            _blockTree.RaiseBlockAddedToMain(new BlockReplacementEventArgs(Build.A.Block.TestObject));

            Transaction blobTxAdded = Build.A.Transaction
                .WithShardBlobTxTypeAndFields()
                .WithMaxFeePerGas(1.GWei)
                .WithMaxPriorityFeePerGas(1.GWei)
                .WithNonce(0)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

            AcceptTxResult result = _txPool.SubmitTx(blobTxAdded, TxHandlingOptions.None);
            Assert.That(result, Is.EqualTo(isTxValid ? AcceptTxResult.Accepted : AcceptTxResult.Invalid));
            Assert.That(_txPool.TryGetPendingTransaction(blobTxAdded.Hash!, out Transaction blobTxReturned), Is.EqualTo(isTxValid));

            if (isTxValid)
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(blobTxReturned, Is.EqualTo(blobTxAdded).UsingTransactionComparer());
                    ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)blobTxReturned.NetworkWrapper;
                    Assert.That(wrapper.Proofs.Length, Is.EqualTo(isOsakaActivated ? Ckzg.CellsPerExtBlob : 1));
                    Assert.That(wrapper.Version, Is.EqualTo(isOsakaActivated ? ProofVersion.V1 : ProofVersion.V0));

                    Assert.That(blobTxStorage.TryGet(blobTxAdded.Hash, blobTxAdded.SenderAddress!, blobTxAdded.Timestamp, out Transaction blobTxFromDb), Is.EqualTo(isPersistentStorage)); // additional check for persistent db
                    if (isPersistentStorage)
                    {
                        Assert.That(blobTxFromDb, Is.EqualTo(blobTxAdded).UsingTransactionComparer(nameof(Transaction.GasBottleneck), nameof(Transaction.PoolIndex)));
                    }
                }
            }
            else
            {
                Assert.That(blobTxReturned, Is.Null);
            }
        }

        [Test]
        public void should_convert_cell_proofs_to_blob_proofs_if_enabled([Values(true, false)] bool isPersistentStorage, [Values(true, false)] bool isConversionEnabled)
        {
            TxPoolConfig txPoolConfig = new()
            {
                BlobsSupport = isPersistentStorage ? BlobsSupportMode.Storage : BlobsSupportMode.InMemory,
                Size = 10,
                ProofsTranslationEnabled = isConversionEnabled
            };
            BlobTxStorage blobTxStorage = new();

            _txPool = CreatePool(txPoolConfig, GetCancunSpecProvider(), txStorage: blobTxStorage);

            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            // update head and set correct current proof version
            _blockTree.RaiseBlockAddedToMain(new BlockReplacementEventArgs(Build.A.Block.TestObject));

            Transaction blobTxAdded = Build.A.Transaction
                .WithShardBlobTxTypeAndFields(spec: new ReleaseSpec() { IsEip7594Enabled = true })
                .WithMaxFeePerGas(1.GWei)
                .WithMaxPriorityFeePerGas(1.GWei)
                .WithNonce(0)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

            AcceptTxResult result = _txPool.SubmitTx(blobTxAdded, TxHandlingOptions.None);
            Assert.That(result, Is.EqualTo(isConversionEnabled ? AcceptTxResult.Accepted : AcceptTxResult.Invalid));
            Assert.That(_txPool.TryGetPendingTransaction(blobTxAdded.Hash!, out Transaction blobTxReturned), Is.EqualTo(isConversionEnabled));

            if (isConversionEnabled)
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(blobTxReturned, Is.EqualTo(blobTxAdded).UsingTransactionComparer());
                    ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)blobTxReturned.NetworkWrapper;
                    Assert.That(wrapper.Proofs.Length, Is.EqualTo(1));
                    Assert.That(wrapper.Version, Is.EqualTo(ProofVersion.V0));
                    Assert.That(IBlobProofsManager.For(ProofVersion.V0).ValidateProofs(wrapper), Is.True);

                    Assert.That(blobTxStorage.TryGet(blobTxAdded.Hash, blobTxAdded.SenderAddress!, blobTxAdded.Timestamp, out Transaction blobTxFromDb), Is.EqualTo(isPersistentStorage)); // additional check for persistent db
                    if (isPersistentStorage)
                    {
                        Assert.That(blobTxFromDb, Is.EqualTo(blobTxAdded).UsingTransactionComparer(nameof(Transaction.GasBottleneck), nameof(Transaction.PoolIndex)));
                    }
                }
            }
            else
            {
                Assert.That(blobTxReturned, Is.Null);
            }
        }

        [Test]
        public void should_reject_malformed_blob_proofs_when_conversion_is_enabled()
        {
            TxPoolConfig txPoolConfig = new()
            {
                BlobsSupport = BlobsSupportMode.InMemory,
                Size = 10,
                ProofsTranslationEnabled = true
            };

            _txPool = CreatePool(txPoolConfig, GetOsakaSpecProvider());

            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            _blockTree.RaiseBlockAddedToMain(new BlockReplacementEventArgs(Build.A.Block.TestObject));

            Transaction blobTxAdded = Build.A.Transaction
                .WithShardBlobTxTypeAndFields()
                .WithMaxFeePerGas(1.GWei)
                .WithMaxPriorityFeePerGas(1.GWei)
                .WithNonce(0)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)blobTxAdded.NetworkWrapper;
            blobTxAdded.NetworkWrapper = wrapper with { Proofs = [] };

            AcceptTxResult result = _txPool.SubmitTx(blobTxAdded, TxHandlingOptions.None);

            Assert.That(result, Is.EqualTo(AcceptTxResult.Invalid));
            Assert.That(_txPool.TryGetPendingTransaction(blobTxAdded.Hash!, out Transaction blobTxReturned), Is.False);
            Assert.That(blobTxReturned, Is.Null);
        }

        [Test]
        public void should_reject_malformed_cell_proofs_when_conversion_is_enabled()
        {
            TxPoolConfig txPoolConfig = new()
            {
                BlobsSupport = BlobsSupportMode.InMemory,
                Size = 10,
                ProofsTranslationEnabled = true
            };

            _txPool = CreatePool(txPoolConfig, GetCancunSpecProvider());

            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            _blockTree.RaiseBlockAddedToMain(new BlockReplacementEventArgs(Build.A.Block.TestObject));

            Transaction blobTxAdded = Build.A.Transaction
                .WithShardBlobTxTypeAndFields(spec: new ReleaseSpec() { IsEip7594Enabled = true })
                .WithMaxFeePerGas(1.GWei)
                .WithMaxPriorityFeePerGas(1.GWei)
                .WithNonce(0)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)blobTxAdded.NetworkWrapper;
            blobTxAdded.NetworkWrapper = wrapper with { Commitments = [] };

            AcceptTxResult result = _txPool.SubmitTx(blobTxAdded, TxHandlingOptions.None);

            Assert.That(result, Is.EqualTo(AcceptTxResult.Invalid));
            Assert.That(_txPool.TryGetPendingTransaction(blobTxAdded.Hash!, out Transaction blobTxReturned), Is.False);
            Assert.That(blobTxReturned, Is.Null);
        }

        [TestCaseSource(nameof(BlobScheduleActivationsTestCaseSource))]
        public async Task<int> should_evict_based_on_proof_version_and_fork(BlobsSupportMode poolMode, TestAction[] testActions)
        {
            Block head = _blockTree.Head;
            _blockTree.BestSuggestedHeader = head.Header;

            (ChainSpecBasedSpecProvider provider, _) = TestSpecHelper.LoadChainSpec(new ChainSpecJson
            {
                Params = new ChainSpecParamsJson
                {
                    Eip4844TransitionTimestamp = head.Timestamp,
                    Eip7002TransitionTimestamp = head.Timestamp,
                    Eip7594TransitionTimestamp = head.Timestamp + 1,
                }
            });

            ulong nonce = 0;

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
                        await AddEmptyBlock();
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
                static TestCaseData MakeTestCase(string testName, int finalCount, BlobsSupportMode mode, params TestAction[] testActions)
                    => new(mode, testActions) { TestName = $"EvictProofVersion({mode}): {testName}", ExpectedResult = finalCount };

                foreach (BlobsSupportMode mode in new[] { BlobsSupportMode.InMemory, BlobsSupportMode.Storage })
                {
                    yield return MakeTestCase("V0 should be evicted in Osaka", 0, mode, TestAction.AddV0, TestAction.Fork);
                    yield return MakeTestCase("Take only V0 ones before Osaka", 2, mode, TestAction.AddV0, TestAction.AddV0, TestAction.AddV1);
                    yield return MakeTestCase("Evict old proof but keep later sparse txs", 1, mode, TestAction.AddV0, TestAction.AddV0, TestAction.Fork, TestAction.AddV1);
                    yield return MakeTestCase("Replace with new proof", 1, mode, TestAction.AddV0, TestAction.Fork, TestAction.ResetNonce, TestAction.AddV1);
                    yield return MakeTestCase("Ignore V1 before Osaka, no gaps", 0, mode, TestAction.AddV1);
                }
            }
        }

        private async Task AddEmptyBlock()
        {
            BlockHeader bh = new(_blockTree.Head.Hash, Keccak.EmptyTreeHash, TestItem.AddressA, 0, _blockTree.Head.Number + 1, _blockTree.Head.GasLimit, _blockTree.Head.Timestamp + 1, []);
            _blockTree.BestSuggestedHeader = bh;
            Block block = new(bh, new BlockBody([], []));
            await RaiseBlockAddedToMainAndWaitForNewHead(block, _blockTree.Head);
        }

        private Transaction CreateBlobTx(PrivateKey sender, ulong nonce = default, int blobCount = 1, IReleaseSpec releaseSpec = default) => Build.A.Transaction
                .WithShardBlobTxTypeAndFields(blobCount: blobCount, spec: releaseSpec)
                .WithMaxFeePerGas(1.GWei)
                .WithMaxPriorityFeePerGas(1.GWei)
                .WithNonce(nonce)
                .SignedAndResolved(_ethereumEcdsa, sender).TestObject;

        [Test]
        public async Task should_evict_txs_with_too_many_blobs_per_tx_after_fork()
        {
            const int regularMaxBlobCount = 9;

            TestSpecProvider provider = new(new ReleaseSpec
            {
                IsEip4844Enabled = true,
                MaxBlobCount = regularMaxBlobCount,
            })
            {
                NextForkSpec = new ReleaseSpec
                {
                    IsEip4844Enabled = true,
                    IsEip7594Enabled = true,
                    MaxBlobCount = regularMaxBlobCount,
                },
                ForkOnBlockNumber = _blockTree.Head!.Number + 1,
            };

            Block head = _blockTree.Head;
            _blockTree.BestSuggestedHeader = head.Header;

            _txPool = CreatePool(specProvider: provider);
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            _txPool.SubmitTx(CreateBlobTx(TestItem.PrivateKeyA, 0, regularMaxBlobCount), TxHandlingOptions.None);
            Assert.That(_txPool.GetPendingBlobTransactionsCount(), Is.EqualTo(1));

            await AddEmptyBlock();

            Assert.That(_txPool.GetPendingBlobTransactionsCount(), Is.Zero);
        }

        [Test]
        public async Task should_evict_txs_with_too_many_blobs_per_block_after_fork()
        {
            const int regularMaxBlobCount = 9;
            const int decreasedMaxBlobCount = regularMaxBlobCount - 1;

            TestSpecProvider provider = new(new ReleaseSpec
            {
                IsEip4844Enabled = true,
                MaxBlobCount = regularMaxBlobCount,
            })
            {
                NextForkSpec = new ReleaseSpec
                {
                    IsEip4844Enabled = true,
                    MaxBlobCount = decreasedMaxBlobCount,
                },
                ForkOnBlockNumber = _blockTree.Head!.Number + 1,
            };

            Block head = _blockTree.Head;
            _blockTree.BestSuggestedHeader = head.Header;

            _txPool = CreatePool(specProvider: provider);
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            _txPool.SubmitTx(CreateBlobTx(TestItem.PrivateKeyA, 0, regularMaxBlobCount), TxHandlingOptions.None);
            Assert.That(_txPool.GetPendingBlobTransactionsCount(), Is.EqualTo(1));

            await AddEmptyBlock();

            Assert.That(_txPool.GetPendingBlobTransactionsCount(), Is.Zero);
        }

        [Test]
        public void max_blobs_per_tx_should_not_exceed_max_blobs_per_block()
        {
            const ulong regularMaxBlobCount = Eip7594Constants.MaxBlobsPerTx - 1;

            TestSpecProvider provider = new(new ReleaseSpec
            {
                IsEip4844Enabled = true,
                IsEip7594Enabled = true,
                MaxBlobCount = regularMaxBlobCount,
            });

            ulong maxBlobsPerTx = provider.GetSpec(_blockTree.Head!.Header).MaxBlobsPerTx;

            Assert.That(maxBlobsPerTx, Is.EqualTo(regularMaxBlobCount));
        }

        [Test]
        public void should_batch_return_blobs_and_proofs_v1_from_persistent_storage()
        {
            // BlobCacheSize = 1 forces cache eviction after the first insert,
            // so the second tx must be fetched via TryGetMany (Phase 2 DB path).
            TxPoolConfig txPoolConfig = new()
            {
                BlobsSupport = BlobsSupportMode.Storage,
                BlobCacheSize = 1,
                Size = 10
            };
            BlobTxStorage blobTxStorage = new();
            _txPool = CreatePool(txPoolConfig, GetOsakaSpecProvider(), txStorage: blobTxStorage);
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.AddressB, UInt256.MaxValue);

            Transaction tx1 = Build.A.Transaction
                .WithShardBlobTxTypeAndFields(spec: new ReleaseSpec() { IsEip7594Enabled = true })
                .WithMaxFeePerGas(1.GWei)
                .WithMaxPriorityFeePerGas(1.GWei)
                .WithNonce(0)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

            Transaction tx2 = Build.A.Transaction
                .WithShardBlobTxTypeAndFields(spec: new ReleaseSpec() { IsEip7594Enabled = true })
                .WithMaxFeePerGas(1.GWei)
                .WithMaxPriorityFeePerGas(1.GWei)
                .WithNonce(0)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyB).TestObject;

            Assert.That(_txPool.SubmitTx(tx1, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(_txPool.SubmitTx(tx2, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));

            // tx1 was evicted from cache (size=1) when tx2 was inserted,
            // so at least one must come from DB via TryGetMany
            byte[][] requestedHashes = [tx1.BlobVersionedHashes![0]!, tx2.BlobVersionedHashes![0]!];
            byte[][] blobs = new byte[2][];
            ReadOnlyMemory<byte[]>[] proofs = new ReadOnlyMemory<byte[]>[2];

            int found = _txPool.TryGetBlobsAndProofsV1(requestedHashes, blobs, proofs);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(found, Is.EqualTo(2));
                Assert.That(blobs[0], Is.Not.Null);
                Assert.That(blobs[1], Is.Not.Null);
                Assert.That(proofs[0].Length, Is.EqualTo(Ckzg.CellsPerExtBlob));
                Assert.That(proofs[1].Length, Is.EqualTo(Ckzg.CellsPerExtBlob));
            }
        }

        [Test]
        public void should_batch_return_partial_blobs_when_some_missing()
        {
            TxPoolConfig txPoolConfig = new()
            {
                BlobsSupport = BlobsSupportMode.Storage,
                BlobCacheSize = 1,
                Size = 10
            };
            BlobTxStorage blobTxStorage = new();
            _txPool = CreatePool(txPoolConfig, GetOsakaSpecProvider(), txStorage: blobTxStorage);
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            Transaction tx1 = Build.A.Transaction
                .WithShardBlobTxTypeAndFields(spec: new ReleaseSpec() { IsEip7594Enabled = true })
                .WithMaxFeePerGas(1.GWei)
                .WithMaxPriorityFeePerGas(1.GWei)
                .WithNonce(0)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

            Assert.That(_txPool.SubmitTx(tx1, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));

            byte[] fakeBlobHash = new byte[32];
            fakeBlobHash[0] = 0x01; // versioned hash prefix
            byte[][] requestedHashes = [tx1.BlobVersionedHashes![0]!, fakeBlobHash];
            byte[][] blobs = new byte[2][];
            ReadOnlyMemory<byte[]>[] proofs = new ReadOnlyMemory<byte[]>[2];

            int found = _txPool.TryGetBlobsAndProofsV1(requestedHashes, blobs, proofs);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(found, Is.EqualTo(1));
                Assert.That(blobs[0], Is.Not.Null);
                Assert.That(blobs[1], Is.Null);
            }
        }

        [Test]
        public void should_return_blob_cells_from_persistent_storage_after_cache_eviction()
        {
            TxPoolConfig txPoolConfig = new()
            {
                BlobsSupport = BlobsSupportMode.Storage,
                BlobCacheSize = 1,
                Size = 10
            };
            BlobTxStorage blobTxStorage = new();
            _txPool = CreatePool(txPoolConfig, GetOsakaSpecProvider(), txStorage: blobTxStorage);
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.AddressB, UInt256.MaxValue);

            Transaction tx1 = Build.A.Transaction
                .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
                .WithMaxFeePerGas(1.GWei)
                .WithMaxPriorityFeePerGas(1.GWei)
                .WithNonce(0UL)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

            Transaction tx2 = Build.A.Transaction
                .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
                .WithMaxFeePerGas(1.GWei)
                .WithMaxPriorityFeePerGas(1.GWei)
                .WithNonce(0UL)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyB).TestObject;

            Assert.That(_txPool.SubmitTx(tx1, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(_txPool.SubmitTx(tx2, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));

            BlobCellMask requestedMask = BlobCellMask.FromIndices([3]);
            Assert.That(_txPool.TryGetBlobCells(tx1.Hash!, requestedMask, out BlobCellMask availableMask, out byte[][] cells), Is.True);
            Assert.That(availableMask, Is.EqualTo(requestedMask));
            Assert.That(cells, Is.Not.Null);
            Assert.That(cells!.Length, Is.EqualTo(tx1.BlobVersionedHashes!.Length * requestedMask.Count));

            Assert.That(_txPool.TryGetBlobCellsAndProofsV1(tx1.BlobVersionedHashes[0], requestedMask, out availableMask, out cells, out byte[][] proofs), Is.True);
            Assert.That(availableMask, Is.EqualTo(requestedMask));
            Assert.That(cells, Is.Not.Null);
            Assert.That(cells!.Length, Is.EqualTo(requestedMask.Count));
            Assert.That(proofs, Is.Not.Null);
            Assert.That(proofs!.Length, Is.EqualTo(requestedMask.Count));
        }

        [Test]
        public void should_use_later_candidate_when_first_matching_blob_hash_lacks_requested_cells([Values(true, false)] bool isPersistentStorage)
        {
            TxPoolConfig txPoolConfig = new()
            {
                BlobsSupport = isPersistentStorage ? BlobsSupportMode.Storage : BlobsSupportMode.InMemory,
                BlobCacheSize = 1,
                Size = 10
            };
            _txPool = CreatePool(txPoolConfig, GetOsakaSpecProvider(), txStorage: new BlobTxStorage());
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.AddressB, UInt256.MaxValue);

            Transaction txWithSparseCells = Build.A.Transaction
                .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
                .WithMaxFeePerGas(1.GWei)
                .WithMaxPriorityFeePerGas(1.GWei)
                .WithNonce(0UL)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

            Transaction txWithFullBlob = Build.A.Transaction
                .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
                .WithMaxFeePerGas(1.GWei)
                .WithMaxPriorityFeePerGas(1.GWei)
                .WithNonce(0UL)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyB).TestObject;

            Assert.That(txWithFullBlob.BlobVersionedHashes![0], Is.EqualTo(txWithSparseCells.BlobVersionedHashes![0]));

            ShardBlobNetworkWrapper fullWrapper = (ShardBlobNetworkWrapper)txWithSparseCells.NetworkWrapper!;
            BlobCellMask sparseMask = BlobCellMask.FromIndices([1]);
            Assert.That(BlobCellsHelper.TryGetFlattenedCells(fullWrapper, sparseMask, out byte[][] sparseCells), Is.True);
            byte[][] emptyBlobs = new byte[fullWrapper.Blobs.Length][];
            Array.Fill(emptyBlobs, []);
            txWithSparseCells.NetworkWrapper = fullWrapper with
            {
                Blobs = emptyBlobs,
                CellMask = sparseMask,
                Cells = sparseCells,
            };
            txWithSparseCells.ClearLengthCache();

            Assert.That(_txPool.SubmitTx(txWithSparseCells, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(_txPool.SubmitTx(txWithFullBlob, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));

            BlobCellMask requestedMask = BlobCellMask.FromIndices([3]);
            Assert.That(_txPool.TryGetBlobCellsAndProofsV1(txWithSparseCells.BlobVersionedHashes[0], requestedMask, out BlobCellMask availableMask, out byte[][] cells, out byte[][] proofs), Is.True);
            Assert.That(availableMask, Is.EqualTo(requestedMask));
            Assert.That(cells, Is.Not.Null);
            Assert.That(cells.Length, Is.EqualTo(requestedMask.Count));
            Assert.That(proofs, Is.Not.Null);
            Assert.That(proofs.Length, Is.EqualTo(requestedMask.Count));
        }

        [Test]
        public void should_use_persisted_full_candidate_after_more_than_four_sparse_cache_misses()
        {
            const int sparseCandidateCount = 5;
            TxPoolConfig txPoolConfig = new()
            {
                BlobsSupport = BlobsSupportMode.Storage,
                BlobCacheSize = 1,
                Size = 10
            };
            IComparer<Transaction> comparer = new TransactionComparerProvider(_specProvider, _blockTree).GetDefaultComparer();
            PersistentBlobTxDistinctSortedPool blobPool = new(new BlobTxStorage(), txPoolConfig, comparer, LimboLogs.Instance);
            BlobCellMask sparseMask = BlobCellMask.FromIndices([1]);
            byte[] requestedBlobVersionedHash = null!;

            for (int i = 0; i < sparseCandidateCount; i++)
            {
                Transaction sparseTx = Build.A.Transaction
                    .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
                    .WithMaxFeePerGas(1.GWei + (UInt256)i)
                    .WithMaxPriorityFeePerGas(1.GWei + (UInt256)i)
                    .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeys[i]).TestObject;

                ConvertToSparseBlobTransaction(sparseTx, sparseMask);
                requestedBlobVersionedHash ??= sparseTx.BlobVersionedHashes![0];
                Assert.That(sparseTx.BlobVersionedHashes![0], Is.EqualTo(requestedBlobVersionedHash));
                Assert.That(blobPool.TryInsert(sparseTx.Hash, sparseTx, out _), Is.True);
            }

            Transaction fullTx = Build.A.Transaction
                .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
                .WithMaxFeePerGas(1.GWei + (UInt256)sparseCandidateCount)
                .WithMaxPriorityFeePerGas(1.GWei + (UInt256)sparseCandidateCount)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeys[sparseCandidateCount]).TestObject;
            Assert.That(fullTx.BlobVersionedHashes![0], Is.EqualTo(requestedBlobVersionedHash));
            Assert.That(blobPool.TryInsert(fullTx.Hash, fullTx, out _), Is.True);

            Transaction cacheEvictor = Build.A.Transaction
                .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
                .WithMaxFeePerGas(1.GWei + (UInt256)(sparseCandidateCount + 1))
                .WithMaxPriorityFeePerGas(1.GWei + (UInt256)(sparseCandidateCount + 1))
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeys[sparseCandidateCount + 1]).TestObject;
            ReplaceBlobSidecar(cacheEvictor, firstBlobByte: 42);
            Assert.That(cacheEvictor.BlobVersionedHashes![0], Is.Not.EqualTo(requestedBlobVersionedHash));
            Assert.That(blobPool.TryInsert(cacheEvictor.Hash, cacheEvictor, out _), Is.True);

            byte[][] requestedHashes = [requestedBlobVersionedHash!];
            byte[][] blobs = new byte[1][];
            ReadOnlyMemory<byte[]>[] proofs = new ReadOnlyMemory<byte[]>[1];

            int found = blobPool.TryGetBlobsAndProofsV1(requestedHashes, blobs, proofs);

            Assert.That(found, Is.EqualTo(1));
            Assert.That(blobs[0], Is.Not.Null);
            Assert.That(proofs[0].Length, Is.EqualTo(Ckzg.CellsPerExtBlob));
        }

        [Test]
        public void should_not_return_sparse_blob_from_persistent_storage_full_blob_lookup()
        {
            TxPoolConfig txPoolConfig = new()
            {
                BlobsSupport = BlobsSupportMode.Storage,
                BlobCacheSize = 1,
                Size = 10
            };
            BlobTxStorage blobTxStorage = new();
            _txPool = CreatePool(txPoolConfig, GetOsakaSpecProvider(), txStorage: blobTxStorage);
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            Transaction fullBlobTx = Build.A.Transaction
                .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
                .WithMaxFeePerGas(1.GWei)
                .WithMaxPriorityFeePerGas(1.GWei)
                .WithNonce(0UL)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

            ShardBlobNetworkWrapper fullWrapper = (ShardBlobNetworkWrapper)fullBlobTx.NetworkWrapper!;
            BlobCellMask cellMask = BlobCellMask.FromIndices([1]);
            Assert.That(BlobCellsHelper.TryGetFlattenedCells(fullWrapper, cellMask, out byte[][] cells), Is.True);

            byte[][] emptyBlobs = new byte[fullWrapper.Blobs.Length][];
            Array.Fill(emptyBlobs, []);

            Transaction sparseBlobTx = new();
            fullBlobTx.CopyTo(sparseBlobTx);
            sparseBlobTx.NetworkWrapper = fullWrapper with
            {
                Blobs = emptyBlobs,
                CellMask = cellMask,
                Cells = cells,
            };

            Assert.That(_txPool.SubmitTx(sparseBlobTx, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));

            byte[][] requestedHashes = [sparseBlobTx.BlobVersionedHashes![0]!];
            byte[][] blobs = new byte[1][];
            ReadOnlyMemory<byte[]>[] proofs = new ReadOnlyMemory<byte[]>[1];

            int found = _txPool.TryGetBlobsAndProofsV1(requestedHashes, blobs, proofs);

            Assert.That(found, Is.EqualTo(0));
            Assert.That(blobs[0], Is.Null);
            Assert.That(proofs[0].IsEmpty, Is.True);
        }

        [Test]
        public void should_batch_return_blobs_from_cache_and_db()
        {
            // BlobCacheSize = 1: after inserting tx1 and tx2, only tx2 remains in cache.
            // Accessing tx1 via single lookup re-populates it, evicting tx2.
            // Batch lookup then exercises: tx1 from cache, tx2 from DB (TryGetMany).
            TxPoolConfig txPoolConfig = new()
            {
                BlobsSupport = BlobsSupportMode.Storage,
                BlobCacheSize = 1,
                Size = 10
            };
            BlobTxStorage blobTxStorage = new();
            _txPool = CreatePool(txPoolConfig, GetOsakaSpecProvider(), txStorage: blobTxStorage);
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.AddressB, UInt256.MaxValue);

            Transaction tx1 = Build.A.Transaction
                .WithShardBlobTxTypeAndFields(spec: new ReleaseSpec() { IsEip7594Enabled = true })
                .WithMaxFeePerGas(1.GWei)
                .WithMaxPriorityFeePerGas(1.GWei)
                .WithNonce(0)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

            Transaction tx2 = Build.A.Transaction
                .WithShardBlobTxTypeAndFields(spec: new ReleaseSpec() { IsEip7594Enabled = true })
                .WithMaxFeePerGas(1.GWei)
                .WithMaxPriorityFeePerGas(1.GWei)
                .WithNonce(0)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyB).TestObject;

            Assert.That(_txPool.SubmitTx(tx1, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(_txPool.SubmitTx(tx2, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));

            // Access tx1 via single lookup — this re-populates tx1 in cache, evicting tx2
            Assert.That(_txPool.TryGetBlobAndProofV1(tx1.BlobVersionedHashes![0]!, out byte[] _, out byte[][] _), Is.True);

            // Now batch lookup: tx1 from cache (just accessed), tx2 from DB
            byte[][] requestedHashes = [tx1.BlobVersionedHashes![0]!, tx2.BlobVersionedHashes![0]!];
            byte[][] blobs = new byte[2][];
            ReadOnlyMemory<byte[]>[] proofs = new ReadOnlyMemory<byte[]>[2];

            int found = _txPool.TryGetBlobsAndProofsV1(requestedHashes, blobs, proofs);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(found, Is.EqualTo(2));
                Assert.That(blobs[0], Is.Not.Null);
                Assert.That(blobs[1], Is.Not.Null);
                Assert.That(proofs[0].Length, Is.EqualTo(Ckzg.CellsPerExtBlob));
                Assert.That(proofs[1].Length, Is.EqualTo(Ckzg.CellsPerExtBlob));
            }
        }

        [Test]
        public void should_accept_sparse_osaka_blob_tx_and_merge_sampled_cells()
        {
            _txPool = CreatePool(
                new TxPoolConfig() { BlobsSupport = BlobsSupportMode.InMemory, Size = 10 },
                GetOsakaSpecProvider());
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            Transaction fullBlobTx = Build.A.Transaction
                .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
                .WithMaxFeePerGas(1.GWei)
                .WithMaxPriorityFeePerGas(1.GWei)
                .WithNonce(0UL)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

            ShardBlobNetworkWrapper fullWrapper = (ShardBlobNetworkWrapper)fullBlobTx.NetworkWrapper!;
            BlobCellMask initialMask = BlobCellMask.FromIndices([1]);
            BlobCellMask additionalMask = BlobCellMask.FromIndices([7]);
            BlobCellMask requestedMask = initialMask | additionalMask;
            Assert.That(BlobCellsHelper.TryGetFlattenedCells(fullWrapper, initialMask, out byte[][] initialCells), Is.True);
            Assert.That(BlobCellsHelper.TryGetFlattenedCells(fullWrapper, additionalMask, out byte[][] additionalCells), Is.True);
            Assert.That(BlobCellsHelper.TryGetFlattenedCells(fullWrapper, requestedMask, out byte[][] mergedCellsExpected), Is.True);

            byte[][] emptyBlobs = new byte[fullWrapper.Blobs.Length][];
            for (int i = 0; i < emptyBlobs.Length; i++)
            {
                emptyBlobs[i] = [];
            }

            Transaction sparseBlobTx = new();
            fullBlobTx.CopyTo(sparseBlobTx);
            sparseBlobTx.NetworkWrapper = fullWrapper with
            {
                Blobs = emptyBlobs,
                CellMask = initialMask,
                Cells = initialCells,
            };

            AcceptTxResult result = _txPool.SubmitTx(sparseBlobTx, TxHandlingOptions.None);
            Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted), result.ToString());
            Assert.That(_txPool.TryGetPendingBlobTransaction(sparseBlobTx.Hash!, out Transaction storedSparseBlobTx), Is.True);
            ShardBlobNetworkWrapper storedWrapper = (ShardBlobNetworkWrapper)storedSparseBlobTx!.NetworkWrapper!;
            Assert.That(storedWrapper.HasFullBlobs(), Is.False);
            Assert.That(storedWrapper.CellMask, Is.EqualTo(initialMask));
            Assert.That(storedWrapper.Cells, Is.EquivalentTo(initialCells));

            Assert.That(_txPool.TryMergeBlobCells(sparseBlobTx.Hash!, additionalMask, additionalCells), Is.True);
            Assert.That(_txPool.TryGetPendingBlobTransaction(sparseBlobTx.Hash!, out Transaction mergedSparseBlobTx), Is.True);

            ShardBlobNetworkWrapper mergedWrapper = (ShardBlobNetworkWrapper)mergedSparseBlobTx!.NetworkWrapper!;
            Assert.That(mergedWrapper.CellMask, Is.EqualTo(requestedMask));
            Assert.That(mergedWrapper.Cells, Is.EquivalentTo(mergedCellsExpected));

            Assert.That(_txPool.TryGetBlobCells(sparseBlobTx.Hash!, requestedMask, out BlobCellMask availableMask, out byte[][] mergedCells), Is.True);
            Assert.That(availableMask, Is.EqualTo(requestedMask));
            Assert.That(mergedCells, Is.EquivalentTo(mergedCellsExpected));
        }

        [Test]
        public void should_reject_invalid_sparse_cells_merge()
        {
            _txPool = CreatePool(
                new TxPoolConfig() { BlobsSupport = BlobsSupportMode.InMemory, Size = 10 },
                GetOsakaSpecProvider());
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            Transaction fullBlobTx = Build.A.Transaction
                .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
                .WithMaxFeePerGas(1.GWei)
                .WithMaxPriorityFeePerGas(1.GWei)
                .WithNonce(0UL)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

            ShardBlobNetworkWrapper fullWrapper = (ShardBlobNetworkWrapper)fullBlobTx.NetworkWrapper!;
            BlobCellMask initialMask = BlobCellMask.FromIndices([1]);
            BlobCellMask additionalMask = BlobCellMask.FromIndices([7]);
            Assert.That(BlobCellsHelper.TryGetFlattenedCells(fullWrapper, initialMask, out byte[][] initialCells), Is.True);
            Assert.That(BlobCellsHelper.TryGetFlattenedCells(fullWrapper, additionalMask, out byte[][] invalidCells), Is.True);

            invalidCells[0] = (byte[])invalidCells[0].Clone();
            invalidCells[0][0] ^= 0x01;

            byte[][] emptyBlobs = new byte[fullWrapper.Blobs.Length][];
            for (int i = 0; i < emptyBlobs.Length; i++)
            {
                emptyBlobs[i] = [];
            }

            Transaction sparseBlobTx = new();
            fullBlobTx.CopyTo(sparseBlobTx);
            sparseBlobTx.NetworkWrapper = fullWrapper with
            {
                Blobs = emptyBlobs,
                CellMask = initialMask,
                Cells = initialCells,
            };

            Assert.That(_txPool.SubmitTx(sparseBlobTx, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(_txPool.TryMergeBlobCells(sparseBlobTx.Hash!, additionalMask, invalidCells), Is.False);
            Assert.That(_txPool.TryGetPendingBlobTransaction(sparseBlobTx.Hash!, out Transaction storedSparseBlobTx), Is.True);

            ShardBlobNetworkWrapper storedWrapper = (ShardBlobNetworkWrapper)storedSparseBlobTx!.NetworkWrapper!;
            Assert.That(storedWrapper.CellMask, Is.EqualTo(initialMask));
            Assert.That(storedWrapper.Cells, Is.EquivalentTo(initialCells));
        }

        private static void ConvertToSparseBlobTransaction(Transaction tx, BlobCellMask cellMask)
        {
            ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)tx.NetworkWrapper!;
            Assert.That(BlobCellsHelper.TryGetFlattenedCells(wrapper, cellMask, out byte[][] cells), Is.True);
            byte[][] emptyBlobs = new byte[wrapper.Blobs.Length][];
            Array.Fill(emptyBlobs, []);
            tx.NetworkWrapper = wrapper with
            {
                Blobs = emptyBlobs,
                CellMask = cellMask,
                Cells = cells,
            };
            tx.ClearLengthCache();
        }

        private static void ReplaceBlobSidecar(Transaction tx, byte firstBlobByte)
        {
            byte[] blob = new byte[Ckzg.BytesPerBlob];
            blob[0] = firstBlobByte;
            IBlobProofsBuilder blobProofsBuilder = IBlobProofsManager.For(ProofVersion.V1);
            ShardBlobNetworkWrapper wrapper = blobProofsBuilder.AllocateWrapper(blob);
            blobProofsBuilder.ComputeProofsAndCommitments(wrapper);
            tx.NetworkWrapper = wrapper;
            tx.BlobVersionedHashes = blobProofsBuilder.ComputeHashes(wrapper);
            tx.ClearLengthCache();
        }
    }
}
