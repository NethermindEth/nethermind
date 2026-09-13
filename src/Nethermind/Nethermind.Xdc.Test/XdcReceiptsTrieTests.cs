// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;
using Nethermind.Xdc.Spec;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

internal class XdcReceiptsTrieTests
{
    private static readonly Address BlockSigner = TestItem.AddressE;

    private static XdcReleaseSpec Spec => new() { BlockSignerContract = BlockSigner, RandomizeSMCBinary = TestItem.AddressC };

    private static TxReceipt Receipt(Address recipient, TxType type) => new()
    {
        TxType = type,
        Recipient = recipient,
        StatusCode = StatusCode.Success,
        GasUsedTotal = 0,
        Logs = [new LogEntry(recipient, [], [])],
        Bloom = Bloom.Empty,
    };

    private static Hash256 Root(params TxReceipt[] receipts) =>
        ReceiptTrie.CalculateRoot(Spec, receipts, Rlp.GetDecoderOrThrow<TxReceipt>(RlpDecoderKey.Trie));

    [TestCase(TxType.EIP1559)]
    [TestCase(TxType.AccessList)]
    public void SignTransactionReceipt_IsEncodedAsLegacy(TxType txType)
    {
        TxReceipt typed = Receipt(BlockSigner, txType);

        Hash256 asEncoded = Root(XdcBlockProcessor.AsEncodedForTrie([typed], Spec));

        Assert.Multiple(() =>
        {
            Assert.That(asEncoded, Is.EqualTo(Root(Receipt(BlockSigner, TxType.Legacy))));
            Assert.That(asEncoded, Is.Not.EqualTo(Root(typed)));
        });
    }

    [TestCase(TxType.EIP1559)]
    [TestCase(TxType.AccessList)]
    public void OtherRecipientReceipt_KeepsItsType(TxType txType)
    {
        TxReceipt randomize = Receipt(TestItem.AddressC, txType);
        TxReceipt ordinary = Receipt(TestItem.AddressD, txType);

        Assert.Multiple(() =>
        {
            Assert.That(Root(XdcBlockProcessor.AsEncodedForTrie([randomize], Spec)), Is.EqualTo(Root(randomize)));
            Assert.That(Root(XdcBlockProcessor.AsEncodedForTrie([ordinary], Spec)), Is.EqualTo(Root(ordinary)));
        });
    }

    // The caller keeps using these receipts after the root is taken — they are what gets stored and
    // served over RPC — so the trie encoding must not reach back into the array or the receipts in it.
    [Test]
    public void OriginalReceipts_AreNotTouched()
    {
        TxReceipt signReceipt = Receipt(BlockSigner, TxType.EIP1559);
        TxReceipt[] receipts = [signReceipt, Receipt(TestItem.AddressD, TxType.EIP1559)];
        TxReceipt[] before = (TxReceipt[])receipts.Clone();

        TxReceipt[] forTrie = XdcBlockProcessor.AsEncodedForTrie(receipts, Spec);

        Assert.Multiple(() =>
        {
            Assert.That(forTrie, Is.Not.SameAs(receipts));
            for (int i = 0; i < receipts.Length; i++)
            {
                Assert.That(receipts[i], Is.SameAs(before[i]));
                Assert.That(receipts[i].TxType, Is.EqualTo(TxType.EIP1559));
            }
        });
    }

    [Test]
    public void NothingToAdjust_ReturnsTheSameArray()
    {
        TxReceipt[] receipts = [Receipt(TestItem.AddressD, TxType.EIP1559), Receipt(BlockSigner, TxType.Legacy)];

        Assert.That(XdcBlockProcessor.AsEncodedForTrie(receipts, Spec), Is.SameAs(receipts));
    }
}
