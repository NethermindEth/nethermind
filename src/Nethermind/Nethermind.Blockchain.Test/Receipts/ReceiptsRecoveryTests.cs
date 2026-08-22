// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Receipts;

[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class ReceiptsRecoveryTests
{
    private IReceiptsRecovery _receiptsRecovery = null!;

    [SetUp]
    public void Setup()
    {
        MainnetSpecProvider specProvider = MainnetSpecProvider.Instance;
        EthereumEcdsa ethereumEcdsa = new(specProvider.ChainId);

        _receiptsRecovery = new ReceiptsRecovery(ethereumEcdsa, specProvider);
    }

    [MaxTime(Timeout.MaxTestTime)]
    [TestCase(5, 5, true, ReceiptsRecoveryResult.NeedReinsert)]
    [TestCase(5, 5, false, ReceiptsRecoveryResult.Skipped)]
    [TestCase(0, 0, true, ReceiptsRecoveryResult.Skipped)]
    [TestCase(1, 0, true, ReceiptsRecoveryResult.Fail)]
    [TestCase(0, 1, true, ReceiptsRecoveryResult.Fail)]
    [TestCase(5, 4, true, ReceiptsRecoveryResult.Fail)]
    [TestCase(1, 2, true, ReceiptsRecoveryResult.Fail)]
    public void TryRecover_should_return_correct_receipts_recovery_result(int blockTxsLength, int receiptsLength, bool forceRecoverSender, ReceiptsRecoveryResult expected)
    {
        Transaction[] txs = new Transaction[blockTxsLength];
        for (int i = 0; i < blockTxsLength; i++)
        {
            txs[i] = Build.A.Transaction.SignedAndResolved().TestObject;
        }

        Block block = Build.A.Block.WithTransactions(txs).TestObject;

        TxReceipt[] receipts = new TxReceipt[receiptsLength];
        for (int i = 0; i < receiptsLength; i++)
        {
            receipts[i] = Build.A.Receipt.WithBlockHash(block.Hash).TestObject;
        }

        Assert.That(_receiptsRecovery.TryRecover(block, receipts, forceRecoverSender), Is.EqualTo(expected));
    }

    [Test]
    public void TryRecover_should_recover_contract_address()
    {
        Transaction tx = Build.A.Transaction.WithSenderAddress(null).WithTo(null).Signed().TestObject;
        Block block = Build.A.Block.WithTransactions(tx).TestObject;
        TxReceipt receipt = Build.A.Receipt.WithBlockHash(block.Hash!).TestObject;

        Assert.That(tx.SenderAddress, Is.Null);
        Assert.That(receipt.ContractAddress, Is.Null);

        ReceiptsRecoveryResult result = _receiptsRecovery.TryRecover(block, new[] { receipt });

        Assert.That(result, Is.EqualTo(ReceiptsRecoveryResult.NeedReinsert));
        Assert.That(receipt.ContractAddress, Is.EqualTo(new Address("0x3a6e7897affdf344781bb9098a605e9839ac131b")));
    }

    // Recovery re-derives a non-success status from the log count, which holds pre-EIP-8141: a failed
    // transaction has no logs. A frame transaction breaks it — its status is derived from the frame
    // statuses, so it can fail while carrying the logs of the frames that succeeded. Without the
    // exemption a node serving this receipt from the database reports success where the node that
    // executed the block reports failure.
    [Test]
    public void TryRecover_should_keep_a_failed_frame_transaction_status_with_logs()
    {
        Block block = FrameTxBlock();
        TxReceipt receipt = Build.A.Receipt.WithBlockHash(block.Hash!).TestObject;
        receipt.TxType = TxType.FrameTx;
        receipt.StatusCode = TxFrameReceipt.StatusFailure;
        receipt.Logs = [new LogEntry(TestItem.AddressB, [1], [])];

        _receiptsRecovery.TryRecover(block, [receipt]);

        Assert.That(receipt.StatusCode, Is.EqualTo(TxFrameReceipt.StatusFailure));
    }

    // An EIP-8141 frame transaction has no `to` field, which makes it look like a creation, but it
    // creates nothing at the top level. Recovering an address here would name an account that was
    // never created, and disagree with the address the executing node reported.
    [Test]
    public void TryRecover_should_not_invent_a_contract_address_for_a_frame_transaction()
    {
        Block block = FrameTxBlock();
        TxReceipt receipt = Build.A.Receipt.WithBlockHash(block.Hash!).TestObject;
        // Recovery only ever assigns these, so a receipt that arrives empty would pass the assertions
        // below without the exemption doing anything.
        receipt.ContractAddress = TestItem.AddressD;
        receipt.Recipient = TestItem.AddressD;
        Assert.That(block.Transactions[0].IsContractCreation, Is.True,
            "the transaction must look like a creation, or the exemption is not the reason for the null");

        _receiptsRecovery.TryRecover(block, [receipt]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.ContractAddress, Is.Null);
            Assert.That(receipt.Recipient, Is.Null, "a frame transaction has no top-level recipient either");
        }
    }

    private static Block FrameTxBlock()
    {
        Transaction tx = new()
        {
            Type = TxType.FrameTx,
            SenderAddress = TestItem.AddressA,
            Frames = [new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: 100_000, default, default)],
            FrameSignatures = [],
        };
        tx.Hash = tx.CalculateHash();
        return Build.A.Block.WithTransactions(tx).TestObject;
    }
}
