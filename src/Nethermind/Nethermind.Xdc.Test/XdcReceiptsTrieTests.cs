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

    [Test]
    public void StoredReceiptKeepsItsType()
    {
        TxReceipt typed = Receipt(BlockSigner, TxType.EIP1559);

        XdcBlockProcessor.AsEncodedForTrie([typed], Spec);

        Assert.That(typed.TxType, Is.EqualTo(TxType.EIP1559));
    }
}
