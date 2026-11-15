// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Encoding;
using Nethermind.Serialization.Rlp;
using Nethermind.Synchronization;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth;

internal static class ReceiptsServingTestHelper
{
    private static readonly BlockDecoder BlockDecoder = new();

    /// <summary>
    /// Makes <see cref="ISyncServer.TryAppendReceipts"/> of <paramref name="syncServer"/> serve the receipts its
    /// <see cref="ISyncServer.GetReceipts"/> gives, encoded as the receipts messages encode decoded receipts.
    /// </summary>
    /// <param name="storedBlock">The block whose receipts are copied from their stored form.</param>
    public static void ServeReceipts(this ISyncServer syncServer, ISpecProvider specProvider, Block? storedBlock = null) =>
        syncServer.TryAppendReceipts(Arg.Any<Hash256>(), Arg.Any<IRlpDecoder<TxReceipt>>(), Arg.Any<bool>(), Arg.Any<ArrayPoolList<byte>>())
            .Returns(call =>
            {
                Hash256 blockHash = call.ArgAt<Hash256>(0);
                TxReceipt[]? receipts = syncServer.GetReceipts(blockHash);
                if (receipts is null) return false;

                ArrayPoolList<byte> destination = call.ArgAt<ArrayPoolList<byte>>(3);
                if (storedBlock is not null && blockHash == storedBlock.Hash)
                {
                    bool eip658 = specProvider.GetReceiptSpec(storedBlock.Number).IsEip658Enabled;
                    byte[] stored = ReceiptArrayStorageDecoder.Instance.Encode(receipts, RlpBehaviors.Storage | (eip658 ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None));
                    Assert.That(ReceiptArrayStorageDecoder.TryAppendNetworkReceipts(stored, BlockDecoder.Encode(storedBlock), call.ArgAt<bool>(2), eip658, eip7668: false, destination), Is.True);
                    return true;
                }

                destination.AddRange(Encode(receipts, call.ArgAt<IRlpDecoder<TxReceipt>>(1), specProvider));
                return true;
            });

    /// <summary>A block with <paramref name="receiptCount"/> transactions, and receipts that its stored form gives back unchanged.</summary>
    public static (Block Block, TxReceipt[] Receipts) CreateStoredBlock(ISpecProvider specProvider, int receiptCount)
    {
        const ulong number = 1;
        bool eip658 = specProvider.GetReceiptSpec(number).IsEip658Enabled;
        TxType[] types = [.. Enumerable.Range(0, receiptCount).Select(static i => i % 2 == 0 ? TxType.Legacy : TxType.EIP1559)];
        Block block = Build.A.Block.WithNumber(number)
            .WithTransactions([.. types.Select(static type => Build.A.Transaction.WithType(type).TestObject)]).TestObject;
        LogEntry log = Build.A.LogEntry.WithAddress(TestItem.AddressA).WithTopics(TestItem.KeccakA).WithData([1, 2, 3]).TestObject;
        TxReceipt[] receipts = [.. types.Select((type, i) => Build.A.Receipt.WithTxType(type).WithBlockNumber(number).WithStatusCode(1)
            .WithGasUsedTotal(GasCostOf.Transaction * (ulong)(i + 1)).WithLogs(i % 2 == 0 ? [log] : []).TestObject)];
        foreach (TxReceipt receipt in receipts)
        {
            if (!eip658) receipt.PostTransactionState = TestItem.KeccakC;
            receipt.Bloom = new Bloom(receipt.Logs!);
        }

        return (block, receipts);
    }

    /// <summary>The receipt sequence of one block as the receipts messages encode decoded receipts.</summary>
    public static byte[] Encode(TxReceipt[] receipts, IRlpDecoder<TxReceipt> decoder, ISpecProvider specProvider) =>
        RlpTestHelper.EncodeSequence([.. receipts.Select(receipt => decoder.Encode(receipt,
            specProvider.GetReceiptSpec(receipt.BlockNumber).IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None))]);
}
