// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public class TransactionTests
{
    [Test]
    public void When_to_not_empty_then_is_message_call()
    {
        Transaction transaction = new();
        transaction.To = Address.Zero;
        Assert.That(transaction.IsMessageCall, Is.True, nameof(Transaction.IsMessageCall));
        Assert.That(transaction.IsContractCreation, Is.False, nameof(Transaction.IsContractCreation));
    }

    [Test]
    public void When_to_empty_then_is_message_call()
    {
        Transaction transaction = new();
        transaction.To = null;
        Assert.That(transaction.IsMessageCall, Is.False, nameof(Transaction.IsMessageCall));
        Assert.That(transaction.IsContractCreation, Is.True, nameof(Transaction.IsContractCreation));
    }

    [TestCase(1, true)]
    [TestCase(300, true)]
    public void Supports1559_returns_expected_results(int decodedFeeCap, bool expectedSupports1559)
    {
        Transaction transaction = new();
        transaction.DecodedMaxFeePerGas = (uint)decodedFeeCap;
        transaction.Type = TxType.EIP1559;
        Assert.That(transaction.DecodedMaxFeePerGas, Is.EqualTo(transaction.MaxFeePerGas));
        Assert.That(transaction.Supports1559, Is.EqualTo(expectedSupports1559));
    }

    [Test]
    public void Equals_and_hash_code_ignore_mutable_pool_execution_fields()
    {
        Transaction tx = new()
        {
            ChainId = 1,
            Type = TxType.EIP1559,
            Nonce = 2,
            GasPrice = 3,
            DecodedMaxFeePerGas = 4,
            GasLimit = 5,
            To = TestItem.AddressA,
            Value = 6,
            Data = new byte[] { 1, 2, 3 },
            SenderAddress = TestItem.AddressB,
            GasBottleneck = 7,
            SpentGas = 8,
            BlockGasUsed = 9,
            PoolIndex = 10,
            Timestamp = 11,
        };

        Transaction sameTxAtDifferentProcessingStage = new()
        {
            ChainId = tx.ChainId,
            Type = tx.Type,
            Nonce = tx.Nonce,
            GasPrice = tx.GasPrice,
            DecodedMaxFeePerGas = tx.DecodedMaxFeePerGas,
            GasLimit = tx.GasLimit,
            To = tx.To,
            Value = tx.Value,
            Data = new byte[] { 1, 2, 3 },
            SenderAddress = TestItem.AddressC,
            GasBottleneck = 12,
            SpentGas = 13,
            BlockGasUsed = 14,
            PoolIndex = 15,
            Timestamp = 16,
        };

        Assert.Multiple(() =>
        {
            Assert.That(sameTxAtDifferentProcessingStage, Is.EqualTo(tx));
            Assert.That(sameTxAtDifferentProcessingStage.GetHashCode(), Is.EqualTo(tx.GetHashCode()));
        });
    }
}
