// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

/// <summary>
/// Tests for the EIP-7805 (FOCIL) execution-layer pieces: inclusion list building from the
/// mempool and post-execution inclusion list satisfaction validation.
/// </summary>
[TestFixture]
public class Eip7805Tests
{
    private static Transaction SignedTx(ulong nonce = 0, int dataLength = 0) => Build.A.Transaction
        .WithNonce(nonce)
        .WithGasLimit((ulong)(21000 + 16 * dataLength))
        .WithMaxFeePerGas(2)
        .WithMaxPriorityFeePerGas(1)
        .WithType(TxType.EIP1559)
        .WithData(new byte[dataLength])
        .SignedAndResolved(TestItem.PrivateKeyA)
        .TestObject;

    private static byte[] Encode(Transaction tx) => Rlp.Encode(tx, RlpBehaviors.SkipTypedWrapping).Bytes;

    [Test]
    public void GetInclusionList_packs_best_transactions_up_to_the_byte_limit()
    {
        Transaction small = SignedTx();
        Transaction blob = Build.A.Transaction.WithType(TxType.Blob).WithMaxFeePerBlobGas(1).WithBlobVersionedHashes(1).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        Transaction oversized = SignedTx(nonce: 1, dataLength: Eip7805Constants.MaxBytesPerInclusionList);

        ITxPool txPool = Substitute.For<ITxPool>();
        txPool.GetBestTxOfEachSender().Returns([small, blob, oversized]);

        ResultWrapper<byte[][]> result = new GetInclusionListHandler(txPool).Handle();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
            Assert.That(result.Data, Has.Length.EqualTo(1), "blob and oversized txs must be excluded");
            Assert.That(result.Data[0], Is.EqualTo(Encode(small)));
        }
    }

    private static NewPayloadHandler CreateHandler(IStateReader stateReader)
    {
        IMergeConfig mergeConfig = Substitute.For<IMergeConfig>();
        mergeConfig.NewPayloadCacheSize.Returns(0);
        mergeConfig.NewPayloadBlockProcessingTimeout.Returns(1000);

        return new NewPayloadHandler(
            Substitute.For<IPayloadPreparationService>(),
            Substitute.For<IBlockValidator>(),
            Substitute.For<IBlockTree>(),
            Substitute.For<IPoSSwitcher>(),
            Substitute.For<IBeaconSyncStrategy>(),
            Substitute.For<IBeaconPivot>(),
            Substitute.For<IBlockCacheService>(),
            Substitute.For<IBlockProcessingQueue>(),
            Substitute.For<IInvalidChainTracker>(),
            Substitute.For<IMergeSyncController>(),
            mergeConfig,
            Substitute.For<IReceiptConfig>(),
            stateReader,
            MainnetSpecProvider.Instance,
            LimboLogs.Instance);
    }

    private static IStateReader StateReaderWith(Address sender, ulong nonce, ulong balance)
    {
        IStateReader stateReader = Substitute.For<IStateReader>();
        stateReader.TryGetAccount(Arg.Any<BlockHeader>(), sender, out Arg.Any<AccountStruct>())
            .Returns(x =>
            {
                x[2] = new AccountStruct(nonce, balance);
                return true;
            });
        return stateReader;
    }

    private static Block BlockWith(ulong gasUsed, params Transaction[] transactions) => Build.A.Block
        .WithNumber(MainnetSpecProvider.ParisBlockNumber)
        .WithTimestamp(MainnetSpecProvider.PragueBlockTimestamp)
        .WithGasLimit(30_000_000)
        .WithGasUsed(gasUsed)
        .WithBaseFeePerGas(1)
        .WithTransactions(transactions)
        .TestObject;

    [Test]
    public void Satisfied_when_inclusion_list_transaction_is_in_the_block()
    {
        Transaction tx = SignedTx();
        NewPayloadHandler handler = CreateHandler(StateReaderWith(tx.SenderAddress!, nonce: 0, balance: 1_000_000_000));

        Assert.That(handler.IsInclusionListSatisfied(BlockWith(21000, tx), [Encode(tx)]), Is.True);
    }

    [Test]
    public void Unsatisfied_when_executable_transaction_was_left_out()
    {
        Transaction tx = SignedTx();
        NewPayloadHandler handler = CreateHandler(StateReaderWith(tx.SenderAddress!, nonce: 0, balance: 1_000_000_000));

        Assert.That(handler.IsInclusionListSatisfied(BlockWith(21000), [Encode(tx)]), Is.False);
    }

    [Test]
    public void Satisfied_when_left_out_transaction_has_stale_nonce()
    {
        Transaction tx = SignedTx();
        NewPayloadHandler handler = CreateHandler(StateReaderWith(tx.SenderAddress!, nonce: 5, balance: 1_000_000_000));

        Assert.That(handler.IsInclusionListSatisfied(BlockWith(21000), [Encode(tx)]), Is.True);
    }

    [Test]
    public void Satisfied_when_left_out_transaction_cannot_pay()
    {
        Transaction tx = SignedTx();
        NewPayloadHandler handler = CreateHandler(StateReaderWith(tx.SenderAddress!, nonce: 0, balance: 1));

        Assert.That(handler.IsInclusionListSatisfied(BlockWith(21000), [Encode(tx)]), Is.True);
    }

    [Test]
    public void Satisfied_when_block_has_insufficient_gas_remaining()
    {
        Transaction tx = SignedTx();
        NewPayloadHandler handler = CreateHandler(StateReaderWith(tx.SenderAddress!, nonce: 0, balance: 1_000_000_000));

        Assert.That(handler.IsInclusionListSatisfied(BlockWith(30_000_000 - 20999), [Encode(tx)]), Is.True);
    }

    [Test]
    public void Satisfied_when_entry_is_malformed()
    {
        NewPayloadHandler handler = CreateHandler(Substitute.For<IStateReader>());

        Assert.That(handler.IsInclusionListSatisfied(BlockWith(21000), [[0xde, 0xad]]), Is.True);
    }
}
