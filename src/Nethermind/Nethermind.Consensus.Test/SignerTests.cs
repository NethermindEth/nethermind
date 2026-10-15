// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

public class SignerTests
{
    [Test]
    public void Legacy_v_at_the_largest_chain_id_round_trips()
    {
        Signer signer = new(EthereumEcdsaExtensions.MaxEip155ChainId, TestItem.PrivateKeyA, LimboLogs.Instance);
        Transaction tx = Build.A.Transaction.WithType(TxType.Legacy).TestObject;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(signer.TrySign(tx), Is.True);
            Assert.That(tx.Signature!.ChainId, Is.EqualTo(EthereumEcdsaExtensions.MaxEip155ChainId));
        }
    }

    [Test]
    public void Legacy_v_throws_where_it_would_wrap()
    {
        Signer signer = new(ulong.MaxValue, TestItem.PrivateKeyA, LimboLogs.Instance);
        Transaction tx = Build.A.Transaction.WithType(TxType.Legacy).TestObject;

        Assert.Throws<OverflowException>(() => signer.TrySign(tx));
    }
}
