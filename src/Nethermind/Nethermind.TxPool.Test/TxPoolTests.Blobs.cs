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
    public partial class TxPoolTests
    {
        // blob collection is designed to be infinite, so there is no point in checking if is full and comparing
        // incoming tx with the worst already pending one (candidate for evicting). There will be no eviction.
        [Test]
        public void should_reject_tx_with_FeeTooLow_only_if_is_not_blob_type()
        {
            TxPoolConfig txPoolConfig = new() { Size = 10 };
            _txPool = CreatePool(txPoolConfig, GetCancunSpecProvider());
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);
            EnsureSenderBalance(TestItem.AddressB, UInt256.MaxValue);

            for (int i = 0; i < txPoolConfig.Size; i++)
            {
                Transaction tx = Build.A.Transaction
                    .WithNonce((UInt256)i)
                    .WithType(TxType.EIP1559)
                    .WithMaxFeePerGas(1.GWei() + (UInt256)(100 - i))
                    .WithMaxPriorityFeePerGas(1.GWei() + (UInt256)(100 - i))
                    .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
                _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast).Should().Be(AcceptTxResult.Accepted);
            }

            _txPool.GetPendingTransactionsCount().Should().Be(txPoolConfig.Size);

            Transaction feeTooLow1559Tx = Build.A.Transaction
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas(1.GWei() + UInt256.One)
                .WithMaxPriorityFeePerGas(1.GWei() + UInt256.One)
                .WithNonce(UInt256.Zero)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyB).TestObject;

            Transaction feeTooLowBlobTx = Build.A.Transaction
                .WithShardBlobTxTypeAndFields()
                .WithMaxFeePerGas(1.GWei() + UInt256.One)
                .WithMaxPriorityFeePerGas(1.GWei() + UInt256.One)
                .WithNonce(UInt256.Zero)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyB).TestObject;


            _txPool.SubmitTx(feeTooLow1559Tx, TxHandlingOptions.None).Should().Be(AcceptTxResult.FeeTooLow);
            _txPool.SubmitTx(feeTooLowBlobTx, TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);
        }

        [Test]
        public void should_add_blob_tx_and_return_when_requested([Values(true, false)] bool isPersistentStorage)
        {
            TxPoolConfig txPoolConfig = new() { Size = 10, PersistentBlobStorageEnabled = isPersistentStorage };
            BlobTxStorage blobTxStorage = new(new MemDb());
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

            blobTxStorage.TryGet(blobTxAdded.Hash, out Transaction blobTxFromDb).Should().Be(isPersistentStorage); // additional check for persistent db
            if (isPersistentStorage)
            {
                blobTxFromDb.Should().BeEquivalentTo(blobTxAdded, options => options
                    .Excluding(t => t.SenderAddress) // sender is not encoded/decoded...
                    .Excluding(t => t.GasBottleneck) // ...as well as GasBottleneck...
                    .Excluding(t => t.PoolIndex));   // ...and PoolIndex
            }
        }

        [Test]
        public void should_not_throw_when_asking_for_non_existing_tx()
        {
            TxPoolConfig txPoolConfig = new() { Size = 10 };
            BlobTxStorage blobTxStorage = new(new MemDb());
            _txPool = CreatePool(txPoolConfig, GetCancunSpecProvider(), txStorage: blobTxStorage);

            _txPool.TryGetPendingTransaction(TestItem.KeccakA, out Transaction blobTxReturned).Should().BeFalse();
            blobTxReturned.Should().BeNull();

            blobTxStorage.TryGet(TestItem.KeccakA, out Transaction blobTxFromDb).Should().BeFalse();
            blobTxFromDb.Should().BeNull();
        }

        [TestCase(999_999_999, false)]
        [TestCase(1_000_000_000, true)]
        public void should_not_allow_to_add_blob_tx_with_MaxPriorityFeePerGas_lower_than_1GWei(int maxPriorityFeePerGas, bool expectedResult)
        {
            TxPoolConfig txPoolConfig = new() { Size = 10 };
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
            _txPool = CreatePool(new TxPoolConfig() { Size = 128 }, GetCancunSpecProvider());
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

            _txPool = CreatePool(new TxPoolConfig() { Size = 128 }, GetCancunSpecProvider());
            EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);

            Transaction firstTx = GetTx(firstIsBlob, UInt256.Zero);
            Transaction secondTx = GetTx(secondIsBlob, UInt256.One);

            _txPool.SubmitTx(firstTx, TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);
            _txPool.SubmitTx(secondTx, TxHandlingOptions.None).Should().Be(firstIsBlob ^ secondIsBlob ? AcceptTxResult.PendingTxsOfOtherType : AcceptTxResult.Accepted);
        }

        [Test]
        public void should_remove_replaced_blob_tx_from_persistent_storage_and_cache()
        {
            TxPoolConfig txPoolConfig = new() { Size = 10, PersistentBlobStorageEnabled = true };
            BlobTxStorage blobTxStorage = new(new MemDb());
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
            blobTxStorage.TryGet(oldTx.Hash, out Transaction blobTxFromDb).Should().BeTrue();
            blobTxFromDb.Should().BeEquivalentTo(oldTx, options => options
                .Excluding(t => t.SenderAddress) // sender is not encoded/decoded...
                .Excluding(t => t.GasBottleneck) // ...as well as GasBottleneck...
                .Excluding(t => t.PoolIndex));   // ...and PoolIndex

            _txPool.SubmitTx(newTx, TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);
            _txPool.GetPendingBlobTransactionsCount().Should().Be(1);
            _txPool.TryGetPendingTransaction(newTx.Hash!, out blobTxReturned).Should().BeTrue();
            blobTxReturned.Should().BeEquivalentTo(newTx);
            blobTxStorage.TryGet(oldTx.Hash, out blobTxFromDb).Should().BeFalse();
            blobTxStorage.TryGet(newTx.Hash, out blobTxFromDb).Should().BeTrue();
            blobTxFromDb.Should().BeEquivalentTo(newTx, options => options
                .Excluding(t => t.SenderAddress) // sender is not encoded/decoded...
                .Excluding(t => t.GasBottleneck) // ...as well as GasBottleneck...
                .Excluding(t => t.PoolIndex));   // ...and PoolIndex
        }

        [Test]
        public void should_keep_in_memory_only_light_blob_tx_equivalent_if_persistent_storage_enabled([Values(true, false)] bool isPersistentStorage)
        {
            TxPoolConfig txPoolConfig = new() { Size = 10, PersistentBlobStorageEnabled = isPersistentStorage };
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
            TxPoolConfig txPoolConfig = new() { Size = 10, PersistentBlobStorageEnabled = isPersistentStorage };
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

            _txPool = CreatePool(new TxPoolConfig() { Size = 128 }, GetCancunSpecProvider());
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

        [TestCase(0, 97)]
        [TestCase(1, 131324)]
        [TestCase(2, 262534)]
        [TestCase(3, 393741)]
        [TestCase(4, 524947)]
        [TestCase(5, 656156)]
        [TestCase(6, 787365)]
        public void should_calculate_size_of_blob_tx_correctly(int numberOfBlobs, int expectedLength)
        {
            Transaction blobTx = Build.A.Transaction
                .WithShardBlobTxTypeAndFields(numberOfBlobs)
                .SignedAndResolved()
                .TestObject;
            blobTx.GetLength().Should().Be(expectedLength);
        }
    }
}
