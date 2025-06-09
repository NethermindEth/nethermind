// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core.Extensions;
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
}

public static class TransactionTestExtensions
{
    public static void EqualToTransaction(this Transaction subject, Transaction expectation)
    {
        subject.Should().BeEquivalentTo(
            expectation,
            static o => o
                .ComparingByMembers<Transaction>()
                .Using<ReadOnlyMemory<byte>>(static ctx => ctx.Subject.AsArray().Should().BeEquivalentTo(ctx.Expectation.AsArray()))
                .WhenTypeIs<ReadOnlyMemory<byte>>()
            );
    }
}
