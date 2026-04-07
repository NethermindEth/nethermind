// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.State.Proofs;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Proofs;

[Parallelizable(ParallelScope.All)]
public class ReceiptTrieTests
{
    private static readonly IRlpStreamEncoder<TxReceipt> _decoder = Rlp.GetStreamEncoder<TxReceipt>()!;
    private static readonly IRlpValueDecoder<TxReceipt> _valueDecoder = Rlp.GetValueDecoder<TxReceipt>()!;

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
        using var pool = new TrackingCappedArrayPool();
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
        for (int i = 0; i < receiptCount; i++)
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
        Rlp.ValueDecoderContext ctx = node.Value.ToArray().AsRlpValueContext();
        TxReceipt receipt = _valueDecoder.Decode(ref ctx);
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
