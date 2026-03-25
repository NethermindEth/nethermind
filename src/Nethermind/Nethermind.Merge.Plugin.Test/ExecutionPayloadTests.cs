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
    private static TxType[] TxTypes() => [TxType.AccessList, TxType.EIP1559, TxType.Blob, TxType.AccessList];

    [TestCaseSource(nameof(TxTypes))]
    public void TryGetTransactions_accepts_clean_typed_tx(TxType txType)
    {
        byte[] validRlp = EncodeTx(txType);

        ExecutionPayload payload = new() { Transactions = [validRlp] };
        TransactionDecodingResult result = payload.TryGetTransactions();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Error, Is.Null);
            Assert.That(result.Transactions, Has.Length.EqualTo(1));
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
        TransactionDecodingResult result = payload.TryGetTransactions();

        Assert.That(result.Error, Contains.Substring("checkpoint failed"));
    }

    private static byte[] EncodeTx(TxType txType)
    {
        TransactionBuilder<Transaction> builder = Build.A.Transaction
            .WithType(txType)
            .WithChainId(TestBlockchainIds.ChainId);

        builder = txType switch
        {
            TxType.EIP1559 => builder.WithMaxFeePerGas(50).WithMaxPriorityFeePerGas(10),
            TxType.Blob => builder.WithBlobVersionedHashes(1).WithMaxFeePerBlobGas(10),
            TxType.AccessList => builder.WithAccessList(Build.A.AccessList.TestObject),
            _ => builder
        };

        Transaction tx = builder.SignedAndResolved().TestObject;
        return TxDecoder.Instance.Encode(tx, RlpBehaviors.SkipTypedWrapping).Bytes;
    }
}
