// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
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
        byte[][] rlps = new byte[count][];
        for (int i = 0; i < count; i++) rlps[i] = EncodeTx(TxType.EIP1559, nonce: (ulong)i);
        rlps[41] = [.. rlps[41], 0xDC, 0xAF];

        ExecutionPayload payload = new() { Transactions = rlps };
        Result<Transaction[]> result = payload.TryGetTransactions();

        Assert.That(result.Error, Contains.Substring("Transaction 41"));
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
