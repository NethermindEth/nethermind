// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using Nethermind.State.Proofs;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Proofs;

[TestFixture(true)]
[TestFixture(false)]
[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class TxTrieTests(bool useEip2718)
{
    private readonly IReleaseSpec _releaseSpec = useEip2718 ? Berlin.Instance : MuirGlacier.Instance;

    private static readonly int[] RootCounts = [0, 1, 2, 15, 16, 17, 63, 64, 65, 127, 128, 129, 255, 256, 257, 4096];

    [Test]
    public void Root_matches_mutable_trie([ValueSource(nameof(RootCounts))] int count, [Values] bool cached)
    {
        Transaction[] transactions = new Transaction[count];
        byte[][] encoded = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            Transaction transaction = Build.A.Transaction.WithNonce(i).WithType(useEip2718 ? (TxType)(i % 5) : TxType.Legacy)
                .WithData(new byte[i % 128]).WithBlobVersionedHashes(1).WithMaxFeePerBlobGas(1).WithAuthorizationCodeIfAuthorizationListTx()
                .WithSignature(new Signature(new byte[64], 0)).TestObject;
            encoded[i] = Rlp.Encode(transaction, RlpBehaviors.SkipTypedWrapping).Bytes;
            if (cached) transaction.SetPreHashMemoryNoLock(encoded[i]);
            transactions[i] = transaction;
        }

        using TrackingCappedArrayPool pool = new();
        Hash256 expected = new TxTrie(transactions, bufferPool: pool, canBeParallel: false).RootHash;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(TxTrie.CalculateRoot(transactions), Is.EqualTo(expected));
            Assert.That(TxTrie.CalculateRoot(encoded), Is.EqualTo(expected));
        }
    }

    [Test]
    public void Encoded_root_matches_mutable_trie([ValueSource(nameof(RootCounts))] int count, [Values] bool sparse)
    {
        byte[][] encoded = new byte[count][];
        Random random = new(42);
        using TrackingCappedArrayPool pool = new();
        TxTrie trie = new(ReadOnlySpan<Transaction>.Empty, bufferPool: pool, canBeParallel: false);
        for (int i = 0; i < count; i++)
        {
            // Include unprefixed bytes, inline nodes, and the 32/56-byte RLP boundaries.
            byte[] value = new byte[sparse && i % 3 == 0 ? 0 : i % 65 + 1];
            random.NextBytes(value);
            encoded[i] = sparse && i % 6 == 0 ? null! : value;
            trie.Set(Rlp.Encode(i).Bytes, value);
        }
        trie.UpdateRootHash(canBeParallel: false);

        Assert.That(TxTrie.CalculateRoot(encoded), Is.EqualTo(trie.RootHash));
    }

    [TestCase(65535)]
    [TestCase(65536)]
    [TestCase(65537)]
    [NonParallelizable]
    public void Encoded_root_handles_three_byte_indices(int count)
    {
        byte[][] encoded = new byte[count][];
        Array.Fill(encoded, new byte[] { 1 });
        using TrackingCappedArrayPool pool = new();
        TxTrie trie = new(ReadOnlySpan<Transaction>.Empty, bufferPool: pool, canBeParallel: false);
        for (int i = 0; i < count; i++) trie.Set(Rlp.Encode(i).Bytes, encoded[i]);
        trie.UpdateRootHash(canBeParallel: false);

        Assert.That(TxTrie.CalculateRoot(encoded), Is.EqualTo(trie.RootHash));
    }

    [TestCase(1)]
    [TestCase(128)]
    public void Cached_rlp_slice_takes_precedence_and_is_preserved(int count)
    {
        Transaction transaction = Build.A.Transaction.TestObject;
        byte[] encoded = Rlp.Encode(transaction, RlpBehaviors.SkipTypedWrapping).Bytes;
        byte[] buffer = new byte[encoded.Length + 2];
        buffer[0] = buffer[^1] = 0xff;
        encoded.CopyTo(buffer.AsSpan(1));
        transaction.SetPreHashMemoryNoLock(buffer.AsMemory(1, encoded.Length));
        transaction.Type = (TxType)127;
        Transaction[] transactions = new Transaction[count];
        byte[][] values = new byte[count][];
        Array.Fill(transactions, transaction);
        Array.Fill(values, encoded);

        Hash256 root = TxTrie.CalculateRoot(transactions);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(root, Is.EqualTo(TxTrie.CalculateRoot(values)));
            Assert.That(buffer.AsSpan(1, encoded.Length).ToArray(), Is.EqualTo(encoded));
            Assert.That(buffer[0], Is.EqualTo(0xff));
            Assert.That(buffer[^1], Is.EqualTo(0xff));
        }
    }

    [TestCase(1)]
    [TestCase(128)]
    public void Encoding_failure_does_not_affect_the_next_root(int count)
    {
        Transaction transaction = Build.A.Transaction.TestObject;
        Transaction[] transactions = new Transaction[count];
        Array.Fill(transactions, Build.A.Transaction.TestObject);
        transactions[^1] = transaction;
        Hash256 expected = TxTrie.CalculateRoot(transactions);
        transaction.Type = (TxType)127;
        Assert.Throws<RlpException>(() => TxTrie.CalculateRoot(transactions));

        transaction.Type = TxType.Legacy;
        Assert.That(TxTrie.CalculateRoot(transactions), Is.EqualTo(expected));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_calculate_root()
    {
        Block block = Build.A.Block.WithTransactions(Build.A.Transaction.TestObject).TestObject;
        Hash256 rootHash = TxTrie.CalculateRoot(block.Transactions);

        if (_releaseSpec == Berlin.Instance)
        {
            Assert.That(rootHash.ToString(), Is.EqualTo("0x29cc403075ed3d1d6af940d577125cc378ee5a26f7746cbaf87f1cf4a38258b5"));
        }
        else
        {
            Assert.That(rootHash.ToString(), Is.EqualTo("0x29cc403075ed3d1d6af940d577125cc378ee5a26f7746cbaf87f1cf4a38258b5"));
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_collect_proof_trie_case_1()
    {
        Block block = Build.A.Block.WithTransactions(Build.A.Transaction.TestObject).TestObject;
        using TrackingCappedArrayPool pool = new();
        TxTrie txTrie = new(block.Transactions, true, pool);
        byte[][] proof = txTrie.BuildProof(0);

        txTrie.UpdateRootHash();
        VerifyProof(proof, txTrie.RootHash);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_collect_proof_with_trie_case_2()
    {
        Block block = Build.A.Block.WithTransactions(Build.A.Transaction.TestObject, Build.A.Transaction.TestObject).TestObject;
        using TrackingCappedArrayPool pool = new();
        TxTrie txTrie = new(block.Transactions, true, pool);
        byte[][] proof = txTrie.BuildProof(0);
        Assert.That(proof.Length, Is.EqualTo(2));

        txTrie.UpdateRootHash();
        VerifyProof(proof, txTrie.RootHash);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_collect_proof_with_trie_case_3_modified()
    {
        Block block = Build.A.Block.WithTransactions(Enumerable.Repeat(Build.A.Transaction.TestObject, 1000).ToArray()).TestObject;
        using TrackingCappedArrayPool pool = new();
        TxTrie txTrie = new(block.Transactions, true, pool);

        txTrie.UpdateRootHash();
        for (int i = 0; i < 1000; i++)
        {
            byte[][] proof = txTrie.BuildProof(i);
            VerifyProof(proof, txTrie.RootHash);
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Encoded_and_decoded_transaction_paths_have_same_root()
    {
        Transaction[] transactions =
        [
            Build.A.Transaction.WithNonce(1).WithType(TxType.Legacy).Signed().TestObject,
            Build.A.Transaction.WithNonce(2).WithType(useEip2718 ? TxType.EIP1559 : TxType.Legacy).Signed().TestObject,
            Build.A.Transaction.WithNonce(3).WithType(useEip2718 ? TxType.AccessList : TxType.Legacy).Signed().TestObject,
        ];

        byte[][] encodedTransactions = transactions
            .Select(static tx => Rlp.Encode(tx, RlpBehaviors.SkipTypedWrapping).Bytes)
            .ToArray();

        Hash256 decodedRoot = TxTrie.CalculateRoot(transactions);
        Hash256 encodedRoot = TxTrie.CalculateRoot(encodedTransactions);

        Assert.That(encodedRoot, Is.EqualTo(decodedRoot));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Parallel_and_non_parallel_root_hashing_produce_same_root()
    {
        const int txCount = 100;
        Transaction[] transactions = new Transaction[txCount];
        for (uint i = 0; i < txCount; i++)
        {
            transactions[i] = Build.A.Transaction.WithNonce(i + 1).Signed().TestObject;
        }

        using TrackingCappedArrayPool pool = new();
        TxTrie txTrie = new(transactions, canBuildProof: false, pool);
        Hash256 parallelRoot = txTrie.RootHash;

        txTrie.UpdateRootHash(canBeParallel: false);
        Hash256 nonParallelRoot = txTrie.RootHash;

        Assert.That(nonParallelRoot, Is.EqualTo(parallelRoot));
    }

    private static void VerifyProof(byte[][] proof, Hash256 txRoot)
    {
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
            else
            {
                if (proofHash != txRoot)
                {
                    throw new InvalidDataException();
                }
            }
        }
    }
}
