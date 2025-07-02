// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Receipts;

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

        _receiptsRecovery.TryRecover(block, receipts, forceRecoverSender).Should().Be(expected);
    }

    [Test]
    public void TryRecover_should_recover_contract_address()
    {
        Transaction tx = Build.A.Transaction.WithSenderAddress(null).WithTo(null).Signed().TestObject;
        Block block = Build.A.Block.WithTransactions(tx).TestObject;
        TxReceipt receipt = Build.A.Receipt.WithBlockHash(block.Hash!).TestObject;

        tx.SenderAddress.Should().BeNull();
        receipt.ContractAddress.Should().BeNull();

        ReceiptsRecoveryResult result = _receiptsRecovery.TryRecover(block, new[] { receipt });

        result.Should().Be(ReceiptsRecoveryResult.NeedReinsert);
        receipt.ContractAddress.Should().Be(new Address("0x3a6e7897affdf344781bb9098a605e9839ac131b"));
    }
}
