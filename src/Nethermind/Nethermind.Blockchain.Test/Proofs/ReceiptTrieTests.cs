// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State.Proofs;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Proofs;

[Parallelizable(ParallelScope.All)]
public class ReceiptTrieTests
{
    private static readonly ReceiptMessageDecoder _decoder = new();
    private static readonly int[] RootCounts = [0, 1, 2, 15, 16, 17, 63, 64, 65, 127, 128, 129, 255, 256, 257, 4096];
    private static readonly int[] InlineCounts = [1, 2, 3, 4, 8, 16, 128, 129];

    [Test]
    public void Direct_root_matches_mutable_trie(
        [ValueSource(nameof(RootCounts))] int count,
        [Values] bool eip658,
        [Values] bool skipStateAndStatus)
    {
        IReleaseSpec spec = eip658 ? Osaka.Instance : Frontier.Instance;
        TxReceipt[] receipts = new TxReceipt[count];
        Random random = new(42);
        for (int i = 0; i < count; i++)
        {
            byte[] data = new byte[i % 99];
            random.NextBytes(data);
            receipts[i] = Build.A.Receipt.WithAllFieldsFilled.WithGasUsedTotal((ulong)(i + 1) * 21000)
                .WithStatusCode((byte)(i & 1)).WithTxType(eip658 ? (TxType)(i % 5) : TxType.Legacy)
                .WithLogs(new LogEntry(TestItem.AddressA, data, [TestItem.KeccakA, TestItem.KeccakB])).TestObject;
        }

        AssertRootMatches(spec, receipts, new ReceiptMessageDecoder(skipStateAndStatus));
    }

    [Test, NonParallelizable]
    public void Direct_root_matches_at_three_byte_index_boundary([Values(65535, 65536, 65537)] int count)
    {
        TxReceipt[] receipts = new TxReceipt[count];
        Array.Fill(receipts, Build.A.Receipt.WithAllFieldsFilled.TestObject);
        AssertRootMatches(Osaka.Instance, receipts, _decoder);
    }

    [Test]
    public void Direct_root_matches_with_inline_nodes([ValueSource(nameof(InlineCounts))] int count, [Values] bool mixed)
    {
        TxReceipt[] receipts = new TxReceipt[count];
        if (mixed)
        {
            for (int i = 1; i < count; i += 2)
                receipts[i] = Build.A.Receipt.WithAllFieldsFilled.TestObject;
        }
        AssertRootMatches(Osaka.Instance, receipts, _decoder);
    }

    [Test]
    public void Custom_receipt_codec_preserves_empty_values()
    {
        TxReceipt[] receipts = [Build.A.Receipt.WithAllFieldsFilled.TestObject];
        Assert.That(ReceiptTrie.CalculateRoot(Osaka.Instance, receipts, new EmptyReceiptDecoder()), Is.EqualTo(Keccak.EmptyTreeHash));
    }

    private sealed class EmptyReceiptDecoder : RlpDecoder<TxReceipt>
    {
        protected override TxReceipt DecodeInternal(ref RlpReader reader, RlpBehaviors rlpBehaviors) => throw new NotSupportedException();
        public override int GetLength(TxReceipt item, RlpBehaviors rlpBehaviors) => 0;
        public override void Encode<TWriter>(ref TWriter writer, TxReceipt item, RlpBehaviors rlpBehaviors) { }
    }

    [Test]
    public void Encoding_failure_does_not_poison_subsequent_calculations([Values(1, 128)] int count)
    {
        TxReceipt receipt = Build.A.Receipt.WithAllFieldsFilled.TestObject;
        LogEntry[] logs = receipt.Logs!;
        TxReceipt[] receipts = new TxReceipt[count];
        Array.Fill(receipts, receipt);
        receipt.Logs = null;

        Assert.That(() => ReceiptTrie.CalculateRoot(Osaka.Instance, receipts, _decoder), Throws.TypeOf<RlpException>());

        receipt.Logs = logs;
        AssertRootMatches(Osaka.Instance, receipts, _decoder);
    }

