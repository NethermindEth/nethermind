// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

public partial class TxPoolTests
{
    [Test]
    public async Task should_keep_next_blob_tx_includable_after_previous_nonce_is_mined()
    {
        const long blockNumber = 358;

        ITxPoolConfig txPoolConfig = new TxPoolConfig()
        {
            Size = 128,
            BlobsSupport = BlobsSupportMode.InMemory
        };
        _txPool = CreatePool(txPoolConfig, GetCancunSpecProvider());

        EnsureSenderBalance(TestItem.AddressA, UInt256.MaxValue);
        Transaction firstTx = CreateBlobTx(TestItem.PrivateKeyA, UInt256.Zero);
        Transaction secondTx = CreateBlobTx(TestItem.PrivateKeyA, UInt256.One);

        Assert.That(_txPool.SubmitTx(firstTx, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
        Assert.That(_txPool.SubmitTx(secondTx, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
        Assert.That(_txPool.GetPendingBlobTransactionsCount(), Is.EqualTo(2));

        _stateProvider.CreateAccount(TestItem.AddressA, UInt256.MaxValue, UInt256.One);
        Block block = Build.A.Block.WithNumber(blockNumber).WithTransactions(firstTx).TestObject;
        await RaiseBlockAddedToMainAndWaitForNewHead(block);

        Assert.That(_txPool.TryGetPendingBlobTransaction(firstTx.Hash!, out _), Is.False);
        Assert.That(_txPool.TryGetPendingBlobTransaction(secondTx.Hash!, out Transaction remainingTx), Is.True);
        Assert.That(remainingTx!.GasBottleneck, Is.Not.EqualTo(UInt256.Zero));
    }
}
