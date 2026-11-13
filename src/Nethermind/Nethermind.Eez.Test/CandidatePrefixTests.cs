// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Execution.Stateless;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class CandidatePrefixTests
{
    // More than 128 transactions, so the keys cross from one-byte to two-byte RLP integers.
    private const int TransactionCount = 260;
    private static readonly IRlpDecoder<TxReceipt> ReceiptTrieDecoder = Rlp.GetDecoderOrThrow<TxReceipt>(RlpDecoderKey.Trie);

    private Transaction[] _transactions = null!;
    private TxReceipt[] _receipts = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _transactions = new Transaction[TransactionCount];
        _receipts = new TxReceipt[TransactionCount];
        ulong gasUsedTotal = 0;
        for (int i = 0; i < TransactionCount; i++)
        {
            TxType type = i % 3 == 0 ? TxType.Legacy : TxType.EIP1559;
            _transactions[i] = Build.A.Transaction.WithType(type).WithNonce(i).WithMaxFeePerGas(2).WithTo(TestItem.Addresses[i % 7])
                .SignedAndResolved(TestItem.PrivateKeys[i % 5]).TestObject;
            gasUsedTotal += 21_000 + (ulong)i;
            LogEntry[] logs = i % 4 == 0 ? [] : [Build.A.LogEntry.WithAddress(TestItem.Addresses[i % 11]).WithTopics(Keccak.Compute($"topic {i}")).TestObject];
            _receipts[i] = Build.A.Receipt.WithTxType(type).WithStatusCode((byte)(i % 2)).WithState(Keccak.Compute($"state {i}"))
                .WithGasUsedTotal(gasUsedTotal).WithLogs(logs).WithCalculatedBloom().TestObject;
        }
    }

    /// <summary>
    /// A plan of one checkpoint per transaction is the dense case a checkpoint per effect produces. Every checkpoint of
    /// the incremental prefix must equal the roots and bloom built from transaction zero for that prefix.
    /// </summary>
    [Test]
    public void Extend_EveryCheckpointOfAPlan_MatchesTheFromScratchPrefix(
        [Values(1, 7)] int stride, [Values(false, true)] bool eip658, [Values(false, true)] bool eip7668)
    {
        IReceiptSpec spec = Substitute.For<IReceiptSpec>();
        spec.IsEip658Enabled.Returns(eip658);
        spec.IsEip7668Enabled.Returns(eip7668);
        List<int> plan = [EezTransactionCheckpoint.PreExecution];
        for (int index = 0; index < TransactionCount; index += stride)
        {
            plan.Add(index);
        }

        if (plan[^1] != TransactionCount - 1)
        {
            plan.Add(TransactionCount - 1);
        }

        using CandidatePrefix prefix = new(spec, TransactionCount);
        foreach (int checkpoint in plan)
        {
            int count = checkpoint + 1;
            prefix.Extend(_transactions, _receipts, count);
            Bloom bloom = new();
            for (int i = 0; i < count; i++)
            {
                bloom.Accumulate(_receipts[i].Bloom);
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(prefix.Count, Is.EqualTo(count));
                Assert.That(prefix.TxRoot, Is.EqualTo(TxTrie.CalculateRoot(_transactions.AsSpan(0, count))), $"transactions root of prefix {count}");
                Assert.That(prefix.ReceiptsRoot, Is.EqualTo(ReceiptTrie.CalculateRoot(spec, _receipts.AsSpan(0, count), ReceiptTrieDecoder)), $"receipts root of prefix {count}");
                Assert.That(prefix.Bloom, Is.EqualTo(bloom), $"bloom of prefix {count}");
            }
        }
    }

    [Test]
    public void Extend_ShorterOrBeyondTheBlock_IsRejected()
    {
        using CandidatePrefix prefix = new(Substitute.For<IReceiptSpec>(), TransactionCount);
        prefix.Extend(_transactions, _receipts, 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => prefix.Extend(_transactions, _receipts, 1), Throws.TypeOf<ArgumentOutOfRangeException>(), "a prefix cannot shrink");
            Assert.That(() => prefix.Extend(_transactions, _receipts, TransactionCount + 1), Throws.TypeOf<ArgumentOutOfRangeException>(), "a prefix cannot pass the block");
            Assert.That(prefix.TxRoot, Is.EqualTo(TxTrie.CalculateRoot(_transactions.AsSpan(0, 2))), "a rejected extension keeps the prefix");
        }
    }
}
