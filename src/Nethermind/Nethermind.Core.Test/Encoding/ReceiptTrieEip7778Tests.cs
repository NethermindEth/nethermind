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
    public void Receipts_root_same_when_eip7778_disabled_or_enabled()
    {
        // After revert from ethereum/execution-specs#2073, GasSpent is no longer part of receipts
        // So the receipt trie root should be the same regardless of EIP-7778
        TxReceipt receipt = BuildReceipt();

        IReceiptSpec specWithout7778 = new ReleaseSpec { IsEip658Enabled = true, IsEip7778Enabled = false };
        IReceiptSpec specWith7778 = new ReleaseSpec { IsEip658Enabled = true, IsEip7778Enabled = true };

        Hash256 root1 = ReceiptTrie.CalculateRoot(specWithout7778, new[] { receipt }, Rlp.GetStreamDecoder<TxReceipt>()!);
        Hash256 root2 = ReceiptTrie.CalculateRoot(specWith7778, new[] { receipt }, Rlp.GetStreamDecoder<TxReceipt>()!);

        Assert.That(root2, Is.EqualTo(root1));
    }

    private static TxReceipt BuildReceipt()
    {
        TxReceipt receipt = Build.A.Receipt.TestObject;
        receipt.Logs = [];
        receipt.Bloom = new Bloom();
        receipt.StatusCode = 1;
        receipt.PostTransactionState = null;
        receipt.GasUsedTotal = 1000;
        return receipt;
    }
}
