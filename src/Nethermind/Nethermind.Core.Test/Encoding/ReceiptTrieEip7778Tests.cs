// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.State.Proofs;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding;

[TestFixture]
public class ReceiptTrieEip7778Tests
{
    [Test]
    public void Receipts_root_ignores_gas_spent_when_eip7778_disabled()
    {
        TxReceipt baseReceipt = BuildReceipt(0);
        TxReceipt updatedReceipt = BuildReceipt(123);

        IReceiptSpec spec = new ReleaseSpec { IsEip658Enabled = true, IsEip7778Enabled = false };
        Hash256 root1 = ReceiptTrie.CalculateRoot(spec, new[] { baseReceipt }, Rlp.GetStreamDecoder<TxReceipt>()!);
        Hash256 root2 = ReceiptTrie.CalculateRoot(spec, new[] { updatedReceipt }, Rlp.GetStreamDecoder<TxReceipt>()!);

        Assert.That(root2, Is.EqualTo(root1));
    }

    [Test]
    public void Receipts_root_includes_gas_spent_when_eip7778_enabled()
    {
        TxReceipt baseReceipt = BuildReceipt(0);
        TxReceipt updatedReceipt = BuildReceipt(123);

        IReceiptSpec spec = new ReleaseSpec { IsEip658Enabled = true, IsEip7778Enabled = true };
        Hash256 root1 = ReceiptTrie.CalculateRoot(spec, new[] { baseReceipt }, Rlp.GetStreamDecoder<TxReceipt>()!);
        Hash256 root2 = ReceiptTrie.CalculateRoot(spec, new[] { updatedReceipt }, Rlp.GetStreamDecoder<TxReceipt>()!);

        Assert.That(root2, Is.Not.EqualTo(root1));
    }

    private static TxReceipt BuildReceipt(long gasSpent)
    {
        TxReceipt receipt = Build.A.Receipt.TestObject;
        receipt.Logs = [];
        receipt.Bloom = new Bloom();
        receipt.StatusCode = 1;
        receipt.PostTransactionState = null;
        receipt.GasUsedTotal = 1000;
        receipt.GasSpent = gasSpent;
        return receipt;
    }
}
