// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.IO;
using System.Linq;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.TxDecoders;
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

    private static readonly int[] MultiBlockLengths = [31, 100, 132, 133, 134, 135, 261, 262, 263, 264, 300, 1000, 2164, 2165, 8192];

    private static readonly int[] RootCounts = [0, 1, 2, 7, 8, 9, 15, 16, 17, 18, 19, 20, 21, 22, 23, 31, 32, 33, 63, 64, 65, 79, 80, 81, 127, 128, 129, 143, 144, 145, 255, 256, 257, 271, 272, 273, 4096];

    [Test]
    public void Sequential_multi_block_batch_requires_all_items_to_be_eligible(
        [Values(8, 17, 40, 64)] int count, [Values(124, 125, 2164, 2165)] int otherLength,
        [Values] bool multiBlock)
    {
        byte[][] values = new byte[count][];
        Array.Fill(values, new byte[otherLength]);
        values[1] = new byte[500];
        IndexedTrieRoot.Calculator<byte[], TestValueEncoder> calculator = new(values, new(multiBlock));
        Assert.That(calculator.TryGetMultiBlockLengths(new int[count]),
            Is.EqualTo(otherLength is > 124 and <= 2164));

        using TrackingCappedArrayPool pool = new();
        TxTrie expected = new(ReadOnlySpan<Transaction>.Empty, bufferPool: pool, canBeParallel: false);
        for (int i = 0; i < count; i++) expected.Set(Rlp.Encode(i).Bytes, values[i]);
        expected.UpdateRootHash(canBeParallel: false);
        Assert.That(calculator.Calculate(minItemsForParallel: IndexedTrieRoot.MinReceiptsForParallelRootHash),
            Is.EqualTo(expected.RootHash));
    }

    [Test]
    public void Sequential_multi_block_batch_computes_each_length_once(
        [Values(8, 16, 17, 64)] int count, [Values] bool multiBlock) =>
        AssertRootAndLengthCalls(count, multiBlock, -1, 0, true);

    [Test]
    public void Mixed_root_reuses_lengths_when_batch_admission_fails(
        [Values(17, 64)] int count, [Values] bool multiBlock, [Values(0, 1)] int outlierIndex,
        [Values(124, 2165)] int outlierLength, [Values] bool canBeParallel) =>
        AssertRootAndLengthCalls(count, multiBlock, outlierIndex, outlierLength, canBeParallel);

    private static void AssertRootAndLengthCalls(int count, bool multiBlock, int outlierIndex, int outlierLength, bool canBeParallel)
    {
        if (!Avx2.IsSupported) Assert.Ignore("Requires AVX2.");
        byte[][] values = new byte[count][];
        int[] lengthCalls = new int[count];
        using TrackingCappedArrayPool pool = new();
        TxTrie expected = new(ReadOnlySpan<Transaction>.Empty, bufferPool: pool, canBeParallel: false);
        for (int i = 0; i < count; i++)
        {
            byte[] value = new byte[i == outlierIndex ? outlierLength : 125 + i * 17];
            value[0] = (byte)i;
            values[i] = value;
            expected.Set(Rlp.Encode(i).Bytes, value);
        }
        expected.UpdateRootHash(canBeParallel: false);

        Hash256 actual = new IndexedTrieRoot.Calculator<byte[], TestValueEncoder>(values, new(multiBlock, lengthCalls))
            .Calculate(canBeParallel, IndexedTrieRoot.MinReceiptsForParallelRootHash);
        using (Assert.EnterMultipleScope())
        {
            if (multiBlock || outlierIndex < 0) Assert.That(lengthCalls, Is.All.EqualTo(1));
            else Assert.That(lengthCalls, Is.All.LessThanOrEqualTo(1));
            Assert.That(actual, Is.EqualTo(expected.RootHash));
        }
    }

    [Test]
    public void Encoded_leaf_length_takes_precedence_over_length_hint(
        [Values(8, 17, 64, 65)] int count, [Values(124, 136, 2165)] int actualLength,
        [Values(125, 2164)] int reportedLength)
    {
        byte[][] values = new byte[count][];
        Random random = new(4231);
        using TrackingCappedArrayPool pool = new();
        TxTrie expected = new(ReadOnlySpan<Transaction>.Empty, bufferPool: pool, canBeParallel: false);
        for (int i = 0; i < count; i++)
        {
            byte[] value = new byte[actualLength];
            random.NextBytes(value);
            values[i] = value;
            expected.Set(Rlp.Encode(i).Bytes, value);
        }
        expected.UpdateRootHash(canBeParallel: false);

        Hash256 actual = new IndexedTrieRoot.Calculator<byte[], TestValueEncoder>(values, new(false, reportedLength: reportedLength)).Calculate();

        Assert.That(actual, Is.EqualTo(expected.RootHash));
    }

    [Test]
    public void Terminal_branch_batches_include_eligible_tail(
        [Values(144, 160, 176, 200, 256, 272, 400)] int count)
    {
        if (!Avx2.IsSupported) Assert.Ignore("Requires AVX2.");
        byte[][] values = new byte[count][];
        IndexedTrieRoot.NodeReference[] references = new IndexedTrieRoot.NodeReference[count];
        for (int i = 0; i < count; i++)
            references[i] = new(ValueKeccak.Compute(BitConverter.GetBytes(i)), Keccak.Size);
        IndexedTrieRoot.NodeReference[] original = (IndexedTrieRoot.NodeReference[])references.Clone();
        IndexedTrieRoot.Calculator<byte[], TestValueEncoder> calculator = new(values, default);
        calculator.BatchTerminalBranches(references);

        int eligible = count / 16 - 1;
        int expectedBatched = Avx512F.IsSupported
            ? eligible - (eligible % 8 == 1 ? 1 : 0)
            : eligible / 4 * 4;
        Assert.That(references.Count(reference => reference.Length == -Keccak.Size), Is.EqualTo(expectedBatched));
        byte[] branch = new byte[KeccakHash.Hash532InputLength];
        for (int i = 0; i < count; i++)
        {
            if (references[i].Length != -Keccak.Size)
            {
                Assert.That(references[i], Is.EqualTo(original[i]));
                continue;
            }
            RlpWriter writer = new(branch);
            writer.StartSequence(16 * Rlp.LengthOfKeccakRlp + 1);
            for (int j = 0; j < 16; j++) writer.Encode(original[i + j].Value);
            writer.Encode(ReadOnlySpan<byte>.Empty);
            Assert.That(references[i].Value, Is.EqualTo(ValueKeccak.Compute(branch)));
        }
    }

    private readonly struct TestValueEncoder(bool multiBlock, int[]? lengthCalls = null, int? reportedLength = null) : IndexedTrieRoot.IValueEncoder<byte[]>
    {
        public IndexedTrieRoot.LeafBatching Batching => multiBlock ? IndexedTrieRoot.LeafBatching.MultiBlock : IndexedTrieRoot.LeafBatching.Encoded;
        public ReadOnlySpan<byte> GetEncodedValue(byte[] item) => multiBlock ? default : item;
        public int GetLength(byte[] item)
        {
            if (lengthCalls is not null) Interlocked.Increment(ref lengthCalls[item[0]]);
            return reportedLength ?? item.Length;
        }
        public void Encode<TWriter>(ref TWriter writer, byte[] item)
            where TWriter : struct, IRlpWriteBackend, allows ref struct => writer.Write(item);
    }

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
            Assert.That(TxTrie.CalculateRoot(transactions, canBeParallel: false), Is.EqualTo(expected));
            Assert.That(TxTrie.CalculateRoot(encoded), Is.EqualTo(expected));
        }
        if (count > 0) VerifyProof(TxTrie.CalculateProof(transactions, count / 2), TxTrie.CalculateRoot(transactions));
    }

    [Test]
    public void Encoded_root_matches_mutable_trie([ValueSource(nameof(RootCounts))] int count, [Values] bool sparse,
        [Values(-1, 0, 124, 125, 136, 300, 2164, 2165, 8192)] int valueLength)
    {
        byte[][] encoded = new byte[count][];
        Random random = new(42);
        using TrackingCappedArrayPool pool = new();
        TxTrie trie = new(ReadOnlySpan<Transaction>.Empty, bufferPool: pool, canBeParallel: false);
        for (int i = 0; i < count; i++)
        {
            // Include unprefixed bytes, inline nodes, and the 32/56-byte RLP boundaries.
            int length = valueLength == -1 ? MultiBlockLengths[i % MultiBlockLengths.Length] : valueLength == 0 ? i % 65 + 1 : valueLength;
            byte[] value = new byte[sparse && i % 3 == 0 ? 0 : length];
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
        VerifyProof(proof, TxTrie.CalculateRoot(block.Transactions));
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
        VerifyProof(proof, TxTrie.CalculateRoot(block.Transactions));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_collect_proof_with_trie_case_3_modified()
    {
        Block block = Build.A.Block.WithTransactions(Enumerable.Repeat(Build.A.Transaction.TestObject, 1000).ToArray()).TestObject;
        using TrackingCappedArrayPool pool = new();
        TxTrie txTrie = new(block.Transactions, true, pool);

        txTrie.UpdateRootHash();
        Hash256 streamedRoot = TxTrie.CalculateRoot(block.Transactions);
        for (int i = 0; i < 1000; i++)
        {
            byte[][] proof = txTrie.BuildProof(i);
            VerifyProof(proof, streamedRoot);
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(TxTrie.CalculateRoot(transactions, canBeParallel: true), Is.EqualTo(txTrie.RootHash));
            Assert.That(TxTrie.CalculateRoot(transactions, canBeParallel: false), Is.EqualTo(txTrie.RootHash));
        }
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

[NonParallelizable]
public class TxTrieCodecTests
{
    [Test]
    public void Registered_codec_encodes_on_the_caller_thread([Values(1, 128)] int count, [Values] bool canBeParallel)
    {
        using TestTxDecoder decoder = new();
        Transaction[] transactions = new Transaction[count];
        Array.Fill(transactions, Build.A.Transaction.WithType(decoder.Type).TestObject);
        using TrackingCappedArrayPool pool = new();
        Hash256 expected = new TxTrie(transactions, bufferPool: pool, canBeParallel: false).RootHash;

        Assert.That(TxTrie.CalculateRoot(transactions, canBeParallel), Is.EqualTo(expected));
    }

    [Test]
    public void Cached_encoding_survives_codec_releasing_it([Values] bool canBeParallel, [Values(0, 127)] int codecIndex)
    {
        using TestTxDecoder decoder = new();
        Transaction cached = Build.A.Transaction.TestObject;
        using OverwritingMemoryOwner owner = new(Rlp.Encode(cached, RlpBehaviors.SkipTypedWrapping).Bytes);
        cached.SetPreHashMemoryNoLock(owner.Memory, owner);
        Transaction[] transactions = new Transaction[128];
        Array.Fill(transactions, Build.A.Transaction.TestObject);
        transactions[1] = cached;
        transactions[codecIndex] = Build.A.Transaction.WithType(decoder.Type).TestObject;
        using TrackingCappedArrayPool pool = new();
        Hash256 expected = new TxTrie(transactions, bufferPool: pool, canBeParallel: false).RootHash;
        decoder.OnEncode = () => Assert.That(cached.Hash, Is.Not.Null);

        Hash256 actual = TxTrie.CalculateRoot(transactions, canBeParallel);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(owner.Disposed, Is.True);
            Assert.That(actual, Is.EqualTo(expected));
        }
    }

    [Test]
    public void Encoding_failure_returns_the_scratch_rental()
    {
        if (Nethermind.Core.Cpu.RuntimeInformation.IsSingleProcessor)
            Assert.Ignore("The single-processor path does not rent batch scratch.");

        using TestTxDecoder decoder = new();
        Transaction[] transactions = new Transaction[128];
        Array.Fill(transactions, Build.A.Transaction.WithType(decoder.Type).TestObject);
        int length = transactions.Length * decoder.GetLength(transactions[0], RlpBehaviors.SkipTypedWrapping);
        byte[] scratch = ArrayPool<byte>.Shared.Rent(length);
        ArrayPool<byte>.Shared.Return(scratch);
        int calls = 0;
        decoder.OnEncode = () =>
        {
            if (++calls == 2) throw new InvalidDataException("Encoding failed after scratch was rented.");
        };

        Assert.Throws<InvalidDataException>(() => TxTrie.CalculateRoot(transactions));

        byte[] returned = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            Assert.That(returned, Is.SameAs(scratch));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(returned);
        }
        decoder.OnEncode = null;
        using TrackingCappedArrayPool pool = new();
        Assert.That(TxTrie.CalculateRoot(transactions),
            Is.EqualTo(new TxTrie(transactions, bufferPool: pool, canBeParallel: false).RootHash));
    }

    private sealed class OverwritingMemoryOwner(byte[] bytes) : IMemoryOwner<byte>
    {
        public Memory<byte> Memory => bytes;
        public bool Disposed { get; private set; }

        public void Dispose()
        {
            Disposed = true;
            bytes.AsSpan().Fill(0xff);
        }
    }

    private sealed class TestTxDecoder : ITxDecoder, IDisposable
    {
        private readonly int _threadId = Environment.CurrentManagedThreadId;
        public TxType Type => (TxType)127;
        public Action? OnEncode { get; set; }

        public TestTxDecoder() => TxDecoder.Instance.RegisterDecoder(this);
        public void Dispose() => TxDecoder.Instance.RegisterDecoder(Type, null!);

        public void Decode(ref Transaction? transaction, int txSequenceStart, ReadOnlySpan<byte> transactionSequence,
            ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None) => throw new NotSupportedException();

        public int GetLength(Transaction transaction, RlpBehaviors rlpBehaviors, bool forSigning = false,
            bool isEip155Enabled = false, ulong chainId = 0)
        {
            Assert.That(Environment.CurrentManagedThreadId, Is.EqualTo(_threadId));
            return 3;
        }

        public void Encode<TWriter>(Transaction transaction, ref TWriter writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None,
            bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0)
            where TWriter : struct, IRlpWriteBackend, allows ref struct
        {
            Assert.That(Environment.CurrentManagedThreadId, Is.EqualTo(_threadId));
            Assert.That(rlpBehaviors.HasFlag(RlpBehaviors.SkipTypedWrapping), Is.True);
            writer.WriteByte((byte)Type);
            OnEncode?.Invoke();
            writer.StartSequence(1);
            writer.WriteByte(1);
        }
    }
}
