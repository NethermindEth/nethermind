// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class ExecutionPayloadTests
{
    private static TxType[] TxTypes() => [TxType.AccessList, TxType.EIP1559, TxType.Blob];

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
        byte[][] rlps = new byte[count][];
        for (int i = 0; i < count; i++) rlps[i] = EncodeTx(TxType.EIP1559, nonce: (ulong)i);

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
        byte[][] rlps = new byte[count][];
        for (int i = 0; i < count; i++) rlps[i] = EncodeTx(TxType.EIP1559, nonce: (ulong)i);
        rlps[invalidIndex] = [.. rlps[invalidIndex], 0xDC, 0xAF];

        ExecutionPayload payload = new() { Transactions = rlps };
        Result<Transaction[]> result = payload.TryGetTransactions();

        Assert.That(result.Error, Contains.Substring($"Transaction {invalidIndex}"));
    }

    private static byte[] EncodeTx(TxType txType, ulong nonce = 0)
    {
        TransactionBuilder<Transaction> builder = Build.A.Transaction
            .WithType(txType)
            .WithNonce(nonce)
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

    // Exercises the parallel branch of TxsDecoder (threshold = 16) that TryGetTransactions now
    // routes through. Verifies it produces identical results to the serial branch
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
    [TestCase(8)]    // below TxsDecoder.ParallelDecodeThreshold (= 16) — serial path
    [TestCase(15)]   // boundary, still serial
    [TestCase(16)]   // boundary, first parallel
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

        // Re-encode each decoded tx and compare to the original bytes. Catches any
        // field-mixup that a thread-unsafe decoder could produce.
        for (int i = 0; i < size; i++)
        {
            byte[] roundTripped = TxDecoder.Instance.Encode(result.Data![i], RlpBehaviors.SkipTypedWrapping).Bytes;
            Assert.That(roundTripped, Is.EqualTo(encoded[i]), $"Mismatch at index {i}");
        }
    }

    [Test]
    public void TryGetTransactions_parallel_path_matches_serial_path()
    {
        // 32 txs guarantees the parallel branch fires.
        byte[][] encoded = BuildDiverseBatch(32);

        // Decode via the deduped TryGetTransactions (which routes through TxsDecoder → parallel).
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

        // Reference run.
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
        // Stress: multiple threads simultaneously decoding the same payload. Catches static-shared-state
        // bugs in the per-tx-type decoders that wouldn't surface in single-call parallel.
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