    private static void AssertRootMatches(IReleaseSpec spec, TxReceipt[] receipts, IRlpDecoder<TxReceipt> decoder)
    {
        using TrackingCappedArrayPool pool = new();
        Hash256 expected = new ReceiptTrie(spec, receipts, decoder, pool, canBeParallel: false).RootHash;
        Hash256 actual = ReceiptTrie.CalculateRoot(spec, receipts, decoder);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual, Is.EqualTo(expected));
            if (receipts.Length > 0)
            {
                byte[][] proof = ReceiptTrie.CalculateReceiptProofs(spec, receipts, receipts.Length / 2, decoder);
                Assert.That(Keccak.Compute(proof[0]), Is.EqualTo(actual), "proof root must match the streamed root");
            }
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_calculate_root_no_eip_658()
    {
        TxReceipt receipt = Build.A.Receipt.WithAllFieldsFilled.TestObject;
        Hash256 rootHash = ReceiptTrie.CalculateRoot(MainnetSpecProvider.Instance.GetSpec((1, null)),
            [receipt], _decoder);
        Assert.That(rootHash.ToString(),
            Is.EqualTo("0xe51a2d9f986d68628990c9d65e45c36128ec7bb697bd426b0bb4d18a3f3321be"));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_calculate_root()
    {
        TxReceipt receipt = Build.A.Receipt.WithAllFieldsFilled.TestObject;
        Hash256 rootHash = ReceiptTrie.CalculateRoot(
            MainnetSpecProvider.Instance.GetSpec((MainnetSpecProvider.MuirGlacierBlockNumber, null)),
            [receipt], _decoder);
        Assert.That(rootHash.ToString(),
            Is.EqualTo("0x2e6d89c5b539e72409f2e587730643986c2ef33db5e817a4223aa1bb996476d5"));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_collect_proof_with_branch()
    {
        TxReceipt receipt1 = Build.A.Receipt.WithAllFieldsFilled.TestObject;
        TxReceipt receipt2 = Build.A.Receipt.WithAllFieldsFilled.TestObject;
        using TrackingCappedArrayPool pool = new();
        ReceiptTrie trie = new(MainnetSpecProvider.Instance.GetSpec((ForkActivation)1),
            [receipt1, receipt2], _decoder, pool, true);
        byte[][] proof = trie.BuildProof(0);
        Assert.That(proof.Length, Is.EqualTo(2));

        trie.UpdateRootHash();
        VerifyProof(proof, trie.RootHash);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Parallel_and_non_parallel_root_hashing_produce_same_root()
    {
        const int receiptCount = 100;
        IReleaseSpec spec = MainnetSpecProvider.Instance.GetSpec((MainnetSpecProvider.MuirGlacierBlockNumber, null));
        TxReceipt[] receipts = new TxReceipt[receiptCount];
        for (uint i = 0; i < receiptCount; i++)
        {
            receipts[i] = Build.A.Receipt.WithAllFieldsFilled.WithGasUsedTotal(1000 + i).TestObject;
        }

        using TrackingCappedArrayPool parallelPool = new(receiptCount * 4, canBeParallel: true);
        ReceiptTrie parallelTrie = new(spec, receipts, _decoder, parallelPool, canBeParallel: true);
        Hash256 parallelRoot = parallelTrie.RootHash;

        using TrackingCappedArrayPool sequentialPool = new(receiptCount * 4, canBeParallel: false);
        ReceiptTrie sequentialTrie = new(spec, receipts, _decoder, sequentialPool, canBeParallel: false);
        Hash256 sequentialRoot = sequentialTrie.RootHash;

        Assert.That(sequentialRoot, Is.EqualTo(parallelRoot));
    }

    private void VerifyProof(byte[][] proof, Hash256 receiptRoot)
    {
        TrieNode node = new(NodeType.Unknown, proof.Last());
        node.ResolveNode(Substitute.For<ITrieNodeResolver>(), TreePath.Empty);
        RlpReader ctx = new(node.Value.AsSpan());
        TxReceipt receipt = _decoder.DecodeGuardNotNull(ref ctx);
        Assert.That(receipt.Bloom, Is.Not.Null);

        for (int i = proof.Length; i > 0; i--)
        {
            Hash256 proofHash = Keccak.Compute(proof[i - 1]);
            if (i > 1)
            {
                if (!new Rlp(proof[i - 2]).ToString(false).Contains(proofHash.ToString(false)))
                {
                    throw new InvalidDataException();
                }
            }
            else if (proofHash != receiptRoot)
            {
                throw new InvalidDataException();
            }
        }
    }
}
