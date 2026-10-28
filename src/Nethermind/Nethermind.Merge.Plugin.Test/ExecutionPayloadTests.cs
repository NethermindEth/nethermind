// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class ExecutionPayloadTests
{
    private sealed class BlockConversionProbe : ExecutionPayload
    {
        public bool Converted { get; private set; }

        public override Result<Block> TryGetBlock(Nethermind.Int256.UInt256? totalDifficulty = null)
        {
            Converted = true;
            return base.TryGetBlock(totalDifficulty);
        }
    }

    /// <summary>Keeps swaps of the registry's <see cref="Transaction"/> decoder from interleaving, whatever NUnit schedules.</summary>
    private static readonly Lock DecoderSwapLock = new();

    private static TxType[] TxTypes() => [TxType.Legacy, TxType.AccessList, TxType.EIP1559, TxType.Blob];

    [Test, NonParallelizable]
    public void Payload_decoding_bypasses_transaction_factory(
        [Values(1, 64)] int count, [Values] bool malformed, [Values] bool borrowMemory)
    {
        byte[][] encoded = BuildDiverseBatch(count);
        byte[] control = EncodeTx(TxType.Legacy);
        if (malformed) encoded[^1] = [.. encoded[^1], 0xDC, 0xAF];
        int factoryCalls = 0;
        FactoryTrackingDecoder decoder = new(() =>
        {
            Interlocked.Increment(ref factoryCalls);
            return new Transaction();
        });
        using DecoderSwap _ = new(encoded, decoder);
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

    [Test, NonParallelizable]
    public void Parallel_decoding_stops_claiming_entries_after_first_failure()
    {
        const int count = 10_000;
        byte[] emptySignatureLegacyTx = Bytes.FromHexString("c9808080808080808080");
        byte[][] encoded = Enumerable.Repeat(emptySignatureLegacyTx, count).ToArray();
        FactoryTrackingDecoder decoder = new(static () => new Transaction());
        using DecoderSwap _ = new(encoded, decoder);

        TransactionDecodingResult result = TxsDecoder.DecodeTxs(encoded, skipErrors: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Error, Does.StartWith("Transaction 0 is not valid"));
            Assert.That(decoder.TrackedDecodes, Is.LessThan(count / 10), "Workers must not decode the tail after the first failure.");
        }
    }

    /// <summary>Registers an <see cref="InputScopedDecoder"/> for <paramref name="inputs"/> until disposed.</summary>
    private readonly ref struct DecoderSwap
    {
        private readonly Lock.Scope _lock;
        private readonly IRlpDecoder<Transaction> _original;

        public DecoderSwap(byte[][] inputs, IRlpDecoder<Transaction> own)
        {
            _lock = DecoderSwapLock.EnterScope();
            _original = Rlp.GetDecoder<Transaction>()!;
            Rlp.RegisterDecoder(typeof(Transaction), new InputScopedDecoder(inputs, own, _original));
        }

        public void Dispose()
        {
            Rlp.RegisterDecoder(typeof(Transaction), _original);
            _lock.Dispose();
        }
    }

    private sealed class FactoryTrackingDecoder(Func<Transaction> factory) : TxDecoder<Transaction>(factory)
    {
        private int _trackedDecodes;

        public int TrackedDecodes => Volatile.Read(ref _trackedDecodes);

        protected override Transaction? DecodeInternal(ref RlpReader reader, RlpBehaviors behaviors = RlpBehaviors.None)
        {
            Interlocked.Increment(ref _trackedDecodes);
            return base.DecodeInternal(ref reader, behaviors);
        }
    }

    /// <summary>Decodes the test's own buffers with <paramref name="own"/> and any other buffer with <paramref name="fallback"/>.</summary>
    /// <remarks>
    /// Tests register it process-wide. NUnit runs a parameterized method inline on the fixture's parallel worker, so
    /// <see cref="NonParallelizableAttribute"/> on it does not stop the fixture's other tests from decoding meanwhile
    /// (nunit/nunit#3371); they, and any background work, must keep getting the decoder that was replaced.
    /// </remarks>
    private sealed class InputScopedDecoder(byte[][] inputs, IRlpDecoder<Transaction> own, IRlpDecoder<Transaction> fallback)
        : TxDecoder<Transaction>
    {
        protected override Transaction? DecodeInternal(ref RlpReader reader, RlpBehaviors behaviors = RlpBehaviors.None)
        {
            foreach (byte[] input in inputs)
            {
                if (reader.Data.Overlaps(input)) return own.Decode(ref reader, behaviors);
            }
            return fallback.Decode(ref reader, behaviors);
        }
    }

    [Test]
    public void Input_scoped_decoder_forwards_unrelated_buffers()
    {
        byte[] tracked = EncodeTx(TxType.Legacy);
        byte[] unrelated = tracked.ToArray();
        int factoryCalls = 0;
        FactoryTrackingDecoder tracking = new(() =>
        {
            factoryCalls++;
            return new Transaction();
        });
        InputScopedDecoder decoder = new([tracked], tracking, new PayloadTestDecoder());
        RlpReader reader = new(unrelated);

        Transaction transaction = decoder.DecodeCompleteNotNull(ref reader, RlpBehaviors.SkipTypedWrapping);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(transaction.Nonce, Is.EqualTo(1000), "The fallback decoder must handle the unrelated buffer.");
            Assert.That(tracking.TrackedDecodes, Is.Zero);
            Assert.That(factoryCalls, Is.Zero);
        }
    }

    [Test, NonParallelizable]
    public void Payload_decoding_uses_registered_decoder([Values(1, 64)] int count)
    {
        byte[][] encoded = EncodeTxs(count);
        using DecoderSwap _ = new(encoded, new PayloadTestDecoder());

        Result<Transaction[]> result = new ExecutionPayload { Transactions = encoded }.TryGetTransactions();

        Assert.That(result.Error, Is.Null);
        Assert.That(result.Data!.Select(tx => tx.Nonce), Is.EqualTo(Enumerable.Range(1000, count).Select(i => (ulong)i)));
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
                Assert.That(tx.Data, Is.SequenceEqualTo(data));
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
                Assert.That(tx.Data, Is.SequenceEqualTo(data));
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

    [Test]
    public void DecodeTxs_skipping_errors_drops_undecodable_entries_in_order([Values(8, 64)] int count)
    {
        byte[][] rlps = EncodeTxs(count);
        rlps[1] = [0x01];
        rlps[count - 2] = [.. rlps[count - 2], 0xDC, 0xAF];

        TransactionDecodingResult result = TxsDecoder.DecodeTxs(rlps, skipErrors: true);

        ulong[] expected = Enumerable.Range(0, count).Where(i => i != 1 && i != count - 2).Select(i => (ulong)i).ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Error, Is.Null);
            Assert.That(result.Transactions.Select(tx => tx.Nonce), Is.EqualTo(expected));
        }
    }

    [Test]
    public void Preparation_reuses_payload_data_with_an_independent_worker_group([Values(1, 64)] int count)
    {
        byte[][] rlps = EncodeTxs(count);
        ExecutionPayload payload = new() { Transactions = rlps };
        using ExecutionPayloadPreparation first = new(payload);
        Result<Block> block = first.TryGetBlock();
        using ExecutionPayloadPreparation second = new(payload);
        Result<Block> resent = second.TryGetBlock();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(block.Data!.Header.TxRoot, Is.EqualTo(TxTrie.CalculateRoot(rlps)));
            Assert.That(second.Workers, Is.Not.SameAs(first.Workers));
            Assert.That(resent.Data!.Header.TxRoot, Is.SameAs(block.Data.Header.TxRoot));
            Assert.That(payload.TransactionsRoot, Is.SameAs(block.Data.Header.TxRoot));
        }
    }

    [Test]
    public void Preparation_recomputes_tx_root_when_transactions_change([Values] bool decoded)
    {
        byte[][] originalRlps = EncodeTxs(count: 64);
        byte[][] replacementRlps = EncodeTxs(count: 64, nonceOffset: 1000);

        ExecutionPayload payload = new() { Transactions = originalRlps };
        using ExecutionPayloadPreparation preparation = new(payload);
        if (decoded) preparation.TryGetBlock();
        payload.Transactions = replacementRlps;
        Assert.That(payload.TransactionsRoot, Is.Null);
        Result<Block> block = preparation.TryGetBlock();

        Assert.That(block.Data!.Header.TxRoot, Is.EqualTo(TxTrie.CalculateRoot(replacementRlps)));
    }

    [Test]
    public void Preparation_does_not_cache_a_root_for_invalid_transactions([Values(1, 64)] int count)
    {
        byte[][] rlps = EncodeTxs(count);
        rlps[^1] = [0x01];
        BlockConversionProbe payload = new() { Transactions = rlps };
        using (ExecutionPayloadPreparation preparation = new(payload))
        {
            Assert.That(preparation.TryGetBlock().IsError, Is.True);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(payload.TransactionsRoot, Is.Null);
            Assert.That(payload.Converted, Is.False, "a decoding failure must return before block conversion retries decoding");
        }
        payload.Transactions = EncodeTxs(count, nonceOffset: 1000);
        using ExecutionPayloadPreparation replacement = new(payload);
        Result<Block> block = replacement.TryGetBlock();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(block.IsError, Is.False);
            Assert.That(block.Data!.Header.TxRoot, Is.EqualTo(TxTrie.CalculateRoot(payload.Transactions)));
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

    private const ulong ParentGasLimit = 30_000_000;

    private const ulong Eip1559TransitionBlock = 12_965_000;

    // (30M + 30M / 1024) / 10,500; doubled by the elasticity multiplier at the EIP-1559 fork block.
    [TestCase(Eip1559TransitionBlock + 1, 2_859, true)]
    [TestCase(Eip1559TransitionBlock + 1, 2_860, false)]
    [TestCase(Eip1559TransitionBlock, 5_719, true)]
    [TestCase(Eip1559TransitionBlock, 5_720, false)]
    public void Bounds_count_by_parent_gas_limit(ulong blockNumber, int count, bool expected)
    {
        bool result = Guard(parentProcessed: true).IsWithinParentGasLimit(Payload(count, blockNumber), London.Instance, out string? error, out Hash256? latestValidHash);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(expected));
            Assert.That(error, expected ? Is.Null : Does.StartWith("Transaction count"));
            Assert.That(latestValidHash, Is.EqualTo(expected ? null : TestItem.KeccakA));
        }
    }

    [TestCase(true, true, "parent")]
    [TestCase(true, false, null)]
    [TestCase(false, true, "zero")]
    [TestCase(false, false, "zero")]
    public void Reports_latest_valid_hash_by_parent_state(bool parentPostMerge, bool parentProcessed, string? expected)
    {
        bool result = Guard(parentProcessed, parentPostMerge).IsWithinParentGasLimit(Payload(100_000), London.Instance, out _, out Hash256? latestValidHash);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.False);
            Assert.That(latestValidHash, Is.EqualTo(expected switch { "parent" => TestItem.KeccakA, "zero" => Keccak.Zero, _ => null }));
        }
    }

    private static readonly IReleaseSpec[] MinTransactionGasForks = [Frontier.Instance, London.Instance, Amsterdam.Instance, Eip8141Prototype.Instance];

    [Test]
    public void Min_transaction_gas_stays_below_every_net_base_cost([ValueSource(nameof(MinTransactionGasForks))] IReleaseSpec spec)
    {
        ulong baseCost = spec.IsEip2780Enabled ? GasCostOf.TransactionEip2780 : GasCostOf.Transaction;
        if (spec.IsEip8141Enabled) baseCost = Math.Min(baseCost, (ulong)Eip8141Constants.IntrinsicGasCost);
        ulong refund = spec.IsEip7778Enabled ? 0 : RefundHelper.CalculateClaimableRefund(baseCost, baseCost, spec);

        Assert.That(baseCost - refund, Is.GreaterThanOrEqualTo(PayloadTransactionCountGuard.MinTransactionGas),
            "A block gas charge below the guard's floor makes it reject blocks that could be valid.");
    }

    [Test]
    public void Allows_any_count_when_disabled_or_parent_unknown([Values] bool disabled)
    {
        PayloadTransactionCountGuard guard = disabled
            ? PayloadTransactionCountGuard.Disabled
            : new PayloadTransactionCountGuard(Substitute.For<IBlockTree>(), AlwaysPoS.Instance);

        Assert.That(guard.IsWithinParentGasLimit(Payload(100_000), Osaka.Instance, out _, out _), Is.True);
    }

    private static PayloadTransactionCountGuard Guard(bool parentProcessed, bool parentPostMerge = true)
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        BlockHeader parent = Build.A.BlockHeader.WithGasLimit(ParentGasLimit).TestObject;
        blockTree.FindHeader(TestItem.KeccakA, BlockTreeLookupOptions.TotalDifficultyNotNeeded | BlockTreeLookupOptions.DoNotCreateLevelIfMissing).Returns(parent);
        blockTree.WasProcessed(parent.Number, TestItem.KeccakA).Returns(parentProcessed);
        return new PayloadTransactionCountGuard(blockTree, parentPostMerge ? AlwaysPoS.Instance : NoPoS.Instance);
    }

    private static ExecutionPayload Payload(int count, ulong blockNumber = 0) =>
        new() { ParentHash = TestItem.KeccakA, BlockNumber = blockNumber, Transactions = new byte[count][] };
}
