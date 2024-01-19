// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Int256;
using NUnit.Framework;

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
                blobTxFromDb.Should().BeEquivalentTo(blobTxAdded, options => options
                    .Excluding(t => t.GasBottleneck) // GasBottleneck is not encoded/decoded...
                    .Excluding(t => t.PoolIndex));   // ...as well as PoolIndex
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
            blobTxFromDb.Should().BeEquivalentTo(oldTx, options => options
                .Excluding(t => t.GasBottleneck) // GasBottleneck is not encoded/decoded...
                .Excluding(t => t.PoolIndex));   // ...as well as PoolIndex

            _txPool.SubmitTx(newTx, TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);
            _txPool.GetPendingBlobTransactionsCount().Should().Be(1);
            _txPool.TryGetPendingTransaction(newTx.Hash!, out blobTxReturned).Should().BeTrue();
            blobTxReturned.Should().BeEquivalentTo(newTx);
            blobTxStorage.TryGet(oldTx.Hash, oldTx.SenderAddress, oldTx.Timestamp, out blobTxFromDb).Should().BeFalse();
            blobTxStorage.TryGet(newTx.Hash, newTx.SenderAddress!, newTx.Timestamp, out blobTxFromDb).Should().BeTrue();
            blobTxFromDb.Should().BeEquivalentTo(newTx, options => options
                .Excluding(t => t.GasBottleneck) // GasBottleneck is not encoded/decoded...
                .Excluding(t => t.PoolIndex));   // ...as well as PoolIndex
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

            _headInfo.CurrentPricePerBlobGas = UInt256.MaxValue;

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
                    options => options.Excluding(t => t.GasBottleneck));
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
            Transaction tx = GetTx(TestItem.PrivateKeyA);
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

            Transaction[] txs = { GetTx(TestItem.PrivateKeyA), GetTx(TestItem.PrivateKeyB) };

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
            returnedTxs.Should().BeEquivalentTo(txs, options => options
                .Excluding(t => t.GasBottleneck)    // GasBottleneck is not encoded/decoded...
                .Excluding(t => t.PoolIndex)        // ...as well as PoolIndex
                .Excluding(t => t.SenderAddress));  // sender is recovered later, it is not returned from db

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

            Transaction[] txsA = { GetTx(TestItem.PrivateKeyA), GetTx(TestItem.PrivateKeyB) };
            Transaction[] txsB = { GetTx(TestItem.PrivateKeyC) };

            _txPool.SubmitTx(txsA[0], TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);
            _txPool.SubmitTx(txsA[1], TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);
            _txPool.SubmitTx(txsB[0], TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);

            _txPool.GetPendingTransactionsCount().Should().Be(0);
            _txPool.GetPendingBlobTransactionsCount().Should().Be(txsA.Length + txsB.Length);

            // adding block A
            Block blockA = Build.A.Block.WithNumber(blockNumber).WithTransactions(txsA).TestObject;
            await RaiseBlockAddedToMainAndWaitForTransactions(txsA.Length, blockA);

            _txPool.GetPendingBlobTransactionsCount().Should().Be(txsB.Length);
            _txPool.TryGetPendingBlobTransaction(txsA[0].Hash!, out _).Should().BeFalse();
            _txPool.TryGetPendingBlobTransaction(txsA[1].Hash!, out _).Should().BeFalse();
            _txPool.TryGetPendingBlobTransaction(txsB[0].Hash!, out _).Should().BeTrue();

            // reorganized from block A to block B
            Block blockB = Build.A.Block.WithNumber(blockNumber).WithTransactions(txsB).TestObject;
            await RaiseBlockAddedToMainAndWaitForTransactions(txsB.Length + txsA.Length, blockB, blockA);

            // tx from block B should be removed from blob pool, but present in processed txs db
            _txPool.TryGetPendingBlobTransaction(txsB[0].Hash!, out _).Should().BeFalse();
            blobTxStorage.TryGetBlobTransactionsFromBlock(blockNumber, out Transaction[] blockBTxs).Should().BeTrue();
            txsB.Should().BeEquivalentTo(blockBTxs, options => options
                .Excluding(t => t.GasBottleneck)    // GasBottleneck is not encoded/decoded...
                .Excluding(t => t.PoolIndex)        // ...as well as PoolIndex
                .Excluding(t => t.SenderAddress));  // sender is recovered later, it is not returned from db

            // blob txs from reorganized blockA should be readded to blob pool
            _txPool.GetPendingBlobTransactionsCount().Should().Be(txsA.Length);
            _txPool.TryGetPendingBlobTransaction(txsA[0].Hash!, out Transaction tx1).Should().BeTrue();
            _txPool.TryGetPendingBlobTransaction(txsA[1].Hash!, out Transaction tx2).Should().BeTrue();

            tx1.Should().BeEquivalentTo(txsA[0], options => options
                .Excluding(t => t.GasBottleneck)    // GasBottleneck is not encoded/decoded...
                .Excluding(t => t.PoolIndex));      // ...as well as PoolIndex

            tx2.Should().BeEquivalentTo(txsA[1], options => options
                .Excluding(t => t.GasBottleneck)    // GasBottleneck is not encoded/decoded...
                .Excluding(t => t.PoolIndex));      // ...as well as PoolIndex
        }

        private Transaction GetTx(PrivateKey sender)
        {
            return Build.A.Transaction
                .WithShardBlobTxTypeAndFields()
                .WithMaxFeePerGas(1.GWei())
                .WithMaxPriorityFeePerGas(1.GWei())
                .WithNonce(UInt256.Zero)
                .SignedAndResolved(_ethereumEcdsa, sender).TestObject;
        }
    }
}
