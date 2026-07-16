// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

public class InclusionListTxSourceTests
{
    private static InclusionListTxSource CreateSource() => new(
        new EthereumEcdsa(MainnetSpecProvider.Instance.ChainId),
        new CustomSpecProvider(((ForkActivation)0, Bogota.Instance)),
        LimboLogs.Instance);

    private static PayloadAttributes Attributes(byte[][] inclusionList) => new() { InclusionListTransactions = inclusionList };

    [Test]
    public void Empty_when_no_payload_attributes()
    {
        InclusionListTxSource source = CreateSource();
        BlockHeader parent = Build.A.BlockHeader.TestObject;

        Assert.That(source.GetTransactions(parent, 30_000_000UL), Is.Empty);
    }

    [Test]
    public void Empty_until_Set_is_called_for_the_build()
    {
        InclusionListTxSource source = CreateSource();
        Transaction tx = Build.A.Transaction.WithNonce(1).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        byte[][] il = [Encode(tx)];
        PayloadAttributes attrs = Attributes(il);

        Assert.That(source.GetTransactions(Build.A.BlockHeader.TestObject, 30_000_000UL, attrs), Is.Empty);

        source.Set(il, Bogota.Instance);
        Assert.That(
            source.GetTransactions(Build.A.BlockHeader.TestObject, 30_000_000UL, attrs).Select(t => t.Nonce),
            Is.EqualTo([1ul]));
    }

    // Regression (review r3595551678): a concurrent FCU writing another IL must not leak into a
    // running build. The list is scoped by the build's PayloadAttributes, so each build sees only its own.
    [Test]
    public void Inclusion_list_is_scoped_per_build()
    {
        InclusionListTxSource source = CreateSource();
        Transaction txA = Build.A.Transaction.WithNonce(1).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Transaction txB = Build.A.Transaction.WithNonce(2).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        byte[][] ilA = [Encode(txA)];
        byte[][] ilB = [Encode(txB)];
        PayloadAttributes attrsA = Attributes(ilA);
        PayloadAttributes attrsB = Attributes(ilB);

        // Build A supplies its IL, then build B supplies another before A consumes its list.
        source.Set(ilA, Bogota.Instance);
        source.Set(ilB, Bogota.Instance);

        Assert.That(source.GetTransactions(Build.A.BlockHeader.TestObject, 30_000_000UL, attrsA).Select(t => t.Nonce), Is.EqualTo([1ul]));
        Assert.That(source.GetTransactions(Build.A.BlockHeader.TestObject, 30_000_000UL, attrsB).Select(t => t.Nonce), Is.EqualTo([2ul]));
    }

    [Test]
    public void Set_with_empty_array_yields_empty()
    {
        InclusionListTxSource source = CreateSource();
        byte[][] il = [];
        PayloadAttributes attrs = Attributes(il);

        source.Set(il, Bogota.Instance);
        Assert.That(source.GetTransactions(Build.A.BlockHeader.TestObject, 30_000_000UL, attrs), Is.Empty);
    }

    // Per spec, blob (EIP-4844) transactions are excluded from the inclusion list.
    [Test]
    public void SupportsBlobs_is_false() => Assert.That(CreateSource().SupportsBlobs, Is.False);

    private static byte[] Encode(Transaction tx) => TxDecoder.Instance.Encode(tx, RlpBehaviors.SkipTypedWrapping).Bytes;
}
