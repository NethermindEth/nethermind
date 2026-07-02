// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
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

    [Test]
    public void Empty_until_Set_is_called()
    {
        InclusionListTxSource source = CreateSource();
        BlockHeader parent = Build.A.BlockHeader.TestObject;

        Assert.That(source.GetTransactions(parent, 30_000_000UL), Is.Empty);
    }

    [Test]
    public void Set_replaces_previous_inclusion_list()
    {
        InclusionListTxSource source = CreateSource();
        Transaction tx1 = Build.A.Transaction.WithNonce(1).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Transaction tx2 = Build.A.Transaction.WithNonce(2).SignedAndResolved(TestItem.PrivateKeyB).TestObject;

        source.Set([Encode(tx1)], Bogota.Instance);
        Assert.That(
            source.GetTransactions(Build.A.BlockHeader.TestObject, 30_000_000UL).Select(t => t.Nonce),
            Is.EqualTo([1ul]));

        source.Set([Encode(tx2)], Bogota.Instance);
        Assert.That(
            source.GetTransactions(Build.A.BlockHeader.TestObject, 30_000_000UL).Select(t => t.Nonce),
            Is.EqualTo([2ul]));
    }

    [Test]
    public void Set_with_empty_array_drains_to_empty()
    {
        InclusionListTxSource source = CreateSource();
        Transaction tx = Build.A.Transaction.WithNonce(5).SignedAndResolved(TestItem.PrivateKeyA).TestObject;

        source.Set([Encode(tx)], Bogota.Instance);
        Assert.That(source.GetTransactions(Build.A.BlockHeader.TestObject, 30_000_000UL).Count(), Is.EqualTo(1));

        source.Set([], Bogota.Instance);
        Assert.That(source.GetTransactions(Build.A.BlockHeader.TestObject, 30_000_000UL), Is.Empty);
    }

    // Per spec, blob (EIP-4844) transactions are excluded from the inclusion list.
    [Test]
    public void SupportsBlobs_is_false() => Assert.That(CreateSource().SupportsBlobs, Is.False);

    private static byte[] Encode(Transaction tx) => TxDecoder.Instance.Encode(tx, RlpBehaviors.SkipTypedWrapping).Bytes;
}
