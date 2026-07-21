// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;
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
            Assert.That(rootTask, Is.Not.Null);
            Assert.That(block.Data!.Header.TxRoot, Is.EqualTo(TxTrie.CalculateRoot(rlps)));
        }
    }

    // A root task started for one transaction set must never produce the root of a mutated payload
    [Test]
    public void TryGetBlock_recomputes_tx_root_when_transactions_change_after_early_start()
    {
        byte[][] originalRlps = EncodeTxs(count: 64);
        byte[][] replacementRlps = EncodeTxs(count: 64, nonceOffset: 1000);

        ExecutionPayload payload = new() { Transactions = originalRlps };
        payload.StartTxRootComputation();
        payload.Transactions = replacementRlps;
        Result<Block> block = payload.TryGetBlock();

        Assert.That(block.Data!.Header.TxRoot, Is.EqualTo(TxTrie.CalculateRoot(replacementRlps)));
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
}
