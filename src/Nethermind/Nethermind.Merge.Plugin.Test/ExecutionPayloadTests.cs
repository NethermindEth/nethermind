// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class ExecutionPayloadTests
{
    [Test]
    public async Task Prepared_payload_matches_inline_decoding([Values(32, 129)] int count, [Values] bool malformed)
    {
        byte[][] encoded = BuildDiverseBatch(count);
        if (malformed) encoded[^1] = [.. encoded[^1], 0xDC, 0xAF];
        ExecutionPayload payload = new() { Transactions = encoded };
        Result<Block> expected = new ExecutionPayload { Transactions = encoded }.TryGetBlock();

        Task<(Result<Block> Block, Hash256? Hash)>? preparation = payload.StartBlockPreparation(null);
        Result<Transaction[]> transactions = payload.TryGetTransactions();
        (Result<Block> actual, Hash256? hash) = preparation is null ? (payload.TryGetBlock(), null) : await preparation;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual.IsError, Is.EqualTo(expected.IsError));
            Assert.That(actual.Error, Is.EqualTo(expected.Error));
            Assert.That(transactions.IsError, Is.EqualTo(malformed));
            if (!malformed)
            {
                Assert.That(actual.Data!.Header.CalculateHash(), Is.EqualTo(expected.Data!.Header.CalculateHash()));
                for (int i = 0; i < count; i++)
                    Assert.That(actual.Data.Transactions[i], Is.SameAs(transactions.Data![i]));
                if (preparation is not null) Assert.That(hash, Is.EqualTo(expected.Data.Header.CalculateHash()));
            }
        }
    }

    private static TxType[] TxTypes() => [TxType.Legacy, TxType.AccessList, TxType.EIP1559, TxType.Blob];

    [Test, NonParallelizable]
    public void Payload_decoding_bypasses_transaction_factory(
        [Values(1, 64)] int count, [Values] bool malformed, [Values] bool borrowMemory)
    {
        byte[][] encoded = BuildDiverseBatch(count);
        byte[] control = EncodeTx(TxType.Legacy);
        if (malformed) encoded[^1] = [.. encoded[^1], 0xDC, 0xAF];
        int factoryCalls = 0;
        IRlpDecoder<Transaction> original = Rlp.GetDecoder<Transaction>()!;
        FactoryTrackingDecoder decoder = new(() =>
        {
            Interlocked.Increment(ref factoryCalls);
            return new Transaction();
        }, [.. encoded, control], original);
        Rlp.RegisterDecoder(typeof(Transaction), decoder);
        try
        {
            string? error;
            Transaction[]? transactions;
            if (borrowMemory)
            {
                Result<Transaction[]> result = new ExecutionPayload { Transactions = encoded }.TryGetTransactions();
                error = result.Error;
                transactions = result.Data;
            }
            else
            {
                TransactionDecodingResult result = TxsDecoder.DecodeTxs(encoded, skipErrors: false);
                error = result.Error;
                transactions = result.Transactions;
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(error is not null, Is.EqualTo(malformed));
                Assert.That(decoder.TrackedDecodes, malformed ? Is.GreaterThanOrEqualTo(count) : Is.EqualTo(count),
                    "Payload decoding must route through the tracked decoder.");
                Assert.That(Volatile.Read(ref factoryCalls), Is.Zero, "Payload decoding must bypass the reusable transaction factory.");
                if (!malformed)
                {
                    Assert.That(transactions!, Has.Length.EqualTo(count));
                }
            }
            RlpReader reader = new(control);
            decoder.DecodeCompleteNotNull(ref reader, RlpBehaviors.SkipTypedWrapping);
            Assert.That(Volatile.Read(ref factoryCalls), Is.EqualTo(1), "Ordinary decoding must exercise the tracked factory.");
        }
        finally
        {
            Rlp.RegisterDecoder(typeof(Transaction), original);
        }
    }

    private sealed class FactoryTrackingDecoder(
        Func<Transaction> factory, byte[][] inputs, IRlpDecoder<Transaction> fallback) : TxDecoder<Transaction>(factory)
    {
        private int _trackedDecodes;

        public int TrackedDecodes => Volatile.Read(ref _trackedDecodes);

        protected override Transaction? DecodeInternal(ref RlpReader reader, RlpBehaviors behaviors = RlpBehaviors.None)
        {
            // Background work may still decode through the registry; only track this test's buffers.
            foreach (byte[] input in inputs)
            {
                if (reader.Data.Overlaps(input))
                {
                    Interlocked.Increment(ref _trackedDecodes);
                    return base.DecodeInternal(ref reader, behaviors);
                }
            }
            return fallback.Decode(ref reader, behaviors);
        }
    }

    [Test]
    public void Factory_tracking_forwards_unrelated_buffers()
    {
        byte[] tracked = EncodeTx(TxType.Legacy);
        byte[] unrelated = tracked.ToArray();
        int factoryCalls = 0;
        FactoryTrackingDecoder decoder = new(() =>
        {
            factoryCalls++;
            return new Transaction();
        }, [tracked], new PayloadTestDecoder());
        RlpReader reader = new(unrelated);

        Transaction transaction = decoder.DecodeCompleteNotNull(ref reader, RlpBehaviors.SkipTypedWrapping);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(transaction.Nonce, Is.EqualTo(1000), "The fallback decoder must handle the unrelated buffer.");
            Assert.That(decoder.TrackedDecodes, Is.Zero);
            Assert.That(factoryCalls, Is.Zero);
        }
    }

    [Test, NonParallelizable]
    public void Payload_decoding_uses_registered_decoder([Values(1, 64)] int count)
    {
        byte[][] encoded = EncodeTxs(count);
        IRlpDecoder<Transaction> original = Rlp.GetDecoder<Transaction>()!;
        Rlp.RegisterDecoder(typeof(Transaction), new PayloadTestDecoder());
        try
        {
            Result<Transaction[]> result = new ExecutionPayload { Transactions = encoded }.TryGetTransactions();
            Assert.That(result.Error, Is.Null);
            Assert.That(result.Data!.Select(tx => tx.Nonce), Is.EqualTo(Enumerable.Range(1000, count).Select(i => (ulong)i)));
        }
        finally
        {
            Rlp.RegisterDecoder(typeof(Transaction), original);
        }
    }

    private sealed class PayloadTestDecoder : TxDecoder<Transaction>
    {
        protected override Transaction? DecodeInternal(ref RlpReader reader, RlpBehaviors behaviors = RlpBehaviors.None)
        {
            Transaction? tx = base.DecodeInternal(ref reader, behaviors);
            if (tx is not null) tx.Nonce += 1000;
            return tx;
        }
    }

    [Test]
    public void Payload_decoding_borrows_data_and_preserves_hash_after_replacement(
        [ValueSource(nameof(TxTypes))] TxType type,
        [Values(68, 8192, 65536)] int dataLength,
        [Values(1, 64)] int count)
    {
        byte[] data = new byte[dataLength];
        data.AsSpan().Fill(0x42);
        byte[][] encoded = Enumerable.Range(0, count).Select(i => EncodeTx(type, nonce: (ulong)i, data: data)).ToArray();
        Hash256[] expectedHashes = encoded.Select(bytes => Keccak.Compute(bytes)).ToArray();
        ExecutionPayload payload = new() { Transactions = encoded };

        Result<Transaction[]> result = payload.TryGetTransactions();
        Assert.That(result.Error, Is.Null);
        payload.Transactions = [];

        for (int i = 0; i < count; i++)
        {
            Transaction tx = result.Data![i];
            Assert.That(MemoryMarshal.TryGetArray(tx.Data, out ArraySegment<byte> segment), Is.True);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(segment.Array, Is.SameAs(encoded[i]));
                Assert.That(tx.Nonce, Is.EqualTo((ulong)i));
                Assert.That(tx.Data.ToArray(), Is.EqualTo(data));
                Assert.That(tx.Hash, Is.EqualTo(expectedHashes[i]));
                Assert.That(TxDecoder.Instance.Encode(tx, RlpBehaviors.SkipTypedWrapping).Bytes, Is.EqualTo(encoded[i]));
            }
        }
    }

    [Test]
    public void Public_decoder_keeps_data_and_lazy_hash_independent_of_input(
        [ValueSource(nameof(TxTypes))] TxType type, [Values(1, 64)] int count)
    {
        byte[] data = [1, 2, 3, 4];
        byte[] encoded = EncodeTx(type, data: data);
        Hash256 expectedHash = Keccak.Compute(encoded);
        TransactionDecodingResult result = TxsDecoder.DecodeTxs(Enumerable.Repeat(encoded, count).ToArray(), skipErrors: false);
        Assert.That(result.Error, Is.Null);
        encoded.AsSpan().Clear();

        foreach (Transaction tx in result.Transactions)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(tx.Data.ToArray(), Is.EqualTo(data));
                Assert.That(tx.Hash, Is.EqualTo(expectedHash));
            }
        }
    }

    [TestCaseSource(nameof(TxTypes))]
    public void TryGetTransactions_accepts_clean_typed_tx(TxType txType)
    {
        byte[] validRlp = EncodeTx(txType);

        ExecutionPayload payload = new() { Transactions = [validRlp] };
        Result<Transaction[]> result = payload.TryGetTransactions();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Error, Is.Null);
            Assert.That(result.Data, Has.Length.EqualTo(1));
        }
    }

    // Most other clients reject transactions with garbage trailing bytes
    [TestCaseSource(nameof(TxTypes))]
    public void TryGetTransactions_rejects_typed_tx_with_trailing_bytes(TxType txType)
    {
        byte[] validRlp = EncodeTx(txType);
        byte[] garbage = [0xDC, 0xAF, 0xAE, 0x1F];
        byte[] mutated = [.. validRlp, .. garbage];

        ExecutionPayload payload = new() { Transactions = [EncodeTx(txType), mutated, EncodeTx(txType)] };
        Result<Transaction[]> result = payload.TryGetTransactions();

        Assert.That(result.Error, Contains.Substring("checkpoint failed"));
    }

    // Above the parallel-decoding threshold all txs must decode in payload order
    [Test]
    public void TryGetTransactions_decodes_many_txs_in_order()
    {
        const int count = 64;
        byte[][] rlps = EncodeTxs(count);

        ExecutionPayload payload = new() { Transactions = rlps };
        Result<Transaction[]> result = payload.TryGetTransactions();

        Assert.That(result.Error, Is.Null);
        Assert.That(result.Data, Has.Length.EqualTo(count));
        for (int i = 0; i < count; i++)
        {
            Assert.That(result.Data[i].Nonce, Is.EqualTo((ulong)i));
        }
    }

    // The serial fallback must still pinpoint the first invalid tx above the parallel threshold
    [Test]
    public void TryGetTransactions_reports_exact_invalid_tx_above_parallel_threshold()
    {
        const int count = 64;
        const int invalidIndex = 41;
        byte[][] rlps = EncodeTxs(count);
        rlps[invalidIndex] = [.. rlps[invalidIndex], 0xDC, 0xAF];

        ExecutionPayload payload = new() { Transactions = rlps };
        Result<Transaction[]> result = payload.TryGetTransactions();

        Assert.That(result.Error, Contains.Substring($"Transaction {invalidIndex}"));
    }

    // The early-started root task must be the one TryGetBlock consumes, with an identical root
    [Test]
    public void TryGetBlock_uses_early_started_tx_root_computation()
    {
        byte[][] rlps = EncodeTxs(count: 64);

        ExecutionPayload payload = new() { Transactions = rlps };
        Task<Hash256>? rootTask = payload.StartTxRootComputation();
        Result<Block> block = payload.TryGetBlock();

        using (Assert.EnterMultipleScope())
        {
            // A single processor computes the root inline instead of starting the task.
            Assert.That(rootTask, Nethermind.Core.Cpu.RuntimeInformation.IsSingleProcessor ? Is.Null : Is.Not.Null);
            Assert.That(block.Data!.Header.TxRoot, Is.EqualTo(TxTrie.CalculateRoot(rlps)));
        }
    }

    // A root task started for one transaction set must never produce the root of a mutated payload
    [Test]
    public async Task TryGetBlock_recomputes_tx_root_when_transactions_change_after_early_start([Values] bool prepareBlock)
    {
        byte[][] originalRlps = EncodeTxs(count: 64);
        byte[][] replacementRlps = EncodeTxs(count: 64, nonceOffset: 1000);

        ExecutionPayload payload = new() { Transactions = originalRlps };
        if (prepareBlock)
        {
            if (payload.StartBlockPreparation(null) is { } preparation) await preparation;
        }
        else
        {
            _ = payload.StartTxRootComputation();
        }
        payload.Transactions = replacementRlps;
        Result<Block> block = payload.TryGetBlock();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(block.Data!.Header.TxRoot, Is.EqualTo(TxTrie.CalculateRoot(replacementRlps)));
            Assert.That(block.Data.Transactions[0].Nonce, Is.EqualTo(1000));
        }
    }

    // Below the background threshold the root is still computed, just inline
    [Test]
    public void TryGetBlock_computes_tx_root_inline_below_background_threshold()
    {
        byte[][] rlps = EncodeTxs(count: 1);

        ExecutionPayload payload = new() { Transactions = rlps };
        Task<Hash256>? rootTask = payload.StartTxRootComputation();
        Result<Block> block = payload.TryGetBlock();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rootTask, Is.Null);
            Assert.That(block.Data!.Header.TxRoot, Is.EqualTo(TxTrie.CalculateRoot(rlps)));
        }
    }

    private static byte[][] EncodeTxs(int count, ulong nonceOffset = 0)
    {
        byte[][] rlps = new byte[count][];
        for (int i = 0; i < count; i++) rlps[i] = EncodeTx(TxType.EIP1559, nonce: nonceOffset + (ulong)i);
        return rlps;
    }

    private static byte[] EncodeTx(TxType txType, ulong nonce = 0, byte[]? data = null)
    {
        TransactionBuilder<Transaction> builder = Build.A.Transaction
            .WithType(txType)
            .WithNonce(nonce)
            .WithData(data ?? [])
            .WithChainId(TestBlockchainIds.ChainId);

        builder = txType switch
        {
            TxType.AccessList => builder.WithAccessList(Build.A.AccessList.TestObject),
            TxType.EIP1559 => builder.WithMaxFeePerGas(50).WithMaxPriorityFeePerGas(10),
            TxType.Blob => builder.WithBlobVersionedHashes(1).WithMaxFeePerBlobGas(10),
            _ => builder
        };

        Transaction tx = builder.SignedAndResolved().TestObject;
        return TxDecoder.Instance.Encode(tx, RlpBehaviors.SkipTypedWrapping).Bytes;
    }

    private static byte[][] BuildDiverseBatch(int size)
    {
        TxType[] cycle = [TxType.Legacy, TxType.AccessList, TxType.EIP1559, TxType.Blob];
        List<byte[]> bytes = new(size);
        for (int i = 0; i < size; i++)
        {
            TransactionBuilder<Transaction> builder = Build.A.Transaction
                .WithType(cycle[i % cycle.Length])
                .WithChainId(TestBlockchainIds.ChainId)
                .WithNonce((ulong)i)
                .WithValue((ulong)(i * 13 + 1));

            builder = cycle[i % cycle.Length] switch
            {
                TxType.AccessList => builder.WithAccessList(Build.A.AccessList.TestObject),
                TxType.EIP1559 => builder.WithMaxFeePerGas(50).WithMaxPriorityFeePerGas(10),
                TxType.Blob => builder.WithBlobVersionedHashes(1).WithMaxFeePerBlobGas(10),
                _ => builder
            };

            Transaction tx = builder.SignedAndResolved(TestItem.PrivateKeys[i % TestItem.PrivateKeys.Length]).TestObject;
            bytes.Add(TxDecoder.Instance.Encode(tx, RlpBehaviors.SkipTypedWrapping).Bytes);
        }
        return [.. bytes];
    }

    [TestCase(1)]
    [TestCase(8)]    // below TxsDecoder.ParallelDecodeThreshold (= 32) — serial path
    [TestCase(31)]   // boundary, still serial
    [TestCase(32)]   // boundary, first parallel
    [TestCase(64)]
    [TestCase(256)]
    public void TryGetTransactions_decodes_mixed_batch_at_size(int size)
    {
        byte[][] encoded = BuildDiverseBatch(size);
        ExecutionPayload payload = new() { Transactions = encoded };

        Result<Transaction[]> result = payload.TryGetTransactions();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Error, Is.Null);
            Assert.That(result.Data, Is.Not.Null);
        }

        Assert.That(result.Data!, Has.Length.EqualTo(size));

        // Re-encoding catches any field-mixup a thread-unsafe decoder could produce.
        for (int i = 0; i < size; i++)
        {
            byte[] roundTripped = TxDecoder.Instance.Encode(result.Data![i], RlpBehaviors.SkipTypedWrapping).Bytes;
            Assert.That(roundTripped, Is.EqualTo(encoded[i]), $"Mismatch at index {i}");
        }
    }

    [Test]
    public void TryGetTransactions_parallel_path_matches_serial_path()
    {
        // 32 txs is at the threshold, so the parallel branch fires.
        byte[][] encoded = BuildDiverseBatch(32);

        Result<Transaction[]> parallelResult = new ExecutionPayload { Transactions = encoded }.TryGetTransactions();
        Assert.That(parallelResult.Error, Is.Null);

        // Decode each independently in a tight serial loop as the oracle.
        Transaction[] serialOracle = new Transaction[encoded.Length];
        for (int i = 0; i < encoded.Length; i++)
        {
            RlpReader ctx = new(encoded[i]);
            serialOracle[i] = Rlp.GetDecoder<Transaction>()!.DecodeCompleteNotNull(ref ctx, RlpBehaviors.SkipTypedWrapping);
        }

        Assert.That(parallelResult.Data!.Length, Is.EqualTo(serialOracle.Length));
        for (int i = 0; i < serialOracle.Length; i++)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(parallelResult.Data[i].Hash, Is.EqualTo(serialOracle[i].Hash), $"Hash mismatch at index {i}");
                Assert.That(parallelResult.Data[i].Nonce, Is.EqualTo(serialOracle[i].Nonce), $"Nonce mismatch at index {i}");
                Assert.That(parallelResult.Data[i].Value, Is.EqualTo(serialOracle[i].Value), $"Value mismatch at index {i}");
                Assert.That(parallelResult.Data[i].Type, Is.EqualTo(serialOracle[i].Type), $"Type mismatch at index {i}");
            }
        }
    }

    [Test]
    public void TryGetTransactions_parallel_path_is_stable_across_repeated_runs()
    {
        byte[][] encoded = BuildDiverseBatch(64);

        Transaction[] reference = new ExecutionPayload { Transactions = encoded }.TryGetTransactions().Data!;
        Hash256[] referenceHashes = reference.Select(t => t.Hash!).ToArray();

        // Repeat many times — any thread-safety bug in the decoder will surface as flakiness.
        for (int iter = 0; iter < 50; iter++)
        {
            Transaction[] decoded = new ExecutionPayload { Transactions = encoded }.TryGetTransactions().Data!;
            Assert.That(decoded, Has.Length.EqualTo(reference.Length), $"Length drift on iteration {iter}");
            for (int i = 0; i < reference.Length; i++)
            {
                Assert.That(decoded[i].Hash, Is.EqualTo(referenceHashes[i]), $"Hash drift at index {i} on iteration {iter}");
            }
        }
    }

    [Test]
    public async Task TryGetTransactions_parallel_path_safe_under_concurrent_callers()
    {
        // Catches static-shared-state bugs in the per-tx-type decoders that a single call would not surface.
        byte[][] encoded = BuildDiverseBatch(32);
        Hash256[] referenceHashes = new ExecutionPayload { Transactions = encoded }
            .TryGetTransactions().Data!.Select(t => t.Hash!).ToArray();

        Task[] workers = [.. Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (int iter = 0; iter < 20; iter++)
            {
                Transaction[] decoded = new ExecutionPayload { Transactions = encoded }.TryGetTransactions().Data!;
                for (int i = 0; i < referenceHashes.Length; i++)
                {
                    Assert.That(decoded[i].Hash, Is.EqualTo(referenceHashes[i]), $"Hash drift at index {i} on iteration {iter}");
                }
            }
        }))];

        await Task.WhenAll(workers);
    }
}
